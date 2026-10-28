using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TabularTools.Core.Writes;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TabularTools.Core.Queries;

/// <summary>
/// Recorded query results (<c>*.recordings.yaml</c>) for the offline executor. One file holds one target. Entries are
/// matched by the SHA-256 of the canonical query text plus the role, so they can be hand-written: write the query and
/// its expected result.
/// </summary>
public sealed class RecordingsFile
{
    public int SchemaVersion { get; set; } = 1;
    public RecordingTarget? Target { get; set; }
    public List<Recording> Recordings { get; set; } = [];

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(YamlRowConverter.Instance)
        .Build();

    public static RecordingsFile Load(string path)
    {
        if (!File.Exists(path)) throw new ArgumentException($"Recordings file not found: {path}");
        RecordingsFile? file;
        try
        {
            file = Deserializer.Deserialize<RecordingsFile?>(File.ReadAllText(path));
        }
        catch (YamlException ex)
        {
            throw new ArgumentException($"{path}({ex.Start.Line},{ex.Start.Column}): {(ex.InnerException ?? ex).Message}", ex);
        }
        file ??= new RecordingsFile();
        if (file.SchemaVersion != 1) throw new ArgumentException($"{path}: unsupported schemaVersion {file.SchemaVersion} (expected 1).");
        for (var i = 0; i < file.Recordings.Count; i++)
        {
            var r = file.Recordings[i];
            if (string.IsNullOrWhiteSpace(r.Query)) throw new ArgumentException($"{path}: recordings[{i}] has no query.");
            if (r.Error is null && r.Columns.Count == 0) throw new ArgumentException($"{path}: recordings[{i}] ({r.Label ?? "unlabelled"}) needs columns or an error.");
            foreach (var row in r.Rows)
                if (row.Values.Count != r.Columns.Count)
                    throw new ArgumentException($"{path}: recordings[{i}] ({r.Label ?? "unlabelled"}) has a row with {row.Values.Count} values for {r.Columns.Count} columns.");
        }
        return file;
    }

    /// <summary>Canonical query text: LF line endings, no trailing whitespace, no leading or trailing blank lines.</summary>
    public static string Canonical(string query) =>
        string.Join('\n', query.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd())).Trim('\n');

    public static string Key(string query, string? role)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(query) + "\n#role:" + (role ?? "")));
        return Convert.ToHexStringLower(bytes);
    }

    public string ToYaml()
    {
        var sb = new StringBuilder();
        sb.Append("# Recorded DAX query results, replayed by TabularTools instead of querying a live model.\n");
        sb.Append("# Entries match on the query text (whitespace at line ends ignored) and role.\n");
        sb.Append("schemaVersion: 1\n");
        if (Target is not null)
        {
            sb.Append("target:\n");
            sb.Append("  endpoint: ").Append(YamlText.Quote(Target.Endpoint ?? "")).Append('\n');
            sb.Append("  database: ").Append(YamlText.Quote(Target.Database ?? "")).Append('\n');
        }
        sb.Append("recordings:");
        if (Recordings.Count == 0) sb.Append(" []");
        sb.Append('\n');
        foreach (var r in Recordings)
        {
            sb.Append("  - label: ").Append(YamlText.Quote(r.Label ?? "")).Append('\n');
            if (r.Role is not null) sb.Append("    role: ").Append(YamlText.Quote(r.Role)).Append('\n');
            if (r.RecordedAt is not null) sb.Append("    recordedAt: ").Append(YamlText.Quote(r.RecordedAt)).Append('\n');
            sb.Append("    query: ").Append(YamlText.Block(Canonical(r.Query ?? ""), 6)).Append('\n');
            if (r.Error is not null)
            {
                sb.Append("    error: ").Append(YamlText.Quote(r.Error)).Append('\n');
                continue;
            }
            sb.Append("    columns:\n");
            foreach (var c in r.Columns)
                sb.Append("      - { name: ").Append(YamlText.Quote(c.Name ?? "")).Append(", dataType: ").Append(c.DataType ?? "other").Append(" }\n");
            sb.Append("    rows:");
            if (r.Rows.Count == 0) sb.Append(" []");
            sb.Append('\n');
            foreach (var row in r.Rows) sb.Append("      - ").Append(YamlText.Row(row.Values)).Append('\n');
        }
        return sb.ToString();
    }
}

public sealed class RecordingTarget
{
    public string? Endpoint { get; set; }
    public string? Database { get; set; }
}

