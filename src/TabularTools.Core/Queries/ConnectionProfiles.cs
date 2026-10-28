using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TabularTools.Core.Config;

namespace TabularTools.Core.Queries;

/// <summary>How a profile gets its Entra ID token (ARCHITECTURE §13.2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AuthMode>))]
public enum AuthMode
{
    /// <summary>Environment → workload identity → Azure CLI → (Windows, interactive sessions only) browser.</summary>
    [JsonStringEnumMemberName("default")] Default,
    [JsonStringEnumMemberName("azureCli")] AzureCli,
    /// <summary>AZURE_TENANT_ID, AZURE_CLIENT_ID and AZURE_CLIENT_SECRET or AZURE_CLIENT_CERTIFICATE_PATH.</summary>
    [JsonStringEnumMemberName("servicePrincipal")] ServicePrincipal,
    [JsonStringEnumMemberName("interactive")] Interactive,
    [JsonStringEnumMemberName("deviceCode")] DeviceCode,
    /// <summary>An access token from the environment variable named by <c>tokenEnv</c>.</summary>
    [JsonStringEnumMemberName("token")] Token,
    /// <summary>No token (Power BI Desktop and other local engines).</summary>
    [JsonStringEnumMemberName("none")] None,
}

/// <summary>A profile ready to connect with: names resolved, paths absolute. Holds no secrets.</summary>
public sealed record ResolvedProfile
{
    public required string Name { get; init; }
    public string? Endpoint { get; init; }
    public string? Database { get; init; }
    public AuthMode Auth { get; init; }
    public string? TokenEnv { get; init; }
    /// <summary>Absolute path of a recordings file; when set, queries are replayed instead of sent.</summary>
    public string? RecordingsPath { get; init; }
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];
    public bool IsRecorded => RecordingsPath is not null;

    public DaxTarget Target => IsRecorded
        ? new DaxTarget("recorded:" + Path.GetFileName(RecordingsPath!), Database ?? "")
        : new DaxTarget(Endpoint!, Database!);
}

public static class ConnectionProfiles
{
    /// <summary>Environment variable that picks the profile when none is given.</summary>
    public const string ProfileVariable = "TABULARTOOLS_PROFILE";

    private static readonly Regex EnvName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Picks and resolves a profile. Precedence: <paramref name="explicitName"/> (argument) → TABULARTOOLS_PROFILE →
    /// <paramref name="fileProfile"/> (test file) → <c>tests.profile</c> in config.
    /// </summary>
    public static ResolvedProfile Resolve(TabularToolsConfig? config, string? explicitName, Func<string, string?>? environment = null, string? fileProfile = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var name = First(explicitName, environment(ProfileVariable), fileProfile, config?.Tests.Profile)
            ?? throw new ArgumentException("No connection profile given. Pass a profile name, set TABULARTOOLS_PROFILE, or set tests.profile in tabulartools.yaml.");
        var profiles = config?.Profiles;
        if (profiles is null || profiles.Count == 0)
            throw new ArgumentException($"Profile '{name}' not found: {(config?.Path is null ? "no tabulartools.yaml was found" : config.Path + " defines no profiles")}.");
        if (!profiles.TryGetValue(name, out var settings) || settings is null)
            throw new ArgumentException($"Profile '{name}' isn't defined in {config!.Path ?? ConfigLoader.FileName}. Defined: {string.Join(", ", profiles.Keys.Order(StringComparer.Ordinal))}.");
        Validate(name, settings);

        var recordings = settings.Recordings is { Length: > 0 } r
            ? Path.GetFullPath(Path.Combine(config!.Directory ?? Directory.GetCurrentDirectory(), r))
            : null;
        return new ResolvedProfile
        {
            Name = name,
            Endpoint = settings.Endpoint?.Trim(),
            Database = settings.Database?.Trim(),
            Auth = ParseAuth(settings.Auth),
            TokenEnv = settings.TokenEnv,
            RecordingsPath = recordings,
            AllowedHosts = config?.Live.AllowedHosts ?? [],
        };
    }

