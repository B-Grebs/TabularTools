using ModelContextProtocol.Client;

namespace TabularTools.Mcp.Tests;

/// <summary>Starts the real MCP server as a child process over stdio, rooted at the ExampleSales fixture.</summary>
public class McpServerFixture : IAsyncLifetime
{
    /// <summary>Server arguments after the dll path.</summary>
    protected virtual IReadOnlyList<string> Arguments => ["--root", TestPaths.ExampleSalesFolder];

    public McpClient Client { get; private set; } = null!;

    public static string ServerDll
    {
        get
        {
            // The test project references the server project, so its build output sits next to the tests.
            var local = Path.Combine(AppContext.BaseDirectory, "TabularTools.Mcp.dll");
            if (File.Exists(local) && File.Exists(Path.ChangeExtension(local, ".runtimeconfig.json"))) return local;

            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name; // bin/<Configuration>/net10.0
            return Path.Combine(TestPaths.RepoRoot, "src", "TabularTools.Mcp", "bin", configuration, "net10.0", "TabularTools.Mcp.dll");
        }
    }

    public async ValueTask InitializeAsync()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "tabulartools",
            Command = "dotnet",
            Arguments = [ServerDll, .. Arguments],
        });
        Client = await McpClient.CreateAsync(transport);
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (Client is not null) await Client.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

/// <summary>A server with --allow-writes, rooted at a temporary copy of the ExampleSales fixture.</summary>
public sealed class WritableServerFixture : McpServerFixture
{
    private readonly TempDir _root = new(TestPaths.ExampleSalesFolder);

    public string Root => _root.Path;

    protected override IReadOnlyList<string> Arguments => ["--root", Root, "--allow-writes"];

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _root.Dispose();
    }
}

/// <summary>A server with --disable-live.</summary>
public sealed class NoLiveServerFixture : McpServerFixture
{
    protected override IReadOnlyList<string> Arguments => ["--root", TestPaths.ExampleSalesFolder, "--disable-live"];
}
