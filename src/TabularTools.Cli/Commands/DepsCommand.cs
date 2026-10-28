using System.CommandLine;
using TabularTools.Core.Dax;
using TabularTools.Core.Rendering;

namespace TabularTools.Cli.Commands;

internal static class DepsCommand
{
    public static Command Create(CliContext context)
    {
        var target = new Argument<string>("object") { Description = "Table[Column], 'Table'[Column], [Measure], 'Table', or an object key." };
        var model = new Option<string?>("--model", "-m") { Description = "The model. Default: the current folder." };
        var direction = new Option<DependencyDirection>("--direction") { Description = "upstream, downstream or both.", DefaultValueFactory = _ => DependencyDirection.Both };
        var depth = new Option<int>("--depth") { Description = "Levels to follow; 0 = all.", DefaultValueFactory = _ => 1 };
        var output = new OutputOptions();

        var command = new Command("deps", "Show what an object depends on and what depends on it, from every DAX expression in the model.");
        command.Arguments.Add(target);
        command.Options.Add(model);
        command.Options.Add(direction);
        command.Options.Add(depth);
        output.AddTo(command);

        command.SetAction(parse => CommandRunner.Run(context, () =>
        {
            var loaded = Core.Loading.ModelLoader.Load(ModelArgument.Resolve(context, parse.GetValue(model) ?? "."));
            var result = DependencyQuery.Query(loaded.Snapshot, loaded.Dependencies, parse.GetValue(target)!, parse.GetValue(direction), parse.GetValue(depth));
            var text = output.ResolveFormat(parse, context) == OutputFormat.Json ? OutputJson.Serialize(result) : LintRenderer.DependenciesText(result);
            output.Write(parse, context, text);
            return ExitCodes.Success;
        }));
        return command;
    }
}
