using System.CommandLine;
using TabularTools.Core.Describe;
using TabularTools.Core.Loading;
using TabularTools.Core.Rendering;

namespace TabularTools.Cli.Commands;

internal static class DescribeCommand
{
    public static Command Create(CliContext context)
    {
        var model = new Argument<string?>("model")
        {
            Description = "A .pbip file, *.SemanticModel folder, TMDL definition folder or model.bim file. Default: the current folder.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        model.Validators.Add(result =>
        {
            if (result.Tokens is [{ Value: var value }] && value.StartsWith('-'))
                result.AddError($"Unknown option: {value}");
        });
        var detail = new Option<DetailLevel>("--detail", "-d")
        {
            Description = "summary = counts and tables; objects = objects without expressions; full = with DAX/M expressions and RLS filters.",
            DefaultValueFactory = _ => DetailLevel.Objects,
        };
        var tables = new Option<string[]>("--tables", "-t") { Description = "Only these tables.", AllowMultipleArgumentsPerToken = true };
        var types = new Option<ObjectType[]>("--types") { Description = "Only these object types (table, column, measure, partition, hierarchy, calculationItem, relationship, role, expression, function).", AllowMultipleArgumentsPerToken = true };
        var name = new Option<string?>("--name", "-n") { Description = "Glob on object names, e.g. 'Sales*'." };
        var excludeHidden = new Option<bool>("--exclude-hidden") { Description = "Leave out hidden objects." };
        var output = new OutputOptions();

        var command = new Command("describe", "List a semantic model's tables, columns, measures, relationships, roles and more.");
        command.Arguments.Add(model);
        command.Options.Add(detail);
        command.Options.Add(tables);
        command.Options.Add(types);
        command.Options.Add(name);
        command.Options.Add(excludeHidden);
        output.AddTo(command);

        command.SetAction(parse => CommandRunner.Run(context, () =>
        {
            var path = parse.GetValue(model) ?? ".";
            var loaded = ModelLoader.Load(Path.GetFullPath(Path.Combine(context.WorkingDirectory, path)));
            var request = new DescribeRequest
            {
                Detail = parse.GetValue(detail),
                Tables = parse.GetValue(tables),
                ObjectTypes = parse.GetValue(types),
                NamePattern = parse.GetValue(name),
                IncludeHidden = !parse.GetValue(excludeHidden),
                Limit = ModelDescriber.MaxLimit,
            };
            var result = DescribeAll(loaded, request);

            var text = output.ResolveFormat(parse, context) switch
            {
                OutputFormat.Json => OutputJson.Serialize(result),
                OutputFormat.Markdown => DescribeRenderer.Markdown(result),
                _ => DescribeRenderer.Text(result),
            };
            output.Write(parse, context, text);
            return ExitCodes.Success;
        }));
        return command;
    }

    /// <summary>The CLI returns everything: follow cursors until the last page.</summary>
    private static DescribeResult DescribeAll(LoadedModel loaded, DescribeRequest request)
    {
        var first = ModelDescriber.Describe(loaded, request);
        if (!first.Truncated) return first;

        var objects = new List<DescribedObject>(first.Objects!);
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = ModelDescriber.Describe(loaded, request with { Cursor = cursor });
            objects.AddRange(page.Objects!);
            cursor = page.NextCursor;
        }
        return first with { Objects = objects, Truncated = false, NextCursor = null };
    }
}
