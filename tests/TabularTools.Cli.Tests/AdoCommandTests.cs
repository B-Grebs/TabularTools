using System.Net;
using System.Text;
using System.Text.Json;
using TabularTools.Cli.Ado;

namespace TabularTools.Cli.Tests;

public class AdoCommandTests
{
    private static readonly Dictionary<string, string> PrEnvironment = new()
    {
        ["SYSTEM_COLLECTIONURI"] = "https://dev.azure.com/example-org/",
        ["SYSTEM_TEAMPROJECT"] = "Example Analytics",
        ["BUILD_REPOSITORY_ID"] = "00000000-0000-0000-0000-000000000000",
        ["SYSTEM_PULLREQUEST_PULLREQUESTID"] = "42",
        ["SYSTEM_ACCESSTOKEN"] = "fake-token",
        ["BUILD_BUILDID"] = "7",
    };

    private sealed class FakeCommenter : IPullRequestCommenter
    {
        public List<(string Marker, string Content, string Status)> Calls { get; } = [];

        public Task<CommentOutcome> UpsertAsync(string marker, string content, string status, CancellationToken cancellationToken = default)
        {
            Calls.Add((marker, content, status));
            return Task.FromResult(new CommentOutcome("created", 5, 1));
        }
    }

    private static (int Exit, string Err) Run(IReadOnlyDictionary<string, string> env, FakeCommenter? commenter, params string[] args)
    {
        var stderr = new StringWriter();
        var context = new CliContext(new StringWriter(), stderr, false, TestPaths.RepoRoot)
        {
            Environment = name => env.GetValueOrDefault(name),
            CommenterFactory = commenter is null ? null : _ => commenter,
        };
        return (CliApp.Run(args, context), stderr.ToString());
    }

