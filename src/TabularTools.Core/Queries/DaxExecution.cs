using System.Text.Json.Serialization;

namespace TabularTools.Core.Queries;

/// <summary>Where queries go: a live endpoint and database, or a recordings file.</summary>
/// <param name="Endpoint">XMLA endpoint, or <c>recorded:&lt;file&gt;</c> for replayed results.</param>
public sealed record DaxTarget(string Endpoint, string Database);

/// <param name="Query">DAX query or $SYSTEM DMV select. Checked by <see cref="DaxQueryGuard"/>.</param>
/// <param name="Role">Evaluate as this RLS role (needs admin rights on the model).</param>
/// <param name="MaxRows">Rows returned at most; more set <see cref="DaxResult.Truncated"/>.</param>
/// <param name="Label">What the query is for, e.g. <c>case-id/base</c>. Stored with recordings; not sent.</param>
public sealed record DaxQueryRequest(string Query, string? Role = null, int MaxRows = DaxQueryRequest.DefaultMaxRows, int TimeoutSeconds = DaxQueryRequest.DefaultTimeoutSeconds, string? Label = null)
{
    public const int DefaultMaxRows = 500;
    public const int DefaultTimeoutSeconds = 120;
}

/// <param name="Name">Column name as the engine returns it, e.g. <c>Date[Year]</c> or <c>[Total Sales]</c>.</param>
/// <param name="DataType">int64, double, decimal, string, boolean, dateTime, or other.</param>
public sealed record DaxColumn(string Name, string DataType);

/// <summary>A query result. Values are null (BLANK), bool, long, decimal, double, or string (dates as ISO 8601, non-finite numbers as "Infinity"/"NaN").</summary>
public sealed record DaxResult
{
    public required IReadOnlyList<DaxColumn> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }
    public int RowCount => Rows.Count;
    public bool Truncated { get; init; }
    public long DurationMs { get; init; }
    public required DaxTarget Target { get; init; }
    /// <summary>True when the result was replayed from a recordings file.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Recorded { get; init; }
}

/// <summary>Runs read-only queries against one target. Implementations must call <see cref="DaxQueryGuard.Check"/>.</summary>
public interface IDaxExecutor
{
    DaxTarget Target { get; }

    /// <summary>Runs the query. Throws <see cref="DaxQueryException"/> when the engine rejects it and <see cref="LiveException"/> for connection or auth problems.</summary>
    Task<DaxResult> ExecuteAsync(DaxQueryRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Creates executors for resolved profiles. Hosts supply it (TabularTools.Live); Core only handles recordings itself.</summary>
public interface IDaxExecutorFactory
{
    IDaxExecutor Create(ResolvedProfile profile);
}

/// <summary>The engine rejected or failed the query (syntax, unknown column, a DAX error). Reported per query, not fatal.</summary>
public sealed class DaxQueryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Can't reach or authenticate to the live model. Treated as an environment error (CLI exit code 4).</summary>
public sealed class LiveException(string message, string? hint = null, Exception? inner = null)
    : Exception(hint is null ? message : $"{message} {hint}", inner)
{
    public string? Hint { get; } = hint;
}
