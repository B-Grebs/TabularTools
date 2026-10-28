using Azure.Core;
using TabularTools.Core.Config;
using TabularTools.Core.Queries;

namespace TabularTools.Live.Tests;

public sealed class TokenProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ResolvedProfile Profile(AuthMode auth, string endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS") =>
        new() { Name = "p", Endpoint = endpoint, Database = "Sales", Auth = auth, TokenEnv = "MY_TOKEN" };

    [Fact]
    public async Task Token_mode_reads_the_named_environment_variable()
    {
        var provider = TokenProviders.For(Profile(AuthMode.Token), name => name == "MY_TOKEN" ? " abc " : null);
        var token = await provider.GetTokenAsync("scope", Ct);
        Assert.Equal("abc", token.Token);

        var empty = TokenProviders.For(Profile(AuthMode.Token), _ => null);
        var ex = await Assert.ThrowsAsync<LiveException>(() => empty.GetTokenAsync("scope", Ct));
        Assert.Contains("MY_TOKEN is empty", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AuthMode.AzureCli, "AzureCliCredential")]
    [InlineData(AuthMode.ServicePrincipal, "EnvironmentCredential")]
    [InlineData(AuthMode.DeviceCode, "DeviceCodeCredential")]
    [InlineData(AuthMode.Interactive, "InteractiveBrowserCredential")]
    [InlineData(AuthMode.Default, "ChainedTokenCredential")]
    public void Each_mode_maps_to_its_credential(AuthMode mode, string type) =>
        Assert.Equal(type, TokenProviders.Credential(mode, _ => null, interactiveAllowed: true).GetType().Name);

    [Theory]
    [InlineData("TF_BUILD", true)]
    [InlineData("GITHUB_ACTIONS", true)]
    [InlineData("CI", true)]
    [InlineData("HOME", false)]
    public void Build_agents_are_detected(string variable, bool ci) =>
        Assert.Equal(ci, TokenProviders.IsCi(n => n == variable ? "true" : null));

    [Fact]
    public async Task Failed_sign_in_becomes_a_live_error_with_a_hint()
    {
        // A service principal with no environment variables can't authenticate; no network call is made.
        var provider = TokenProviders.For(Profile(AuthMode.ServicePrincipal), _ => null);
        var ex = await Assert.ThrowsAsync<LiveException>(() => provider.GetTokenAsync("https://analysis.windows.net/powerbi/api/.default", Ct));
        Assert.Contains("AZURE_TENANT_ID", ex.Message, StringComparison.Ordinal);
    }
}

public sealed class XmlaExecutorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class CountingTokens : ITokenProvider
    {
        public int Calls { get; private set; }

        public Task<AccessToken> GetTokenAsync(string scope, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new AccessToken("t", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private static ResolvedProfile Cloud(string? database = "Sales \"Q\"") =>
        new() { Name = "dev", Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/Example%20Sales%20Dev", Database = database, Auth = AuthMode.Default };

    [Fact]
    public async Task Rejected_queries_never_ask_for_a_token_or_connect()
    {
        var tokens = new CountingTokens();
        var executor = new XmlaDaxExecutor(Cloud(), tokens, isWindows: false);
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(new DaxQueryRequest("{ \"refresh\": {} }"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(new DaxQueryRequest("<Batch/>"), Ct));
        Assert.Equal(0, tokens.Calls);
    }

    [Fact]
    public void Connection_string_quotes_values_and_carries_the_role()
    {
        var executor = new XmlaDaxExecutor(Cloud(), new CountingTokens(), isWindows: false);
        Assert.Equal("Data Source=\"powerbi://api.powerbi.com/v1.0/myorg/Example%20Sales%20Dev\";Initial Catalog=\"Sales \"\"Q\"\"\";Roles=\"West;Region\";Application Name=TabularTools",
            executor.ConnectionString("West;Region"));
        Assert.Equal(new DaxTarget("powerbi://api.powerbi.com/v1.0/myorg/Example%20Sales%20Dev", "Sales \"Q\""), executor.Target);
    }

    [Fact]
    public void Disallowed_hosts_and_local_engines_off_windows_are_refused_up_front()
    {
        var evil = Cloud() with { Endpoint = "powerbi://api.powerbi.com.example.net/v1.0/myorg/X" };
        Assert.Throws<ArgumentException>(() => new XmlaDaxExecutor(evil, new CountingTokens(), isWindows: false));
        var local = Cloud() with { Endpoint = "localhost:1234", Auth = AuthMode.None };
        Assert.Throws<LiveException>(() => new XmlaDaxExecutor(local, null, isWindows: false));
        Assert.Throws<ArgumentException>(() => new XmlaDaxExecutor(Cloud(), null, isWindows: false)); // cloud needs tokens
    }

    [Fact]
    public void Factory_replays_recorded_profiles_without_any_live_code()
    {
        var recordings = Path.Combine(TestPaths.ExampleSalesFolder, "tests", "ExampleSales.recordings.yaml");
        var executor = new DaxExecutorFactory(_ => null).Create(new ResolvedProfile { Name = "r", RecordingsPath = recordings });
        Assert.IsType<RecordedDaxExecutor>(executor);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'5', 0, (byte)'1', 0, (byte)'2', 0, (byte)'3', 0 }, 5123)]
    [InlineData(new byte[] { (byte)'6', (byte)'0', (byte)'0', (byte)'0', (byte)'\r', (byte)'\n' }, 6000)]
    [InlineData(new byte[] { (byte)'x' }, null)]
    [InlineData(new byte[] { (byte)'9', (byte)'9', (byte)'9', (byte)'9', (byte)'9', (byte)'9' }, null)]
    public void Desktop_port_files_parse(byte[] bytes, int? port) => Assert.Equal(port, DesktopInstances.ParsePort(bytes));

    [Fact]
    public void Auth_none_is_only_for_local_engines()
    {
        var ex = Assert.Throws<ArgumentException>(() => ConnectionProfiles.Validate("p", new ProfileSettings { Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/X", Database = "X", Auth = "none" }));
        Assert.Contains("only works with local engines", ex.Message, StringComparison.Ordinal);
        ConnectionProfiles.Validate("p", new ProfileSettings { Endpoint = "desktop", Database = "X", Auth = "none" });
    }
}

/// <summary>
/// Runs against a real model only when TABULARTOOLS_LIVE_TESTS=1, with TABULARTOOLS_LIVE_CONFIG (a tabulartools.yaml)
/// and TABULARTOOLS_PROFILE set. Never runs in public CI. See scripts/verify-live.ps1 for the full manual check.
/// </summary>
public sealed class OptInLiveTests
{
    [Fact]
    public async Task Runs_a_trivial_query_against_the_configured_profile()
    {
        if (Environment.GetEnvironmentVariable("TABULARTOOLS_LIVE_TESTS") != "1")
            Assert.Skip("Live tests are opt-in: set TABULARTOOLS_LIVE_TESTS=1, TABULARTOOLS_LIVE_CONFIG and TABULARTOOLS_PROFILE.");
        var config = ConfigLoader.Load(Environment.GetEnvironmentVariable("TABULARTOOLS_LIVE_CONFIG")!);
        var profile = ConnectionProfiles.Resolve(config, null);
        var executor = new DaxExecutorFactory().Create(profile);
        var result = await executor.ExecuteAsync(new DaxQueryRequest("EVALUATE ROW(\"one\", 1)"), TestContext.Current.CancellationToken);
        Assert.Equal(1L, Assert.Single(result.Rows)[0]);
    }
}