    [Fact]
    public void Posts_the_file_with_its_marker_in_a_pull_request_build()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("review.md"), "### Review\n");
        var commenter = new FakeCommenter();

        var (exit, err) = Run(PrEnvironment, commenter, "ado", "comment", "--file", temp.Combine("review.md"), "--marker", "review", "--status", "closed");

        Assert.Equal(ExitCodes.Success, exit);
        var call = Assert.Single(commenter.Calls);
        Assert.Equal("review", call.Marker);
        Assert.StartsWith("<!-- tabulartools:review -->\n### Review", call.Content);
        Assert.Equal("closed", call.Status);
        Assert.Contains("pull request 42", err);
    }

    [Fact]
    public void Outside_a_pull_request_build_nothing_is_posted()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("review.md"), "x");
        var commenter = new FakeCommenter();
        var (exit, err) = Run(new Dictionary<string, string>(), commenter, "ado", "comment", "--file", temp.Combine("review.md"));
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Empty(commenter.Calls);
        Assert.Contains("Not a pull request build", err);
    }

    [Fact]
    public void A_missing_access_token_is_an_environment_error()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("review.md"), "x");
        var env = new Dictionary<string, string>(PrEnvironment);
        env.Remove("SYSTEM_ACCESSTOKEN");
        var (exit, err) = Run(env, new FakeCommenter(), "ado", "comment", "--file", temp.Combine("review.md"));
        Assert.Equal(ExitCodes.EnvironmentError, exit);
        Assert.Contains("SYSTEM_ACCESSTOKEN", err);
    }

    [Fact]
    public void Missing_file_and_bad_marker_are_usage_errors()
    {
        Assert.Equal(ExitCodes.UsageError, Run(PrEnvironment, new FakeCommenter(), "ado", "comment", "--file", "nope.md").Exit);
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("review.md"), "x");
        Assert.Equal(ExitCodes.UsageError, Run(PrEnvironment, new FakeCommenter(), "ado", "comment", "--file", temp.Combine("review.md"), "--marker", "a b -->").Exit);
    }

    [Fact]
    public void Long_reports_are_truncated_with_a_link_to_the_artifacts()
    {
        var body = "<!-- tabulartools:review -->\n<details><summary>x</summary>\n\n```diff\n" + string.Concat(Enumerable.Repeat("+line\n", 1000));
        var prepared = Ado.Rendering.Prepare(body, "review", "https://example.invalid/artifacts", maxLength: 2000);

        Assert.True(prepared.Length <= 2000);
        Assert.StartsWith("<!-- tabulartools:review -->", prepared);
        Assert.Contains("Report truncated", prepared);
        Assert.Contains("(https://example.invalid/artifacts)", prepared);
        Assert.Equal(0, CountOf(prepared, "```") % 2);
        Assert.Equal(CountOf(prepared, "<details>"), CountOf(prepared, "</details>"));
    }

    private static int CountOf(string text, string value) => (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;

    // ---- REST client against a fake Azure DevOps ----

    private sealed class FakeAdo(string threadsJson) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body, string? Auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, body, request.Headers.Authorization?.ToString()));
            var json = request.Method == HttpMethod.Get ? threadsJson
                : request.Method == HttpMethod.Post ? """{"id": 77, "comments": [{"id": 1}]}"""
                : """{"id": 3}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static AdoContext Context => AdoContext.FromEnvironment(PrEnvironment.GetValueOrDefault)!;

    [Fact]
    public async Task Creates_a_thread_when_no_comment_has_the_marker()
    {
        var fake = new FakeAdo("""{"value": [{"id": 9, "comments": [{"id": 1, "content": "LGTM"}]}], "count": 1}""");
        var commenter = new AdoPullRequestCommenter(new HttpClient(fake), Context);

        var outcome = await commenter.UpsertAsync("review", "<!-- tabulartools:review -->\nhello", "active", TestContext.Current.CancellationToken);

        Assert.Equal(new CommentOutcome("created", 77, 1), outcome);
        Assert.Equal(2, fake.Requests.Count);
        var (method, url, body, auth) = fake.Requests[1];
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("https://dev.azure.com/example-org/Example%20Analytics/_apis/git/repositories/00000000-0000-0000-0000-000000000000/pullRequests/42/threads?api-version=7.1", url);
        Assert.Equal("Bearer fake-token", auth);
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("active", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("comments")[0].GetProperty("commentType").GetInt32());
        Assert.Contains("hello", doc.RootElement.GetProperty("comments")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Updates_the_first_comment_of_the_thread_that_has_the_marker()
    {
        var fake = new FakeAdo("""
            {"value": [
              {"id": 9, "isDeleted": true, "comments": [{"id": 1, "content": "<!-- tabulartools:review --> old, deleted thread"}]},
              {"id": 12, "comments": [{"id": 2, "content": "reply"}, {"id": 1, "content": "<!-- tabulartools:review -->\nprevious"}]},
              {"id": 13, "comments": [{"id": 1, "content": "<!-- tabulartools:lint -->"}]}
            ]}
            """);
        var commenter = new AdoPullRequestCommenter(new HttpClient(fake), Context);

        var outcome = await commenter.UpsertAsync("review", "<!-- tabulartools:review -->\nnew", "active", TestContext.Current.CancellationToken);

        Assert.Equal(new CommentOutcome("updated", 12, 1), outcome);
        var (method, url, body, _) = fake.Requests[1];
        Assert.Equal(HttpMethod.Patch, method);
        Assert.EndsWith("/pullRequests/42/threads/12/comments/1?api-version=7.1", url);
        Assert.Contains("new", JsonDocument.Parse(body!).RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Http_failures_explain_the_permission_needed()
    {
        var handler = new StatusHandler(HttpStatusCode.Forbidden);
        var commenter = new AdoPullRequestCommenter(new HttpClient(handler), Context);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => commenter.UpsertAsync("review", "x", "active", TestContext.Current.CancellationToken));
        Assert.Contains("403", ex.Message);
        Assert.Contains("Contribute to pull requests", ex.Message);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("denied") });
    }
}
