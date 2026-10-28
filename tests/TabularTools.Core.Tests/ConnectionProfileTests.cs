using TabularTools.Core.Config;
using TabularTools.Core.Queries;

namespace TabularTools.Core.Tests;

public sealed class ConnectionProfileTests
{
    private static TabularToolsConfig Config(string yaml, string? path = null)
    {
        var config = ConfigLoader.Parse(yaml, "test");
        config.Path = path ?? Path.Combine(Path.GetTempPath(), "cfg", ConfigLoader.FileName);
        return config;
    }

    private const string Profiles = """
        profiles:
          dev:
            endpoint: powerbi://api.powerbi.com/v1.0/myorg/Dev
            database: Sales
          ci:
            endpoint: powerbi://api.powerbi.com/v1.0/myorg/CI
            database: Sales
            auth: azureCli
          offline:
            recordings: rec/sales.recordings.yaml
        tests:
          profile: dev
        """;

    [Fact]
    public void Precedence_is_argument_then_environment_then_test_file_then_config()
    {
        var config = Config(Profiles);
        Func<string, string?> none = _ => null;
        Func<string, string?> env = n => n == ConnectionProfiles.ProfileVariable ? "ci" : null;

        Assert.Equal("offline", ConnectionProfiles.Resolve(config, "offline", env, "dev").Name);
        Assert.Equal("ci", ConnectionProfiles.Resolve(config, null, env, "offline").Name);
        Assert.Equal("offline", ConnectionProfiles.Resolve(config, null, none, "offline").Name);
        var dev = ConnectionProfiles.Resolve(config, null, none);
        Assert.Equal(("dev", AuthMode.Default, false), (dev.Name, dev.Auth, dev.IsRecorded));
        Assert.Equal(new DaxTarget("powerbi://api.powerbi.com/v1.0/myorg/Dev", "Sales"), dev.Target);
        Assert.Equal(AuthMode.AzureCli, ConnectionProfiles.Resolve(config, "ci", none).Auth);
    }

