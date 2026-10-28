using System.CommandLine;
using TabularTools.Core.Diff;
using TabularTools.Core.Rendering;

namespace TabularTools.Cli.Commands;

internal static class DiffCommand
{
    /// <summary>Options shared by diff and review.</summary>
    internal sealed class SideOptions
    {
        public Option<string> Base { get; } = new("--base", "-b")
        {
            Description = "Base: a git ref (e.g. origin/main) or a model folder/file. Required.",
            Required = true,
        };
        public Option<string> Head { get; } = new("--head")
        {
            Description = "Head: a git ref, WORKTREE (files on disk), or a model folder/file.",
            DefaultValueFactory = _ => "HEAD",
        };
        public Option<string?> Repo { get; } = new("--repo") { Description = "Repository folder. Default: the current folder." };
        public Option<string?> Model { get; } = new("--model", "-m") { Description = "Only this model (a *.SemanticModel folder, .pbip, or model.bim). Default: every model changed between base and head." };
        public Option<bool> NoMergeBase { get; } = new("--no-merge-base") { Description = "Compare with the base ref itself instead of merge-base(base, head)." };
        public Option<bool> IncludeFormatting { get; } = new("--include-formatting") { Description = "Also report expression changes that only touch whitespace or comments." };
        public Option<FileInfo?> Config { get; } = new("--config", "-c") { Description = "tabulartools.yaml to use. Default: the one at the repository root." };

        public void AddTo(Command command)
        {
            command.Options.Add(Base);
            command.Options.Add(Head);
            command.Options.Add(Repo);
            command.Options.Add(Model);
            command.Options.Add(NoMergeBase);
            command.Options.Add(IncludeFormatting);
            command.Options.Add(Config);
        }

        public DiffRequest Request(ParseResult parse, CliContext context, DiffFailOn? failOn = null, ChangeSeverity? minSeverity = null) => new()
        {
            Base = parse.GetValue(Base)!,
            Head = parse.GetValue(Head),
            Repo = parse.GetValue(Repo),
            ModelPath = parse.GetValue(Model),
            MergeBase = !parse.GetValue(NoMergeBase),
            IncludeFormattingOnly = parse.GetValue(IncludeFormatting) ? true : null,
            ConfigPath = parse.GetValue(Config)?.FullName,
            FailOn = failOn,
            MinSeverity = minSeverity,
            WorkingDirectory = context.WorkingDirectory,
        };
    }

    public static Command Create(CliContext context)
    {
        var sides = new SideOptions();
        var failOn = new Option<DiffFailOn?>("--fail-on") { Description = "Exit with code 1 when a change is at or above: breaking, high, medium, low, info, none. Default: config, else none." };
        var minSeverity = new Option<ChangeSeverity?>("--min-severity") { Description = "Only report changes at or above: breaking, high, medium, low, info." };
        var marker = new Option<string>("--marker") { Description = "Marker key embedded in Markdown output, so 'ado comment' updates the same thread.", DefaultValueFactory = _ => DiffRenderer.DefaultMarker };
        var output = new OutputOptions();

        var command = new Command("diff", "Compare two versions of a semantic model by meaning: between git refs (e.g. a PR's target and source), against the working tree, or between two folders.");
        sides.AddTo(command);
        command.Options.Add(failOn);
        command.Options.Add(minSeverity);
        command.Options.Add(marker);
        output.AddTo(command);

        command.SetAction(parse => CommandRunner.Run(context, () =>
        {
            var run = SemanticDiff.Run(sides.Request(parse, context, parse.GetValue(failOn), parse.GetValue(minSeverity)));
            var result = run.Result;
            var text = output.ResolveFormat(parse, context) switch
            {
                OutputFormat.Json => OutputJson.Serialize(result),
                OutputFormat.Markdown => DiffRenderer.Markdown(result, parse.GetValue(marker)),
                _ => DiffRenderer.Text(result),
            };
            output.Write(parse, context, text);
            return result.Blocking ? ExitCodes.BlockingFindings : ExitCodes.Success;
        }));
        return command;
    }
}
