using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TabularTools.Cli.Ado;

/// <summary>What happened to the PR comment.</summary>
public sealed record CommentOutcome(string Action, int ThreadId, int CommentId);

/// <summary>Creates or updates one PR comment thread per marker.</summary>
public interface IPullRequestCommenter
{
    /// <param name="marker">Key of the <c>&lt;!-- tabulartools:marker --&gt;</c> comment that identifies the thread.</param>
    /// <param name="content">Markdown body; must already contain the marker.</param>
    /// <param name="status">Thread status for new threads: active, closed, ...</param>
    Task<CommentOutcome> UpsertAsync(string marker, string content, string status, CancellationToken cancellationToken = default);
}

/// <summary>The pull request a pipeline run belongs to, from Azure Pipelines' predefined variables.</summary>
public sealed record AdoContext(Uri CollectionUri, string Project, string RepositoryId, int PullRequestId, string AccessToken, string? BuildId)
{
    public const string PullRequestIdVariable = "SYSTEM_PULLREQUEST_PULLREQUESTID";

    /// <summary>Reads the context. Returns null when the run isn't for a pull request; throws when a required variable is missing.</summary>
    public static AdoContext? FromEnvironment(Func<string, string?> env)
    {
        var prText = env(PullRequestIdVariable);
        if (string.IsNullOrWhiteSpace(prText)) return null;
        if (!int.TryParse(prText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pr))
            throw new InvalidOperationException($"{PullRequestIdVariable} is not a number: '{prText}'.");

        string Required(string name, string hint) =>
            env(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} is not set. {hint}");

        var collection = Required("SYSTEM_COLLECTIONURI", "Run this inside an Azure Pipelines job.");
        if (!collection.EndsWith('/')) collection += "/";
        return new AdoContext(
            new Uri(collection, UriKind.Absolute),
            Required("SYSTEM_TEAMPROJECT", "Run this inside an Azure Pipelines job."),
            Required("BUILD_REPOSITORY_ID", "Run this inside an Azure Pipelines job."),
            pr,
            Required("SYSTEM_ACCESSTOKEN", "Map it in the step: env: { SYSTEM_ACCESSTOKEN: $(System.AccessToken) }."),
            env("BUILD_BUILDID"));
    }

    /// <summary>Link to the run's published artifacts, for truncated comments.</summary>
    public string? ArtifactsUrl => BuildId is null ? null
        : new Uri(CollectionUri, $"{Uri.EscapeDataString(Project)}/_build/results?buildId={Uri.EscapeDataString(BuildId)}&view=artifacts").AbsoluteUri;
}

/// <summary>
/// <see cref="IPullRequestCommenter"/> over the Azure DevOps REST API (api-version 7.1). Finds the thread whose first comment
/// contains the marker and updates that comment, or creates a new thread.
/// </summary>
public sealed class AdoPullRequestCommenter(HttpClient http, AdoContext context) : IPullRequestCommenter
{
    public const string ApiVersion = "7.1";

    private string ThreadsUrl =>
        new Uri(context.CollectionUri,
            $"{Uri.EscapeDataString(context.Project)}/_apis/git/repositories/{Uri.EscapeDataString(context.RepositoryId)}/pullRequests/{context.PullRequestId}/threads").AbsoluteUri;

    public async Task<CommentOutcome> UpsertAsync(string marker, string content, string status, CancellationToken cancellationToken = default)
    {
        var markerText = Rendering.MarkerText(marker);
        var existing = await FindThreadAsync(markerText, cancellationToken).ConfigureAwait(false);
        if (existing is { } found)
        {
            using var patch = Request(HttpMethod.Patch, $"{ThreadsUrl}/{found.ThreadId}/comments/{found.CommentId}?api-version={ApiVersion}",
                new JsonObject { ["content"] = content });
            await SendAsync(patch, cancellationToken).ConfigureAwait(false);
            return new CommentOutcome("updated", found.ThreadId, found.CommentId);
        }

        var body = new JsonObject
        {
            ["comments"] = new JsonArray(new JsonObject { ["parentCommentId"] = 0, ["content"] = content, ["commentType"] = 1 }),
            ["status"] = status,
        };
        using var post = Request(HttpMethod.Post, $"{ThreadsUrl}?api-version={ApiVersion}", body);
        using var created = await SendAsync(post, cancellationToken).ConfigureAwait(false);
        var thread = JsonNode.Parse(created)!;
        var threadId = thread["id"]?.GetValue<int>() ?? 0;
        var commentId = thread["comments"]?.AsArray().FirstOrDefault()?["id"]?.GetValue<int>() ?? 1;
        return new CommentOutcome("created", threadId, commentId);
    }

    private async Task<(int ThreadId, int CommentId)?> FindThreadAsync(string markerText, CancellationToken cancellationToken)
    {
        using var get = Request(HttpMethod.Get, $"{ThreadsUrl}?api-version={ApiVersion}", null);
        using var json = await SendAsync(get, cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(json);
        foreach (var thread in root?["value"]?.AsArray() ?? [])
        {
            if (thread?["isDeleted"]?.GetValue<bool>() == true) continue;
            var first = thread?["comments"]?.AsArray()
                .Where(c => c?["isDeleted"]?.GetValue<bool>() != true)
                .OrderBy(c => c?["id"]?.GetValue<int>() ?? int.MaxValue)
                .FirstOrDefault();
            var text = first?["content"]?.GetValue<string>();
            if (text is not null && text.Contains(markerText, StringComparison.Ordinal))
                return (thread!["id"]!.GetValue<int>(), first!["id"]!.GetValue<int>());
        }
        return null;
    }

    private HttpRequestMessage Request(HttpMethod method, string url, JsonNode? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return request;
    }

    private async Task<Stream> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (detail.Length > 300) detail = detail[..300] + "...";
            var hint = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? " Check that the build service identity has 'Contribute to pull requests' on the repository and that SYSTEM_ACCESSTOKEN is mapped."
                : "";
            throw new HttpRequestException($"Azure DevOps returned {(int)response.StatusCode} {response.ReasonPhrase} for {request.Method} {request.RequestUri?.GetLeftPart(UriPartial.Path)}.{hint} {detail}".TrimEnd());
        }
        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Prepares comment bodies: marker on top, size limit with a pointer to the full report.</summary>
public static class Rendering
{
    /// <summary>Azure DevOps rejects comments far above this; stay safely below.</summary>
    public const int MaxCommentLength = 140_000;

    public static string MarkerText(string marker) => $"<!-- tabulartools:{marker} -->";

    public static string Prepare(string markdown, string marker, string? artifactsUrl, int maxLength = MaxCommentLength)
    {
        var markerText = MarkerText(marker);
        var body = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!body.Contains(markerText, StringComparison.Ordinal)) body = markerText + "\n" + body;
        if (body.Length <= maxLength) return body;

        var notice = "\n\n> ⚠️ **Report truncated** to fit a pull request comment. " +
                     (artifactsUrl is null ? "The full report is published as a pipeline artifact." : $"The full report is in the [pipeline artifacts]({artifactsUrl}).");
        var cut = body.LastIndexOf('\n', Math.Max(0, maxLength - notice.Length - 64));
        var kept = body[..Math.Max(cut, markerText.Length)];

        // Close anything left open so the rest of the page renders.
        var closing = new System.Text.StringBuilder();
        if (CountOf(kept, "```") % 2 == 1) closing.Append("\n```");
        var openDetails = CountOf(kept, "<details>") - CountOf(kept, "</details>");
        for (var i = 0; i < openDetails; i++) closing.Append("\n</details>");
        return kept + closing + notice;
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
