using System.CommandLine;
using TabularTools.Core.Git;
using TabularTools.Core.Queries;
using TabularTools.Core.Rendering;
using TabularTools.Core.Testing;
using TabularTools.Core.Writes;

namespace TabularTools.Cli.Commands;

internal static class TestCommand
{
    public static Command Create(CliContext context)
    {
        var command = new Command("test", "Measure regression tests: evaluate measures with base and head definitions against a live (or recorded) model, without deploying.");
        command.Subcommands.Add(Run(context));
        command.Subcommands.Add(Gen(context));
        return command;
    }

    private static Command Run(CliContext context)
    {
        var files = new Argument<string[]>("test-files") { Description = "Test files (*.measuretests.yaml). Default: tests.files in tabulartools.yaml.", Arity = ArgumentArity.ZeroOrMore };
        var profile = new Option<string?>("--profile", "-p") { Description = "Connection profile. Default: TABULARTOOLS_PROFILE, the test file's, else tests.profile." };
        var mode = new Option<AssertionKind?>("--mode") { Description = "Only run differential or snapshot assertions (invariants always run)." };
        var baseRef = new Option<string?>("--base", "-b") { Description = "Base git ref for differential tests. Default: HEAD (tests your uncommitted edits)." };
        var headRef = new Option<string?>("--head") { Description = "Head git ref, or WORKTREE (default) for the files on disk." };
        var noMergeBase = new Option<bool>("--no-merge-base") { Description = "Compare with the base ref itself instead of merge-base(base, head)." };
        var cases = new Option<string[]>("--case") { Description = "Only these case ids. Repeatable.", AllowMultipleArgumentsPerToken = true };
        var strict = new Option<bool>("--strict") { Description = "Expected changes (on measures the change edited) also fail." };
        var record = new Option<string?>("--record") { Description = "Save every query result to this recordings file, for offline replay in CI." };
        var config = new Option<FileInfo?>("--config", "-c") { Description = "tabulartools.yaml to use. Default: the nearest one above the first test file." };
        var marker = new Option<string>("--marker") { Description = "Marker key embedded in Markdown output, used by 'ado comment'.", DefaultValueFactory = _ => TestRenderer.DefaultMarker };
        var output = new OutputOptions();

        var command = new Command("run", "Run measure tests. Exits 1 when a test fails or errors (or changes, with --strict).");
        command.Arguments.Add(files);
        command.Options.Add(profile);
        command.Options.Add(mode);
        command.Options.Add(baseRef);
        command.Options.Add(headRef);
        command.Options.Add(noMergeBase);
        command.Options.Add(cases);
        command.Options.Add(strict);
        command.Options.Add(record);
        command.Options.Add(config);
        command.Options.Add(marker);
        output.AddTo(command);

        command.SetAction(parse => CommandRunner.Run(context, () =>
        {
            var recorders = new List<RecordingDaxExecutor>();
            var recordFile = parse.GetValue(record) is { } r ? Path.GetFullPath(Path.Combine(context.WorkingDirectory, r)) : null;
            var result = MeasureTestRunner.RunAsync(new TestRunRequest
            {
                TestFiles = parse.GetValue(files) ?? [],
                Profile = parse.GetValue(profile),
                Base = parse.GetValue(baseRef),
                Head = parse.GetValue(headRef),
                MergeBase = !parse.GetValue(noMergeBase),
                Mode = parse.GetValue(mode),
                CaseIds = parse.GetValue(cases) is { Length: > 0 } ids ? ids : null,
                Strict = parse.GetValue(strict) ? true : null,
                ConfigPath = parse.GetValue(config)?.FullName,
                WorkingDirectory = context.WorkingDirectory,
                Environment = context.Environment,
                WrapExecutor = recordFile is null ? null : executor =>
                {
                    if (executor is RecordedDaxExecutor) throw new ArgumentException("--record needs a live profile; this one already replays recordings.");
                    var recorder = new RecordingDaxExecutor(executor, context.Clock);
                    recorders.Add(recorder);
                    return recorder;
                },
            }, context.Executors).GetAwaiter().GetResult();

            if (recordFile is not null)
            {
                if (recorders.Count > 1) context.Error.WriteLine("warning: the tests used several profiles; recording only the first one's results.");
                if (recorders.Count > 0)
                {
                    var write = recorders[0].Save(recordFile, new FileWriter([Path.GetDirectoryName(recordFile)!]));
                    context.Error.WriteLine($"Recorded {recorders[0].Count} queries in {write.Files[0].Path} ({Action(write.Files[0].Action)}).");
                }
            }

            output.Write(parse, context, output.ResolveFormat(parse, context) switch
            {
                OutputFormat.Json => OutputJson.Serialize(result),
                OutputFormat.Markdown => TestRenderer.Markdown(result, parse.GetValue(marker)),
                _ => TestRenderer.Text(result),
            });
            return result.Blocking ? ExitCodes.BlockingFindings : ExitCodes.Success;
        }));
        return command;
    }

