using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TabularTools.Core.Config;
using TabularTools.Core.Loading;
using TabularTools.Core.Queries;
using TabularTools.Core.Rendering;
using TabularTools.Core.Testing;
using TabularTools.Core.Writes;

namespace TabularTools.Mcp.Tools;

/// <summary>Measure regression tests: generate test files and run them.</summary>
[McpServerToolType]
public sealed class TestTools(ModelWorkspace workspace, ServerOptions options, IDaxExecutorFactory executors)
{
    [McpServerTool(Name = "generate_measure_tests", Title = "Generate measure tests", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(GenerateResult))]
    [Description("Create or extend a measure regression test file (*.measuretests.yaml): pick measures and slices (group-by columns and filters) and the tool writes " +
                 "the test case. In differential mode (default) the case compares base and head definitions when run; in snapshot mode it also captures current " +
                 "values from the live model. Returns the file content and a diff. Saving (save: true) needs the server started with --allow-writes; otherwise it's a preview.")]
    public Task<CallToolResult> GenerateMeasureTests(
        [Description("Test file to create or extend, relative to the server root; must end with .measuretests.yaml.")] string testFile,
        [Description("Measures to test, e.g. ['[Total Sales]', '[Margin %]'].")] string[] measures,
        [Description("Model: a .pbip, *.SemanticModel folder or model.bim, relative to the server root. Default: the existing file's model.")] string? model = null,
        [Description("Columns to slice by, e.g. [\"'Date'[Year]\", \"'Store'[Region]\"]. Prefer a date grain and one or two key dimensions.")] string[]? groupBy = null,
        [Description("DAX filters, e.g. [\"'Date'[Year] IN { 2023, 2024 }\"]. Pin closed periods so refreshes don't move the numbers.")] string[]? filters = null,
        [Description("'differential' (compare base and head definitions; stores no values) or 'snapshot' (stores current values; needs a profile).")] string mode = "differential",
        [Description("Connection profile; required for snapshot mode and written into a new file.")] string? profile = null,
        [Description("Case id. Default: made from the measures and columns. An existing case with this id is replaced.")] string? caseId = null,
        [Description("What the case checks.")] string? description = null,
        [Description("Evaluate the case as this RLS role.")] string? asRole = null,
        [Description("Write the file (needs --allow-writes). Default: false, preview only.")] bool save = false) => ToolResults.RunAsync(async () =>
    {
        if (save && !options.AllowWrites)
            throw new ArgumentException("Saving needs the server started with --allow-writes. Call again with save: false to preview the file.");
        var kind = AssertionKinds.Parse(mode, "mode");
        if (kind == AssertionKind.Invariant) throw new ArgumentException("mode is 'differential' or 'snapshot'.");
        var result = await MeasureTestGenerator.GenerateAsync(new GenerateRequest
        {
            TestFile = testFile,
            Model = model,
            Measures = measures,
            GroupBy = groupBy,
            Filters = filters,
            Mode = kind,
            Profile = profile,
            CaseId = caseId,
            Description = description,
            AsRole = asRole,
            Save = save,
            WorkingDirectory = workspace.Roots[0],
            AllowLive = !options.DisableLive,
        }, new FileWriter(workspace.Roots), executors).ConfigureAwait(false);
        var change = result.Write.Files[0];
        var verb = result.Write.DryRun ? "Preview (not saved)" : change.Action == WriteAction.Created ? "Created" : change.Action == WriteAction.Modified ? "Updated" : "Unchanged";
        return ToolResults.Success(result, $"{verb}: {change.Path}, case '{result.CaseId}' {(result.Replaced ? "replaced" : "added")}; {result.Cases.Count} case(s) in the file.");
    });