public sealed class Recording
{
    /// <summary>What the query was for, e.g. <c>sales-by-region/head</c>. Informational.</summary>
    public string? Label { get; set; }
    public string? Role { get; set; }
    public string? RecordedAt { get; set; }
    public string? Query { get; set; }
    public List<RecordedColumn> Columns { get; set; } = [];
    public List<YamlRow> Rows { get; set; } = [];
    /// <summary>Engine error message to replay instead of a result.</summary>
    public string? Error { get; set; }
}

public sealed class RecordedColumn
{
    public string? Name { get; set; }
    public string? DataType { get; set; }
}

/// <summary>Offline executor for CI: replays results from a recordings file. Unknown queries fail with the query's key and label.</summary>
public sealed class RecordedDaxExecutor : IDaxExecutor
{
    private readonly Dictionary<string, Recording> _byKey = new(StringComparer.Ordinal);
    private readonly string _path;

    public RecordedDaxExecutor(string path, string? database = null)
    {
        _path = path;
        var file = RecordingsFile.Load(path);
        foreach (var r in file.Recordings) _byKey[RecordingsFile.Key(r.Query!, r.Role)] = r;
        Target = new DaxTarget("recorded:" + Path.GetFileName(path), file.Target?.Database ?? database ?? "");
    }

    public DaxTarget Target { get; }

    public Task<DaxResult> ExecuteAsync(DaxQueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DaxQueryGuard.Check(request.Query);
        var key = RecordingsFile.Key(request.Query, request.Role);
        if (!_byKey.TryGetValue(key, out var r))
            throw new DaxQueryException(
                $"No recording for this query{(request.Label is null ? "" : $" ({request.Label})")} in {Path.GetFileName(_path)} (key {key[..12]}). " +
                "Re-record against a live model with 'tabulartools test run --record <file>', or add the entry by hand.");
        if (r.Error is not null) throw new DaxQueryException(r.Error);

        var rows = r.Rows.Select(row => (IReadOnlyList<object?>)row.Values.Select(DaxValues.Normalize).ToList()).ToList();
        var max = Math.Max(0, request.MaxRows);
        return Task.FromResult(new DaxResult
        {
            Columns = r.Columns.Select(c => new DaxColumn(c.Name ?? "", c.DataType ?? "other")).ToList(),
            Rows = rows.Take(max).ToList(),
            Truncated = rows.Count > max,
            DurationMs = 0,
            Target = Target,
            Recorded = true,
        });
    }
}

/// <summary>Wraps a live executor and keeps every result, so <see cref="Save"/> can write a recordings file.</summary>
public sealed class RecordingDaxExecutor(IDaxExecutor inner, Func<DateTimeOffset>? clock = null) : IDaxExecutor
{
    private readonly object _gate = new();
    private readonly List<(string Key, Recording Recording)> _captured = [];
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public DaxTarget Target => inner.Target;

    public int Count { get { lock (_gate) return _captured.Count; } }

    public async Task<DaxResult> ExecuteAsync(DaxQueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var recording = new Recording
        {
            Label = request.Label,
            Role = request.Role,
            RecordedAt = _clock().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Query = RecordingsFile.Canonical(request.Query),
        };
        try
        {
            // Record the full result; the caller's row cap is applied on replay.
            var result = await inner.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            recording.Columns = result.Columns.Select(c => new RecordedColumn { Name = c.Name, DataType = c.DataType }).ToList();
            recording.Rows = result.Rows.Select(r => new YamlRow(r)).ToList();
            Add(request, recording);
            return result;
        }
        catch (DaxQueryException ex)
        {
            recording.Error = ex.Message;
            Add(request, recording);
            throw;
        }
    }

    private void Add(DaxQueryRequest request, Recording recording)
    {
        lock (_gate) _captured.Add((RecordingsFile.Key(request.Query, request.Role), recording));
    }

    /// <summary>Merges the captured results into <paramref name="path"/> (same query and role replaced) and writes it.</summary>
    public WriteResult Save(string path, FileWriter writer, bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var file = File.Exists(path) ? RecordingsFile.Load(path) : new RecordingsFile();
        file.Target = new RecordingTarget { Endpoint = Target.Endpoint, Database = Target.Database };
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < file.Recordings.Count; i++) byKey[RecordingsFile.Key(file.Recordings[i].Query!, file.Recordings[i].Role)] = i;
        lock (_gate)
        {
            foreach (var (key, recording) in _captured.OrderBy(c => c.Recording.Label, StringComparer.Ordinal))
            {
                if (byKey.TryGetValue(key, out var index)) file.Recordings[index] = recording;
                else { byKey[key] = file.Recordings.Count; file.Recordings.Add(recording); }
            }
        }
        return writer.Write(path, file.ToYaml(), dryRun);
    }
}
