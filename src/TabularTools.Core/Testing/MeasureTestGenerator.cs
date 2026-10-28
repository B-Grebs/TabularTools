using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TabularTools.Core.Config;
using TabularTools.Core.Diagnostics;
using TabularTools.Core.Loading;
using TabularTools.Core.Queries;
using TabularTools.Core.Writes;

namespace TabularTools.Core.Testing;

public sealed record GenerateRequest
{
    /// <summary>The model (path). Default: the existing test file's model.</summary>
    public string? Model { get; init; }
    /// <summary>Test file to create or extend; must end with <c>.measuretests.yaml</c>.</summary>
    public required string TestFile { get; init; }
    public required IReadOnlyList<string> Measures { get; init; }
    public IReadOnlyList<string>? GroupBy { get; init; }
    public IReadOnlyList<string>? Filters { get; init; }
    /// <summary>Differential (default) or snapshot (captures current values from the live model).</summary>
    public AssertionKind Mode { get; init; } = AssertionKind.Differential;
    /// <summary>Connection profile; required for snapshot mode. Also written to a new file.</summary>
    public string? Profile { get; init; }
    public string? CaseId { get; init; }
    public string? Description { get; init; }
    public string? AsRole { get; init; }
    /// <summary>Write the file. Default false: return the content and the diff only.</summary>
    public bool Save { get; init; }
    public string? ConfigPath { get; init; }
    public required string WorkingDirectory { get; init; }
    public Func<string, string?>? Environment { get; init; }
    public bool AllowLive { get; init; } = true;
}

/// <param name="Replaced">True when a case with the same id was replaced.</param>
public sealed record GenerateResult(string TestFile, string CaseId, bool Replaced, IReadOnlyList<string> Cases, string Yaml, WriteResult Write, IReadOnlyList<Diagnostic>? Diagnostics);