    [McpServerTool(Name = "run_measure_tests", Title = "Run measure tests", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(MeasureTestRunResult))]
    [Description("Run measure regression tests from test files against a live model (or a recordings file). Differential assertions evaluate each case with the " +
                 "base and head definitions of changed measures as query-scoped overrides (nothing is deployed) and report which values changed: a change on a " +
                 "measure the edit touched directly is 'changed' (expected); a change anywhere else is 'fail' (regression). Snapshot assertions compare with stored " +
                 "values; invariants must hold on every row. Changes queries can't simulate (relationships, data types, RLS) are listed as not tested. " +
                 "Default base is HEAD and head is the working tree, so it tests uncommitted edits.")]
    public Task<CallToolResult> RunMeasureTests(
        [Description("Test files (*.measuretests.yaml), relative to the server root. Default: tests.files in tabulartools.yaml.")] string[]? testFiles = null,
        [Description("Connection profile. Default: TABULARTOOLS_PROFILE, the test file's profile, else tests.profile.")] string? profile = null,
        [Description("Only run 'differential' or 'snapshot' assertions (invariants always run). Default: all.")] string? mode = null,
        [Description("Base git ref for differential tests, e.g. 'origin/main'. Default: HEAD.")] string? @base = null,
        [Description("Head git ref, or 'WORKTREE' (default) for the files on disk.")] string? head = null,
        [Description("Only these case ids.")] string[]? caseIds = null,
        [Description("Treat expected changes as failures.")] bool strict = false,
        [Description("Maximum differences listed per assertion (summaries always count all).")] int maxDiffs = 10) => ToolResults.RunAsync(async () =>
    {
        AssertionKind? kind = mode is null ? null : AssertionKinds.Parse(mode, "mode");
        var result = await MeasureTestRunner.RunAsync(new TestRunRequest
        {
            TestFiles = testFiles ?? [],
            Profile = profile,
            Mode = kind,
            Base = @base,
            Head = head,
            CaseIds = caseIds,
            Strict = strict ? true : null,
            WorkingDirectory = workspace.Roots[0],
            AllowLive = !options.DisableLive,
        }, executors).ConfigureAwait(false);
        var max = Math.Clamp(maxDiffs, 0, MeasureTestRunner.MaxDiffsListed);
        var trimmed = result with
        {
            Files = result.Files.Select(f => f with { Results = f.Results.Select(r => r with { Diffs = r.Diffs?.Take(max).ToList() }).ToList() }).ToList(),
        };
        return ToolResults.Success(trimmed, TestRenderer.SummaryLine(result));
    });

    [McpServerResource(UriTemplate = "tabulartools://schemas/{name}", Name = "schemas", Title = "JSON Schemas", MimeType = "application/schema+json")]
    [Description("JSON Schemas for files TabularTools reads. 'measure-tests' describes *.measuretests.yaml; read it before writing a test file by hand.")]
    public static string Schema(string name) => name switch
    {
        "measure-tests" => Schemas.MeasureTests,
        _ => throw new ArgumentException($"Unknown schema '{name}'. Available: measure-tests."),
    };

    [McpServerResource(UriTemplate = "tabulartools://workspace/test-files", Name = "test-files", Title = "Measure test files", MimeType = "application/json")]
    [Description("Measure test files found under the server root (tests.files in tabulartools.yaml, else any *.measuretests.yaml), with their models and case ids.")]
    public string TestFiles()
    {
        var root = workspace.Roots[0];
        var config = ConfigLoader.Discover(root);
        var files = MeasureTestRunner.FindTestFiles(config);
        if (files.Count == 0)
            files = Directory.EnumerateFiles(root, "*" + MeasureTestFile.Extension, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Order(StringComparer.Ordinal).Take(200).ToList();
        return OutputJson.Serialize(files.Select(f =>
        {
            try
            {
                var file = MeasureTestFile.Load(f);
                return (object)new { file = Path.GetRelativePath(root, f).Replace('\\', '/'), model = file.Model, profile = file.Profile, cases = file.Cases.Select(c => c.Id).ToList() };
            }
            catch (ArgumentException ex)
            {
                return new { file = Path.GetRelativePath(root, f).Replace('\\', '/'), error = ex.Message };
            }
        }).ToList());
    }
}
