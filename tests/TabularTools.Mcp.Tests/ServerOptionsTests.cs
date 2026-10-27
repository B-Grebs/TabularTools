namespace TabularTools.Mcp.Tests;

public class ServerOptionsTests
{
    [Fact]
    public void Defaults_are_read_only_with_the_current_folder_as_root()
    {
        var options = ServerOptions.Parse([]);

        Assert.False(options.AllowWrites);
        Assert.False(options.DisableLive);
        Assert.Equal([Directory.GetCurrentDirectory()], options.Roots);
    }

    [Fact]
    public void Parses_roots_and_flags()
    {
        var options = ServerOptions.Parse(["--root", TestPaths.Fixtures, "--root", TestPaths.ExampleSalesFolder, "--allow-writes", "--disable-live"]);

        Assert.Equal([TestPaths.Fixtures, TestPaths.ExampleSalesFolder], options.Roots);
        Assert.True(options.AllowWrites);
        Assert.True(options.DisableLive);
    }

    [Theory]
    [InlineData("--root")]
    [InlineData("--nope")]
    public void Rejects_bad_arguments(string arg)
    {
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse([arg]));
    }

    [Fact]
    public void Rejects_a_missing_root_folder()
    {
        var ex = Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--root", Path.Combine(TestPaths.Fixtures, "missing")]));
        Assert.Contains("not found", ex.Message);
    }
}
