using TabularTools.Core.Writes;

namespace TabularTools.Core.Tests;

public sealed class FileWriterTests
{
    [Fact]
    public void Dry_run_reports_the_change_without_writing()
    {
        using var temp = new TempDir();
        var result = new FileWriter([temp.Path]).Write(temp.Combine("a", "t.measuretests.yaml"), "x: 1\n", dryRun: true);
        Assert.True(result.DryRun);
        var change = Assert.Single(result.Files);
        Assert.Equal(WriteAction.Created, change.Action);
        Assert.Contains("+x: 1", change.UnifiedDiff, StringComparison.Ordinal);
        Assert.False(File.Exists(temp.Combine("a", "t.measuretests.yaml")));
    }

    [Fact]
    public void Writes_atomically_and_reports_created_modified_and_unchanged()
    {
        using var temp = new TempDir();
        var writer = new FileWriter([temp.Path]);
        var path = temp.Combine("f.yaml");
        Assert.Equal(WriteAction.Created, writer.Write(path, "a\n", false).Files[0].Action);
        Assert.Equal(WriteAction.Unchanged, writer.Write(path, "a\n", false).Files[0].Action);
        var modified = writer.Write(path, "b\n", false).Files[0];
        Assert.Equal(WriteAction.Modified, modified.Action);
        Assert.Contains("-a", modified.UnifiedDiff, StringComparison.Ordinal);
        Assert.Equal("b\n", File.ReadAllText(path));
        Assert.Equal(["f.yaml"], Directory.GetFiles(temp.Path).Select(Path.GetFileName)); // no temp files left
    }

    [Fact]
    public void Keeps_crlf_line_endings_and_a_byte_order_mark()
    {
        using var temp = new TempDir();
        var path = temp.Combine("f.yaml");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n']);
        var change = new FileWriter([temp.Path]).Write(path, "a\nb\n", false).Files[0];
        Assert.Equal(WriteAction.Modified, change.Action);
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n', (byte)'b', (byte)'\r', (byte)'\n'], File.ReadAllBytes(path));
    }

    [Fact]
    public void Refuses_paths_outside_the_roots()
    {
        using var root = new TempDir();
        using var other = new TempDir();
        var writer = new FileWriter([root.Path]);
        Assert.Contains("outside the allowed folders", Assert.Throws<ArgumentException>(() => writer.Write(other.Combine("x.yaml"), "x", false)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => writer.Write(root.Combine("..", Path.GetFileName(other.Path), "x.yaml"), "x", false));
        Assert.Throws<ArgumentException>(() => writer.Write(root.Path, "x", false));
    }

    [Fact]
    public void Refuses_to_follow_a_symlink_out_of_the_root()
    {
        using var root = new TempDir();
        using var outside = new TempDir();
        var link = root.Combine("escape");
        try { Directory.CreateSymbolicLink(link, outside.Path); }
        catch (IOException) { return; } // symlinks need extra rights on some Windows machines
        catch (UnauthorizedAccessException) { return; }
        var ex = Assert.Throws<ArgumentException>(() => new FileWriter([root.Path]).Write(Path.Combine(link, "x.yaml"), "x", false));
        Assert.Contains("outside", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(outside.Path, "x.yaml")));
    }
}