    /// <summary>Checks one profile's settings; throws <see cref="ArgumentException"/> naming the problem.</summary>
    public static void Validate(string name, ProfileSettings settings)
    {
        var where = $"profiles.{name}";
        if (settings.Recordings is { Length: > 0 })
        {
            if (settings.Endpoint is { Length: > 0 }) throw new ArgumentException($"{where}: set either endpoint or recordings, not both.");
            return;
        }
        if (string.IsNullOrWhiteSpace(settings.Endpoint)) throw new ArgumentException($"{where}: endpoint is required (or recordings, to replay results offline).");
        if (string.IsNullOrWhiteSpace(settings.Database)) throw new ArgumentException($"{where}: database is required.");
        if (EndpointPolicy.Parse(settings.Endpoint) is null)
            throw new ArgumentException($"{where}: endpoint '{settings.Endpoint}' isn't a powerbi://, asazure:// or localhost:<port> address, or 'desktop'. Put connection details here, never secrets.");
        if (settings.Endpoint.Contains(';', StringComparison.Ordinal) || settings.Endpoint.Contains('=', StringComparison.Ordinal))
            throw new ArgumentException($"{where}: endpoint must be an address, not a connection string.");
        var auth = ParseAuth(settings.Auth, where);
        if (auth == AuthMode.None && EndpointPolicy.Parse(settings.Endpoint)!.Kind is not (EndpointKind.Localhost or EndpointKind.Desktop))
            throw new ArgumentException($"{where}: auth 'none' only works with local engines (localhost or desktop).");
        if (auth == AuthMode.Token && (settings.TokenEnv is null || !EnvName.IsMatch(settings.TokenEnv)))
            throw new ArgumentException($"{where}: auth 'token' needs tokenEnv, the name of an environment variable holding the token.");
        if (settings.TokenEnv is { } env && !EnvName.IsMatch(env))
            throw new ArgumentException($"{where}: tokenEnv '{env}' isn't an environment variable name.");
    }

    public static AuthMode ParseAuth(string? text, string where = "auth") => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "default" => AuthMode.Default,
        "azurecli" => AuthMode.AzureCli,
        "serviceprincipal" => AuthMode.ServicePrincipal,
        "interactive" => AuthMode.Interactive,
        "devicecode" => AuthMode.DeviceCode,
        "token" => AuthMode.Token,
        "none" => AuthMode.None,
        _ => throw new ArgumentException($"{where}: auth '{text}' isn't default, azureCli, servicePrincipal, interactive, deviceCode, token or none."),
    };

    private static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}

public enum EndpointKind { PowerBi, AzureAnalysisServices, Localhost, Desktop }

/// <param name="Host">Host name (lower case); <c>localhost</c> for local engines.</param>
public sealed record ParsedEndpoint(EndpointKind Kind, string Host, int? Port = null);

/// <summary>Which endpoints tokens and queries may go to (ARCHITECTURE §14.5).</summary>
public static class EndpointPolicy
{
    public static IReadOnlyList<string> DefaultAllowedHosts { get; } = ["*.powerbi.com", "*.asazure.windows.net"];

    private static readonly Regex HostPattern = new(@"^(\*\.)?[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*$", RegexOptions.CultureInvariant);

    public static bool IsValidHostPattern(string pattern) => HostPattern.IsMatch(pattern);

    public static ParsedEndpoint? Parse(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        var e = endpoint.Trim();
        if (e.Equals("desktop", StringComparison.OrdinalIgnoreCase)) return new ParsedEndpoint(EndpointKind.Desktop, "localhost");

        var local = Regex.Match(e, @"^(localhost|127\.0\.0\.1|\[::1\])(:(\d{1,5}))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (local.Success)
            return new ParsedEndpoint(EndpointKind.Localhost, "localhost", local.Groups[3].Success ? int.Parse(local.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : null);

        if (!Uri.TryCreate(e, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        return uri.Scheme.ToLowerInvariant() switch
        {
            "powerbi" => new ParsedEndpoint(EndpointKind.PowerBi, uri.Host.ToLowerInvariant()),
            "asazure" => new ParsedEndpoint(EndpointKind.AzureAnalysisServices, uri.Host.ToLowerInvariant()),
            _ => null,
        };
    }

    /// <summary>
    /// Throws when the endpoint isn't allowed: an unknown host (a config error) or a local engine on a non-Windows OS
    /// (an environment error: ADOMD can't reach local engines there).
    /// </summary>
    public static ParsedEndpoint Check(string endpoint, IEnumerable<string>? extraHosts, bool isWindows)
    {
        var parsed = Parse(endpoint) ?? throw new ArgumentException($"Endpoint '{endpoint}' isn't a powerbi://, asazure:// or localhost:<port> address.");
        if (parsed.Kind is EndpointKind.Localhost or EndpointKind.Desktop)
        {
            if (!isWindows)
                throw new LiveException($"'{endpoint}' is a local engine, which ADOMD.NET can only reach on Windows.",
                    "Use a powerbi:// workspace endpoint, or run on Windows.");
            return parsed;
        }
        if (!IsAllowedHost(parsed.Host, extraHosts))
            throw new ArgumentException($"Host '{parsed.Host}' isn't allowed. Tokens and queries only go to *.powerbi.com, *.asazure.windows.net and hosts listed in live.allowedHosts.");
        return parsed;
    }

    public static bool IsAllowedHost(string host, IEnumerable<string>? extraHosts)
    {
        foreach (var pattern in DefaultAllowedHosts.Concat(extraHosts ?? []))
        {
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = pattern[1..];
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host.Length > suffix.Length) return true;
            }
            else if (host.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>OAuth scope for an endpoint's token.</summary>
    public static string TokenScope(ParsedEndpoint endpoint) => endpoint.Kind switch
    {
        EndpointKind.AzureAnalysisServices => "https://*.asazure.windows.net/.default",
        _ => "https://analysis.windows.net/powerbi/api/.default",
    };
}
