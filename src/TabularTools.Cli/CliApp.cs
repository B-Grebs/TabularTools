using System.CommandLine;
using TabularTools.Cli.Commands;
using TabularTools.Core;

namespace TabularTools.Cli;

public static class CliApp
{
    public static int Run(string[] args, CliContext context)
    {
        var root = new RootCommand($"{ProductInfo.Name} {ProductInfo.Version} — describe, lint, diff and test Power BI semantic models (PBIP/TMDL).");
        root.Subcommands.Add(DescribeCommand.Create(context));
        root.Subcommands.Add(LintCommand.Create(context));
        root.Subcommands.Add(DepsCommand.Create(context));
        root.Subcommands.Add(RulesCommand.Create(context));

        var parse = root.Parse(args);
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors) context.Error.WriteLine("error: " + error.Message);
            context.Error.WriteLine("Run 'tabulartools --help' for usage.");
            return ExitCodes.UsageError;
        }

        return parse.Invoke(new InvocationConfiguration { Output = context.Out, Error = context.Error });
    }
}
