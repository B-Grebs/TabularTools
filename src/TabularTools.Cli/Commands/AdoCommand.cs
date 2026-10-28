using System.CommandLine;
using TabularTools.Cli.Ado;

namespace TabularTools.Cli.Commands;

internal static class AdoCommand
{
    public static Command Create(CliContext context)
    {
        var file = new Option<FileInfo>("--file") { Description = "Markdown file to post, e.g. the output of 'review --format markdown --out review.md'.", Required = true };
        var marker = new Option<string>("--marker") { Description = "Thread key. The comment containing <!-- tabulartools:<marker> --> is updated instead of adding a new one.", DefaultValueFactory = _ => "review" };
        var status = new Option<string>("--status") { Description = "Status of a newly created thread: active or closed.", DefaultValueFactory = _ => "active" };
        status.AcceptOnlyFromAmong("active", "closed", "fixed", "wontFix", "pending");

        var comment = new Command("comment", "Create or update a pull request comment from a Markdown file, in an Azure Pipelines PR build. " +
                                             "Reads SYSTEM_COLLECTIONURI, SYSTEM_TEAMPROJECT, BUILD_REPOSITORY_ID, SYSTEM_PULLREQUEST_PULLREQUESTID and SYSTEM_ACCESSTOKEN.");
        comment.Options.Add(file);
        comment.Options.Add(marker);
        comment.Options.Add(status);
        comment.SetAction(parse => Comment(context, parse.GetValue(file)!, parse.GetValue(marker)!, parse.GetValue(status)!));

        var ado = new Command("ado", "Azure DevOps integration.");
        ado.Subcommands.Add(comment);
        return ado;
    }

    private static int Comment(CliContext context, FileInfo file, string marker, string status)
    {
        if (!file.Exists)
        {
            context.Error.WriteLine($"error: file not found: {file.FullName}");
            return ExitCodes.UsageError;
        }
        if (marker.Length == 0 || marker.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            context.Error.WriteLine("error: --marker may only contain letters, digits, '-', '_' and '.'.");
            return ExitCodes.UsageError;
        }

        AdoContext? ado;
        try
        {
            ado = AdoContext.FromEnvironment(context.Environment);
        }
        catch (InvalidOperationException ex)
        {
            context.Error.WriteLine("error: " + ex.Message);
            return ExitCodes.EnvironmentError;
        }
        if (ado is null)
        {
            context.Error.WriteLine($"Not a pull request build ({AdoContext.PullRequestIdVariable} is not set); no comment posted.");
            return ExitCodes.Success;
        }

        var body = Ado.Rendering.Prepare(File.ReadAllText(file.FullName), marker, ado.ArtifactsUrl);
        try
        {
            var commenter = context.CommenterFactory?.Invoke(ado) ?? new AdoPullRequestCommenter(SharedHttp.Value, ado);
            var outcome = commenter.UpsertAsync(marker, body, status).GetAwaiter().GetResult();
            context.Error.WriteLine($"{(outcome.Action == "created" ? "Created" : "Updated")} PR comment (thread {outcome.ThreadId}) on pull request {ado.PullRequestId}.");
            return ExitCodes.Success;
        }
        catch (HttpRequestException ex)
        {
            context.Error.WriteLine("error: " + ex.Message);
            return ExitCodes.EnvironmentError;
        }
        catch (TaskCanceledException)
        {
            context.Error.WriteLine("error: Azure DevOps did not respond in time.");
            return ExitCodes.EnvironmentError;
        }
    }

    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
}
