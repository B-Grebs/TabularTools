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
        var noTests = new Option<bool>("--no-tests") { Description = "Skip measure tests (they run when tests.files lists test files and a profile is set)." };
        var profile = new Option<string?>("--profile", "-p") { Description = "Connection profile for measure tests. Default: TABULARTOOLS_PROFILE, else tests.profile." };
        var strict = new Option<bool>("--strict") { Description = "Expected measure changes also block." };
        var jsonOut = new Option<string?>("--json-out") { Description = "Also write the result as JSON to this file (one run, so live tests aren't queried twice)." };
        var marker = new Option<string>("--marker") { Description = "Marker key embedded in Markdown output, used by 'ado comment'.", DefaultValueFactory = _ => ReviewRenderer.DefaultMarker };
        var output = new OutputOptions();

        var command = new Command("review", "Review semantic model changes for a pull request: semantic diff of every changed model, lint violations introduced since the base, and the configured measure tests. " +
                                            "Use --format markdown for a PR comment.");
        sides.AddTo(command);
        command.Options.Add(diffFailOn);
        command.Options.Add(lintFailOn);
        command.Options.Add(rules);
        command.Options.Add(noLint);
        command.Options.Add(noTests);
        command.Options.Add(profile);
        command.Options.Add(strict);
        command.Options.Add(jsonOut);
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
                Tests = parse.GetValue(noTests) ? null : new ReviewTestOptions(context.Executors, parse.GetValue(profile), parse.GetValue(strict) ? true : null, context.Environment),
            });
            var text = output.ResolveFormat(parse, context) switch
            {
                OutputFormat.Json => OutputJson.Serialize(result),
                OutputFormat.Markdown => ReviewRenderer.Markdown(result, parse.GetValue(marker)),
                _ => ReviewRenderer.Text(result),
            };
            output.Write(parse, context, text);
            if (parse.GetValue(jsonOut) is { } json)
            {
                var path = Path.GetFullPath(Path.Combine(context.WorkingDirectory, json));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, OutputJson.Serialize(result));
            }
            return result.Blocking ? ExitCodes.BlockingFindings : ExitCodes.Success;
        }));
        return command;
    }
}
