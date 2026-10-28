using System.Diagnostics;
using Microsoft.AnalysisServices.AdomdClient;
using TabularTools.Core.Queries;
using ASAccessToken = Microsoft.AnalysisServices.AccessToken;

namespace TabularTools.Live;

/// <summary>
/// Runs read-only DAX queries over XMLA with ADOMD.NET (ARCHITECTURE §11.5). Never opens a connection for anything
/// <see cref="DaxQueryGuard"/> rejects, and has no code path to commands, processing or metadata writes.
/// </summary>
public sealed class XmlaDaxExecutor : IDaxExecutor
{
    private readonly ResolvedProfile _profile;
    private readonly ParsedEndpoint _endpoint;
    private readonly ITokenProvider? _tokens;
    private readonly string _dataSource;

    public XmlaDaxExecutor(ResolvedProfile profile, ITokenProvider? tokens, bool? isWindows = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.IsRecorded) throw new ArgumentException("Recorded profiles use RecordedDaxExecutor.");
        _profile = profile;
        _endpoint = EndpointPolicy.Check(profile.Endpoint!, profile.AllowedHosts, isWindows ?? OperatingSystem.IsWindows());
        _dataSource = _endpoint.Kind switch
        {
            EndpointKind.Desktop => "localhost:" + (DesktopInstances.FindPorts().FirstOrDefault() is var port and > 0 ? port
                : throw new LiveException("No running Power BI Desktop model found.", "Open the PBIP in Power BI Desktop first.")),
            _ => profile.Endpoint!,
        };
        _tokens = _endpoint.Kind is EndpointKind.Localhost or EndpointKind.Desktop || profile.Auth == AuthMode.None ? null
            : tokens ?? throw new ArgumentException("A token provider is needed for cloud endpoints.");
        Target = profile.Target;
    }

    public DaxTarget Target { get; }

    public async Task<DaxResult> ExecuteAsync(DaxQueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DaxQueryGuard.Check(request.Query);
        var token = _tokens is null ? (Azure.Core.AccessToken?)null : await _tokens.GetTokenAsync(EndpointPolicy.TokenScope(_endpoint), cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Execute(request, token, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private DaxResult Execute(DaxQueryRequest request, Azure.Core.AccessToken? token, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var connection = new AdomdConnection(ConnectionString(request.Role));
        if (token is { } t)
        {
            connection.AccessToken = new ASAccessToken(t.Token, t.ExpiresOn);
            var scope = EndpointPolicy.TokenScope(_endpoint);
            connection.OnAccessTokenExpired = _ =>
            {
                var fresh = _tokens!.GetTokenAsync(scope, CancellationToken.None).GetAwaiter().GetResult();
                return new ASAccessToken(fresh.Token, fresh.ExpiresOn);
            };
        }

        try
        {
            connection.Open();
        }
        catch (Exception ex) when (ex is AdomdConnectionException or AdomdErrorResponseException or AdomdException or PlatformNotSupportedException or NotSupportedException)
        {
            throw new LiveException($"Couldn't connect to {_profile.Endpoint} (database '{_profile.Database}'): {ex.Message}",
                "Check the endpoint, that XMLA read is enabled on the capacity, and that the identity has Build permission on the model.", ex);
        }

        using var command = connection.CreateCommand();
        command.CommandText = request.Query;
        command.CommandTimeout = Math.Max(1, request.TimeoutSeconds);
        using var registration = cancellationToken.Register(() => { try { command.Cancel(); } catch (AdomdException) { } });
        try
        {
            using var reader = command.ExecuteReader();
            var columns = Enumerable.Range(0, reader.FieldCount)
                .Select(i => new DaxColumn(reader.GetName(i), DaxValues.TypeName(SafeType(reader, i))))
                .ToList();
            var rows = new List<IReadOnlyList<object?>>();
            var truncated = false;
            while (reader.Read())
            {
                if (rows.Count >= request.MaxRows) { truncated = true; break; }
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++) row[i] = DaxValues.Normalize(reader.GetValue(i));
                rows.Add(row);
            }
            return new DaxResult { Columns = columns, Rows = rows, Truncated = truncated, DurationMs = watch.ElapsedMilliseconds, Target = Target };
        }
        catch (AdomdErrorResponseException ex)
        {
            throw new DaxQueryException(ex.Message, ex);
        }
        catch (AdomdConnectionException ex)
        {
            throw new LiveException($"Lost the connection to {_profile.Endpoint}: {ex.Message}", null, ex);
        }
    }

    private static Type? SafeType(AdomdDataReader reader, int i)
    {
        try { return reader.GetFieldType(i); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Endpoint and database come from the profile, never from user text, so nothing can be injected here.</summary>
    internal string ConnectionString(string? role)
    {
        var parts = new List<string> { $"Data Source={Quote(_dataSource)}" };
        if (!string.IsNullOrEmpty(_profile.Database)) parts.Add($"Initial Catalog={Quote(_profile.Database)}");
        if (role is not null) parts.Add($"Roles={Quote(role)}");
        parts.Add("Application Name=TabularTools");
        return string.Join(";", parts);
    }

    /// <summary>Connection string value quoting: wrap in double quotes, doubling any inside.</summary>
    internal static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