    private static Command Gen(CliContext context)
    {
        var file = new Argument<string>("test-file") { Description = "Test file to create or extend (*.measuretests.yaml)." };
        var model = new Option<string?>("--model", "-m") { Description = "The model: a .pbip, *.SemanticModel folder or model.bim. Default: the existing file's model." };
        var measures = new Option<string[]>("--measures") { Description = "Measures to test, e.g. \"[Total Sales]\". Repeatable.", AllowMultipleArgumentsPerToken = true, Required = true };
        var groupBy = new Option<string[]>("--group-by") { Description = "Columns to slice by, e.g. \"'Date'[Year]\". Repeatable.", AllowMultipleArgumentsPerToken = true };
        var filters = new Option<string[]>("--filter") { Description = "DAX filters, e.g. \"'Date'[Year] = 2023\". Repeatable.", AllowMultipleArgumentsPerToken = true };
        var mode = new Option<AssertionKind>("--mode") { Description = "differential (default) or snapshot (captures current values from the live model).", DefaultValueFactory = _ => AssertionKind.Differential };
        var profile = new Option<string?>("--profile", "-p") { Description = "Connection profile; needed for snapshot mode, and written into a new file." };
        var caseId = new Option<string?>("--case-id") { Description = "Case id. Default: made from the measures and columns. An existing case with this id is replaced." };
        var description = new Option<string?>("--description") { Description = "Case description." };
        var role = new Option<string?>("--role") { Description = "Evaluate the case as this RLS role." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show the result without writing the file." };
        var config = new Option<FileInfo?>("--config", "-c") { Description = "tabulartools.yaml to use. Default: the nearest one above the test file." };
        var output = new OutputOptions();

        var command = new Command("gen", "Create or extend a measure test file with a case for the given measures and slices.");
        command.Arguments.Add(file);
        command.Options.Add(model);
        command.Options.Add(measures);
        command.Options.Add(groupBy);
        command.Options.Add(filters);
        command.Options.Add(mode);
        command.Options.Add(profile);
        command.Options.Add(caseId);
        command.Options.Add(description);
        command.Options.Add(role);
        command.Options.Add(dryRun);
        command.Options.Add(config);
        output.AddTo(command);

        command.SetAction(parse => CommandRunner.Run(context, () =>
        {
            if (parse.GetValue(mode) == AssertionKind.Invariant) throw new ArgumentException("--mode is differential or snapshot; add invariants to the file by hand.");
            var path = Path.GetFullPath(Path.Combine(context.WorkingDirectory, parse.GetValue(file)!));
            var roots = new List<string> { context.WorkingDirectory, Path.GetDirectoryName(path)! };
            try { roots.Add(GitCliReader.Open(Path.GetDirectoryName(path)!).RepositoryRoot); } catch (GitException) { }
            var result = MeasureTestGenerator.GenerateAsync(new GenerateRequest
            {
                TestFile = path,
                Model = parse.GetValue(model) is { } m ? Path.GetFullPath(Path.Combine(context.WorkingDirectory, m)) : null,
                Measures = parse.GetValue(measures) ?? [],
                GroupBy = parse.GetValue(groupBy),
                Filters = parse.GetValue(filters),
                Mode = parse.GetValue(mode),
                Profile = parse.GetValue(profile),
                CaseId = parse.GetValue(caseId),
                Description = parse.GetValue(description),
                AsRole = parse.GetValue(role),
                Save = !parse.GetValue(dryRun),
                ConfigPath = parse.GetValue(config)?.FullName,
                WorkingDirectory = context.WorkingDirectory,
                Environment = context.Environment,
            }, new FileWriter(roots), context.Executors, context.Clock).GetAwaiter().GetResult();

            foreach (var d in result.Diagnostics ?? []) context.Error.WriteLine($"{d.Level.ToString().ToLowerInvariant()} {d.Code}: {d.Message}");
            var change = result.Write.Files[0];
            output.Write(parse, context, output.ResolveFormat(parse, context) switch
            {
                OutputFormat.Json => OutputJson.Serialize(result),
                _ => $"{(result.Write.DryRun ? "Would write" : Capitalize(Action(change.Action)))} {change.Path}: case '{result.CaseId}' {(result.Replaced ? "replaced" : "added")} ({result.Cases.Count} case{(result.Cases.Count == 1 ? "" : "s")}).\n" +
                     (change.UnifiedDiff.Length > 0 ? "\n" + change.UnifiedDiff + "\n" : ""),
            });
            return ExitCodes.Success;
        }));
        return command;
    }

    private static string Action(WriteAction action) => action switch
    {
        WriteAction.Created => "created",
        WriteAction.Modified => "updated",
        _ => "unchanged",
    };

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