/// <summary>Creates or extends a test file (ARCHITECTURE §8, <c>generate_measure_tests</c>).</summary>
public static class MeasureTestGenerator
{
    public static async Task<GenerateResult> GenerateAsync(GenerateRequest request, FileWriter writer, IDaxExecutorFactory? factory = null,
        Func<DateTimeOffset>? clock = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writer);
        var cwd = Path.GetFullPath(request.WorkingDirectory);
        var path = Path.GetFullPath(Path.Combine(cwd, request.TestFile));
        if (!path.EndsWith(MeasureTestFile.Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Test files must end with {MeasureTestFile.Extension}: {request.TestFile}");
        if (request.Measures.Count == 0) throw new ArgumentException("Give at least one measure.");

        var existing = File.Exists(path) ? MeasureTestFile.Load(path) : null;
        var existingText = existing is null ? null : File.ReadAllText(path);
        var dir = Path.GetDirectoryName(path)!;
        string modelRelative;
        if (request.Model is { Length: > 0 } m)
        {
            var modelFull = Path.GetFullPath(Path.Combine(cwd, m));
            modelRelative = Path.GetRelativePath(dir, modelFull).Replace('\\', '/');
            if (existing is not null && Path.GetFullPath(Path.Combine(dir, existing.Model!)) != modelFull)
                throw new ArgumentException($"{request.TestFile} tests the model {existing.Model}; use another file for {m}.");
        }
        else modelRelative = existing?.Model ?? throw new ArgumentException("Give the model to test (a .pbip, *.SemanticModel folder or model.bim).");

        var file = existing ?? new MeasureTestFile { Model = modelRelative, Profile = request.Profile, Path = path };
        file.Path = path;
        var model = ModelLoader.Load(ModelLocator.Resolve(file.ModelPath));
        var config = request.ConfigPath is { Length: > 0 } cp ? ConfigLoader.Load(Path.GetFullPath(Path.Combine(cwd, cp))) : ConfigLoader.Discover(dir);

        var draft = new TestCase
        {
            Id = "draft",
            Measures = request.Measures.ToList(),
            GroupBy = request.GroupBy?.ToList(),
            Filters = request.Filters is { Count: > 0 } f ? f.ToList() : null,
            AsRole = request.AsRole,
        };
        var resolved = CaseResolver.Resolve(draft, file, model.Snapshot, model.Dependencies, config?.Tests.Tolerance);
        if (resolved.Problems.Count > 0) throw new ArgumentException("Can't build the case: " + string.Join(" ", resolved.Problems));

        var id = string.IsNullOrWhiteSpace(request.CaseId) ? DefaultId(resolved) : request.CaseId.Trim();
        var testCase = new TestCase
        {
            Id = id,
            Description = request.Description,
            Measures = resolved.Measures.Select(x => QueryPlanner.Ref(x.Name)).ToList(),
            GroupBy = resolved.GroupBy.Count > 0 ? resolved.GroupBy.Select(g => QueryPlanner.Column(g.Table, g.Name)).ToList() : null,
            Filters = draft.Filters,
            AsRole = request.AsRole is null ? null : model.Snapshot.Roles.First(r => r.Name.Equals(request.AsRole, StringComparison.OrdinalIgnoreCase)).Name,
        };

        if (request.Mode == AssertionKind.Snapshot)
        {
            var profile = ConnectionProfiles.Resolve(config, request.Profile, request.Environment, file.Profile);
            if (!request.AllowLive && !profile.IsRecorded)
                throw new ArgumentException("Snapshot mode reads the live model, and live queries are disabled on this server (--disable-live).");
            var executor = profile.IsRecorded ? new RecordedDaxExecutor(profile.RecordingsPath!, profile.Database)
                : factory?.Create(profile) ?? throw new ArgumentException("Snapshot mode needs live access, which this host doesn't provide.");
            var withId = resolved with { Case = testCase };
            var query = QueryPlanner.Values(withId, SideDefinitions.Empty, x => x.Name, id);
            var result = await executor.ExecuteAsync(new DaxQueryRequest(query.Text, testCase.AsRole, MeasureTestRunner.MaxRows,
                config?.Tests.TimeoutSeconds ?? DaxQueryRequest.DefaultTimeoutSeconds, query.Label), cancellationToken).ConfigureAwait(false);
            if (result.Truncated) throw new ArgumentException($"The case returns more than {MeasureTestRunner.MaxRows} rows; snapshot fewer rows (add filters).");
            ResultComparer.Index(query, result); // checks the shape
            testCase.Assertions =
            [
                new TestAssertion
                {
                    Type = "snapshot",
                    CapturedAt = (clock ?? (() => DateTimeOffset.UtcNow))().ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    Columns = [.. testCase.GroupBy ?? [], .. testCase.Measures],
                    Rows = result.Rows.Select(r => new YamlRow(r)).ToList(),
                },
            ];
        }
        else
        {
            testCase.Assertions = [new TestAssertion { Type = "differential" }];
        }

        var diagnostics = new List<Diagnostic>();
        var replaced = file.Cases.FindIndex(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        string content;
        if (replaced < 0 && existingText is not null && AppendCase(existingText, testCase) is { } appended)
        {
            content = appended;
            file.Cases.Add(testCase);
        }
        else
        {
            if (replaced >= 0) file.Cases[replaced] = testCase;
            else file.Cases.Add(testCase);
            content = file.ToYaml();
            if (existingText is not null && HasComments(existingText))
                diagnostics.Add(new Diagnostic(DiagnosticLevel.Warning, "TEST010", $"{Path.GetFileName(path)} was rewritten; its comments aren't kept."));
        }

        MeasureTestFile.Parse(content, path); // never write a file we can't read back
        var write = writer.Write(path, content, dryRun: !request.Save);
        return new GenerateResult(path, id, replaced >= 0, file.Cases.Select(x => x.Id!).ToList(), content, write, diagnostics.Count > 0 ? diagnostics : null);
    }

    /// <summary>A readable id from the measures and group-by columns, e.g. <c>total-sales-margin-by-year-region</c>.</summary>
    public static string DefaultId(ResolvedCase c)
    {
        var text = string.Join(" ", c.Measures.Select(m => m.Name));
        if (c.GroupBy.Count > 0) text += " by " + string.Join(" ", c.GroupBy.Select(g => g.Name));
        var slug = Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return slug.Length == 0 || !char.IsLetterOrDigit(slug[0]) ? "case" : slug;
    }

    /// <summary>Appends a case to the text of an existing file when <c>cases:</c> is its last top-level key, keeping comments.</summary>
    private static string? AppendCase(string text, TestCase testCase)
    {
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = lf.Split('\n');
        var last = Array.FindLastIndex(lines, l => Regex.IsMatch(l, @"^[A-Za-z_][A-Za-z0-9_]*\s*:"));
        if (last < 0 || !Regex.IsMatch(lines[last], @"^cases\s*:\s*(\[\s*\])?\s*(#.*)?$")) return null;
        if (lines[last].Contains('[', StringComparison.Ordinal)) lines[last] = "cases:";
        var sb = new StringBuilder(string.Join('\n', lines).TrimEnd('\n')).Append('\n');
        sb.Append(MeasureTestFile.CaseYaml(testCase));
        return sb.ToString();
    }

    /// <summary>True when the YAML has comments other than the schema line we write ourselves.</summary>
    private static bool HasComments(string text)
    {
        var scanner = new YamlDotNet.Core.Scanner(new StringReader(text), skipComments: false);
        try
        {
            while (scanner.MoveNext())
                if (scanner.Current is YamlDotNet.Core.Tokens.Comment c && !c.Value.TrimStart().StartsWith("yaml-language-server", StringComparison.Ordinal))
                    return true;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return true; // can't tell; warn
        }
        return false;
    }
}
