using System.CommandLine;
using TabularTools.Core.Config;
using TabularTools.Core.Lint;
using TabularTools.Core.Rendering;

namespace TabularTools.Cli.Commands;

internal static class LintCommand
{
    public static Command Create(CliContext context)
    {
        var model = ModelArgument.Create();
        var rules = new Option<string[]>("--rules", "-r")
        {
            Description = "Rulesets to run instead of the configured ones: builtin, microsoft-bpa, or a BPA JSON file path. Repeatable.",
            AllowMultipleArgumentsPerToken = true,
        };
        var config = new Option<FileInfo?>("--config", "-c") { Description = "tabulartools.yaml to use. Default: the nearest one above the model." };
        var failOn = new Option<FailOn?>("--fail-on") { Description = "Exit with code 1 when a violation is at or above: error, warning, info, none. Default: config, else error." };
        var minSeverity = new Option<Severity?>("--min-severity") { Description = "Only report violations at or above: error, warning, info." };
        var ruleIds = new Option<string[]>("--rule") { Description = "Only run these rule ids. Repeatable.", AllowMultipleArgumentsPerToken = true };
        var output = new OutputOptions();

        var command = new Command("lint", "Check a semantic model against best-practice rules. Exits 1 when violations reach the --fail-on level.");
        command.Arguments.Add(model);
        command.Options.Add(rules);
        command.Options.Add(config);
        command.Options.Add(failOn);
        command.Options.Add(minSeverity);
        command.Options.Add(ruleIds);
        output.AddTo(command);

        command.SetAction(parse => CommandRunner.Run(context, () =>
        {
            var loaded = ModelArgument.Load(parse, model, context);
            var cfg = ConfigLoader.ForModel(loaded.Source, parse.GetValue(config)?.FullName);
            var result = LintEngine.Run(loaded, new LintOptions
            {
                Rulesets = parse.GetValue(rules),
                Config = cfg,
                FailOn = parse.GetValue(failOn),
                MinSeverity = parse.GetValue(minSeverity),
                RuleIds = parse.GetValue(ruleIds),
                BaseDirectory = context.WorkingDirectory,
            });

            var text = output.ResolveFormat(parse, context) switch
            {
                OutputFormat.Json => OutputJson.Serialize(result),
                OutputFormat.Markdown => LintRenderer.Markdown(result),
                _ => LintRenderer.Text(result),
            };
            output.Write(parse, context, text);
            return result.Blocking ? ExitCodes.BlockingFindings : ExitCodes.Success;
        }));
        return command;
    }
}
