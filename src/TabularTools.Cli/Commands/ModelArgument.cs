using System.CommandLine;
using TabularTools.Core.Loading;

namespace TabularTools.Cli.Commands;

/// <summary>The optional model path argument shared by model commands.</summary>
internal static class ModelArgument
{
    public static Argument<string?> Create() 
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
        return model;
    }

    public static LoadedModel Load(ParseResult parse, Argument<string?> model, CliContext context) =>
        ModelLoader.Load(Resolve(context, parse.GetValue(model) ?? "."));

    public static string Resolve(CliContext context, string path) => Path.GetFullPath(Path.Combine(context.WorkingDirectory, path));
}
