using System.CommandLine;
using TabularTools.Core.Config;
using TabularTools.Core.Queries;
using TabularTools.Core.Rendering;

namespace TabularTools.Cli.Commands;

internal static class DaxCommand
{
    public static Command Create(CliContext context)
    {
        var profile = new Option<string?>("--profile", "-p") { Description = "Connection profile from tabulartools.yaml. Default: TABULARTOOLS_PROFILE, else tests.profile." };
        var query = new Option<string?>("--query", "-q") { Description = "The DAX query (EVALUATE/DEFINE) or $SYSTEM DMV select." };
        var file = new Option<FileInfo?>("--file") { Description = "Read the query from this file instead." };
        var maxRows = new Option<int>("--max-rows") { Description = "Rows returned at most.", DefaultValueFactory = _ => DaxQueryRequest.DefaultMaxRows };
        var role = new Option<string?>("--role") { Description = "Evaluate as this RLS role (needs admin rights on the model)." };
        var timeout = new Option<int>("--timeout") { Description = "Timeout in seconds.", DefaultValueFactory = _ => DaxQueryRequest.DefaultTimeoutSeconds };
        var config = new Option<FileInfo?>("--config", "-c") { Description = "tabulartools.yaml to use. Default: the nearest one above the current folder." };
        var output = new OutputOptions();

        var command = new Command("dax", "Run a read-only DAX query against a live model through a connection profile. Anything that isn't a DAX query or a $SYSTEM DMV select is rejected before connecting.");
        command.Options.Add(profile);
        command.Options.Add(query);
        command.Options.Add(file);
        command.Options.Add(maxRows);
        command.Options.Add(role);
        command.Options.Add(timeout);
        command.Options.Add(config);
        output.AddTo(command);

        command.SetAction(parse => CommandRunner.Run(context, () =>
        {
            var text = parse.GetValue(query);
            if (parse.GetValue(file) is { } f)
            {
                if (text is not null) throw new ArgumentException("Pass --query or --file, not both.");
                text = File.ReadAllText(f.FullName);
            }
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Pass the query with --query or --file.");
            DaxQueryGuard.Check(text);
            var rows = parse.GetValue(maxRows);
            if (rows < 1) throw new ArgumentException("--max-rows must be at least 1.");

            var cfg = parse.GetValue(config) is { } c ? ConfigLoader.Load(c.FullName) : ConfigLoader.Discover(context.WorkingDirectory);
            if (cfg?.Live.AllowRunDax == false) throw new ArgumentException($"Ad-hoc queries are turned off (live.allowRunDax: false in {cfg.Path}).");
            var resolved = ConnectionProfiles.Resolve(cfg, parse.GetValue(profile), context.Environment);
            var executor = context.Executors.Create(resolved);
            var result = executor.ExecuteAsync(new DaxQueryRequest(text, parse.GetValue(role), rows, parse.GetValue(timeout))).GetAwaiter().GetResult();
            output.Write(parse, context, output.ResolveFormat(parse, context) switch
            {
                OutputFormat.Json => OutputJson.Serialize(result),
                OutputFormat.Markdown => DaxResultRenderer.Markdown(result),
                _ => DaxResultRenderer.Text(result),
            });
            return ExitCodes.Success;
        }));
        return command;
    }
}
