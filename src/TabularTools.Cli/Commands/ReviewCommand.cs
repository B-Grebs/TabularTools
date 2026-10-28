using System.CommandLine;
using TabularTools.Core.Diff;
using TabularTools.Core.Lint;
using TabularTools.Core.Rendering;
using TabularTools.Core.Review;

namespace TabularTools.Cli.Commands;

internal static class ReviewCommand
{
    public static Command Create(CliContext context)
    {
        var sides = new DiffCommand.SideOptions();
        var diffFailOn = new Option<DiffFailOn?>("--fail-on") { Description = "Exit with code 1 when a change is at or above: breaking, high, medium, low, info, none. Default: config, else none." };
        var lintFailOn = new Option<FailOn?>("--lint-fail-on") { Description = "Exit with code 1 when a NEW lint violation is at or above: error, warning, info, none. Default: config, else error." };
        var rules = new Option<string[]>("--rules", "-r") { Description = "Rulesets instead of the configured ones. Repeatable.", AllowMultipleArgumentsPerToken = true };
        var noLint = new Option<bool>("--no-lint") { Description = "Skip linting." };
        var marker = new Option<string>("--marker") { Description = "Marker key embedded in Markdown output, used by 'ado comment'.", DefaultValueFactory = _ => ReviewRenderer.DefaultMarker };
        var output = new OutputOptions();

        var command = new Command("review", "Review semantic model changes for a pull request: semantic diff of every changed model plus lint violations introduced since the base. " +
                                            "Use --format markdown for a PR comment.");
        sides.AddTo(command);
        command.Options.Add(diffFailOn);
        command.Options.Add(lintFailOn);
        command.Options.Add(rules);
        command.Options.Add(noLint);
        command.Options.Add(marker);
        output.AddTo(command);

        command.SetAction(parse => CommandRunner.Run(context, () =>
        {
            var result = ReviewEngine.Run(new ReviewRequest
            {
                Diff = sides.Request(parse, context, parse.GetValue(diffFailOn)),
                LintFailOn = parse.GetValue(lintFailOn),
                Rulesets = parse.GetValue(rules) is { Length: > 0 } r ? r : null,
                NoLint = parse.GetValue(noLint),
            });
            var text = output.ResolveFormat(parse, context) switch
            {
                OutputFormat.Json => OutputJson.Serialize(result),
                OutputFormat.Markdown => ReviewRenderer.Markdown(result, parse.GetValue(marker)),
                _ => ReviewRenderer.Text(result),
            };
            output.Write(parse, context, text);
            return result.Blocking ? ExitCodes.BlockingFindings : ExitCodes.Success;
        }));
        return command;
    }
}
