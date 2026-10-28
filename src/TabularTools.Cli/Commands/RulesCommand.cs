using System.CommandLine;
using TabularTools.Core.Config;
using TabularTools.Core.Lint;
using TabularTools.Core.Lint.Rules;
using TabularTools.Core.Rendering;

namespace TabularTools.Cli.Commands;

internal static class RulesCommand
{
    public static Command Create(CliContext context)
    {
        var command = new Command("rules", "List rulesets or check a BPA rules file.");
        command.Subcommands.Add(List(context));
        command.Subcommands.Add(Check(context));
        return command;
    }

    private static Command List(CliContext context)
    {
        var rules = new Option<string[]>("--rules", "-r") { Description = "Rulesets to list instead of the configured ones.", AllowMultipleArgumentsPerToken = true };
        var config = new Option<FileInfo?>("--config", "-c") { Description = "tabulartools.yaml to use. Default: the nearest one above the current folder." };
        var output = new OutputOptions();
        var command = new Command("list", "List the rules that lint would run.");
        command.Options.Add(rules);
        command.Options.Add(config);
        output.AddTo(command);
        command.SetAction(parse => CommandRunner.Run(context, () =>
        {
            var cfg = parse.GetValue(config) is { } f ? ConfigLoader.Load(f.FullName) : ConfigLoader.Discover(context.WorkingDirectory);
            var rulesets = LintEngine.LoadRulesets(new LintOptions { Rulesets = parse.GetValue(rules), Config = cfg, BaseDirectory = context.WorkingDirectory });
            var rows = rulesets.SelectMany(r => r.Rules.Select(rule => (r, rule.Info))).ToList();
            var text = output.ResolveFormat(parse, context) == OutputFormat.Json
                ? OutputJson.Serialize(rulesets.Select(r => new { r.Id, r.Source, r.Path, Rules = r.Rules.Select(x => x.Info) }))
                : LintRenderer.RulesText(rows);
            output.Write(parse, context, text);
            return ExitCodes.Success;
        }));
        return command;
    }

    private static Command Check(CliContext context)
    {
        var file = new Argument<FileInfo>("file") { Description = "A BPA JSON rules file." };
        var output = new OutputOptions();
        var command = new Command("check", "Check that every rule in a BPA rules file can be evaluated. Exits 1 if any can't.");
        command.Arguments.Add(file);
        output.AddTo(command);
        command.SetAction(parse => CommandRunner.Run(context, () =>
        {
            var path = ModelArgument.Resolve(context, parse.GetValue(file)!.ToString());
            var ruleset = RulesetLoader.Load(path, context.WorkingDirectory);
            var checks = ruleset.Rules.OfType<BpaRule>().Select(r => r.Check() is { } problem
                ? new RuleCheck(r.Info.Id, r.Info.Name, r.Info.Scope, false, problem)
                : new RuleCheck(r.Info.Id, r.Info.Name, r.Info.Scope, true)).ToList();

            var text = output.ResolveFormat(parse, context) == OutputFormat.Json
                ? OutputJson.Serialize(checks)
                : string.Join(Environment.NewLine, checks.Select(c => c.Ok ? $"ok      {c.RuleId}" : $"FAILED  {c.RuleId}: {c.Problem}"))
                  + $"{Environment.NewLine}{checks.Count(c => c.Ok)} of {checks.Count} rules can be evaluated.";
            output.Write(parse, context, text);
            return checks.All(c => c.Ok) ? ExitCodes.Success : ExitCodes.BlockingFindings;
        }));
        return command;
    }
}