    [Fact]
    public void Recordings_paths_resolve_against_the_config_folder()
    {
        var config = Config(Profiles);
        var offline = ConnectionProfiles.Resolve(config, "offline", _ => null);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(config.Path)!, "rec", "sales.recordings.yaml"), offline.RecordingsPath);
        Assert.StartsWith("recorded:", offline.Target.Endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_or_unknown_profiles_say_what_to_do()
    {
        Assert.Contains("TABULARTOOLS_PROFILE", Assert.Throws<ArgumentException>(() => ConnectionProfiles.Resolve(Config("schemaVersion: 1"), null, _ => null)).Message, StringComparison.Ordinal);
        Assert.Contains("Defined: ci, dev, offline", Assert.Throws<ArgumentException>(() => ConnectionProfiles.Resolve(Config(Profiles), "prod", _ => null)).Message, StringComparison.Ordinal);
        Assert.Contains("no tabulartools.yaml", Assert.Throws<ArgumentException>(() => ConnectionProfiles.Resolve(null, "dev", _ => null)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("database: X", "endpoint is required")]
    [InlineData("endpoint: powerbi://api.powerbi.com/v1.0/myorg/X", "database is required")]
    [InlineData("endpoint: Data Source=powerbi://x;Password=secret\n    database: X", "isn't a powerbi://")]
    [InlineData("endpoint: http://example.com\n    database: X", "isn't a powerbi://")]
    [InlineData("endpoint: powerbi://api.powerbi.com/v1.0/myorg/X\n    database: X\n    auth: magic", "auth 'magic'")]
    [InlineData("endpoint: powerbi://api.powerbi.com/v1.0/myorg/X\n    database: X\n    auth: token", "needs tokenEnv")]
    [InlineData("endpoint: powerbi://api.powerbi.com/v1.0/myorg/X\n    database: X\n    auth: token\n    tokenEnv: 'not a name'", "needs tokenEnv")]
    [InlineData("endpoint: localhost:1234\n    database: X\n    recordings: r.yaml", "not both")]
    public void Invalid_profiles_fail_when_the_config_loads(string body, string message)
    {
        using var temp = new TempDir();
        var path = temp.Combine(ConfigLoader.FileName);
        File.WriteAllText(path, "profiles:\n  p:\n    " + body + "\n");
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Load(path));
        Assert.Contains("profiles.p", ex.Message, StringComparison.Ordinal);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_config_adds_and_replaces_profiles_and_test_settings()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine(ConfigLoader.FileName), Profiles + "\n  maxParallel: 2\nlive:\n  allowedHosts: [a.example.com]\n");
        File.WriteAllText(temp.Combine(ConfigLoader.LocalFileName), """
            profiles:
              dev:
                endpoint: powerbi://api.powerbi.com/v1.0/myorg/My%20Sandbox
                database: Sales
            tests:
              profile: ci
            live:
              allowRunDax: false
              allowedHosts: ["*.example.org"]
            """);
        var config = ConfigLoader.Load(temp.Combine(ConfigLoader.FileName));
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/My%20Sandbox", config.Profiles!["dev"].Endpoint);
        Assert.Equal(3, config.Profiles.Count);
        Assert.Equal("ci", config.Tests.Profile);
        Assert.Equal(2, config.Tests.MaxParallel);
        Assert.False(config.Live.AllowRunDax);
        Assert.Equal(["a.example.com", "*.example.org"], config.Live.AllowedHosts);
    }

    [Theory]
    [InlineData("tests:\n  maxParallel: 0", "maxParallel")]
    [InlineData("tests:\n  timeoutSeconds: 0", "timeoutSeconds")]
    [InlineData("tests:\n  tolerance: { relative: -1 }", "negative")]
    [InlineData("live:\n  allowedHosts: ['bad host']", "allowedHosts")]
    public void Invalid_test_and_live_settings_are_rejected(string yaml, string message)
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine(ConfigLoader.FileName), yaml);
        Assert.Contains(message, Assert.Throws<ConfigException>(() => ConfigLoader.Load(temp.Combine(ConfigLoader.FileName))).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Sales%20Dev", EndpointKind.PowerBi, "api.powerbi.com")]
    [InlineData("asazure://westeurope.asazure.windows.net/server", EndpointKind.AzureAnalysisServices, "westeurope.asazure.windows.net")]
    [InlineData("localhost:51234", EndpointKind.Localhost, "localhost")]
    [InlineData("desktop", EndpointKind.Desktop, "localhost")]
    public void Endpoints_parse(string endpoint, EndpointKind kind, string host)
    {
        var parsed = EndpointPolicy.Parse(endpoint)!;
        Assert.Equal((kind, host), (parsed.Kind, parsed.Host));
    }

    [Fact]
    public void Only_allowed_hosts_get_tokens_and_local_engines_need_windows()
    {
        Assert.Equal(EndpointKind.PowerBi, EndpointPolicy.Check("powerbi://api.powerbi.com/v1.0/myorg/X", null, isWindows: false).Kind);
        Assert.Contains("isn't allowed", Assert.Throws<ArgumentException>(() => EndpointPolicy.Check("powerbi://api.powerbi.com.evil.example/v1.0/myorg/X", null, false)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => EndpointPolicy.Check("powerbi://powerbi.com/x", null, false)); // the suffix needs a subdomain
        Assert.Equal(EndpointKind.PowerBi, EndpointPolicy.Check("powerbi://api.powerbigov.us/v1.0/myorg/X", ["*.powerbigov.us"], false).Kind);
        Assert.Equal(EndpointKind.PowerBi, EndpointPolicy.Check("powerbi://xmla.example.com/x", ["xmla.example.com"], false).Kind);
        Assert.Throws<LiveException>(() => EndpointPolicy.Check("localhost:1234", null, isWindows: false));
        Assert.Equal(EndpointKind.Localhost, EndpointPolicy.Check("localhost:1234", null, isWindows: true).Kind);
        Assert.Equal("https://analysis.windows.net/powerbi/api/.default", EndpointPolicy.TokenScope(EndpointPolicy.Parse("powerbi://api.powerbi.com/x")!));
        Assert.Equal("https://*.asazure.windows.net/.default", EndpointPolicy.TokenScope(EndpointPolicy.Parse("asazure://westus.asazure.windows.net/s")!));
    }
}
