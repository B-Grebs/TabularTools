using TabularTools.Core.Config;
using TabularTools.Core.Diff;

namespace TabularTools.Core.Tests;

public class DiffSupportTests
{
    [Fact]
    public void Unified_diff_shows_changed_lines_with_context()
    {
        Assert.Equal("", TextDiff.Unified("a\nb", "a\nb"));
        Assert.Equal("@@ -1,3 +1,3 @@\n a\n-b\n+B\n c", TextDiff.Unified("a\nb\nc", "a\nB\nc"));
        Assert.Equal("@@ -1,2 +1,3 @@\n a\n+x\n b", TextDiff.Unified("a\nb", "a\nx\nb"));
        Assert.Equal("@@ -0,0 +1 @@\n+new", TextDiff.Unified(null, "new"));

        var before = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}"));
        var after = before.Replace("line 2\n", "line two\n", StringComparison.Ordinal).Replace("line 18\n", "line eighteen\n", StringComparison.Ordinal);
        var diff = TextDiff.Unified(before, after);
        Assert.Equal(2, diff.Split('\n').Count(l => l.StartsWith("@@", StringComparison.Ordinal))); // far apart: two hunks
    }

    [Fact]
    public void Token_similarity_is_one_for_equal_sequences_and_drops_with_edits()
    {
        Assert.Equal(1, TextDiff.TokenSimilarity(["a", "b"], ["a", "b"]));
        Assert.Equal(0.5, TextDiff.TokenSimilarity(["a", "b"], ["a", "c"]));
        Assert.Equal(0, TextDiff.TokenSimilarity([], ["a"]));
    }

    [Theory]
    [InlineData("Sales", "Sales")]
    [InlineData("'Total Sales' = SUM(x)", "Total Sales")]
    [InlineData("'It''s' = 1", "It's")]
    [InlineData("Margin = [a] / [b]", "Margin")]
    [InlineData("00000000-0000-4000-8000-000000000201", "00000000-0000-4000-8000-000000000201")]
    public void Tmdl_names_are_read_from_declarations(string text, string expected) =>
        Assert.Equal(expected, SourceLocationIndex.ReadName(text));

    [Fact]
    public void Every_change_kind_is_unique_and_documented()
    {
        Assert.Equal(SeverityPolicy.Kinds.Count, SeverityPolicy.Kinds.Select(k => k.Kind).Distinct().Count());
        var doc = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "docs", "diff-severities.md"));
        Assert.All(SeverityPolicy.Kinds, k => Assert.Contains($"| `{k.Kind}` | {k.Severity.ToString().ToLowerInvariant()} |", doc));
    }

    [Fact]
    public void Diff_config_is_parsed_merged_and_validated()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("tabulartools.yaml"), """
            diff:
              failOn: breaking
              renameThreshold: 0.85
              severity:
                column.summarizeByChanged: low
            """);
        File.WriteAllText(temp.Combine("tabulartools.local.yaml"), """
            diff:
              includeFormattingOnly: true
              severity:
                measure.moved: high
            """);
        var config = ConfigLoader.Load(temp.Combine("tabulartools.yaml"));
        Assert.Equal("breaking", config.Diff.FailOn);
        Assert.Equal(0.85, config.Diff.RenameThreshold);
        Assert.True(config.Diff.IncludeFormattingOnly);
        var overrides = ConfigLoader.DiffSeverityOverrides(config)!;
        Assert.Equal(ChangeSeverity.Low, overrides["column.summarizeByChanged"]);
        Assert.Equal(ChangeSeverity.High, overrides["measure.moved"]);
    }

    [Theory]
    [InlineData("diff:\n  failOn: sometimes", "diff.failOn")]
    [InlineData("diff:\n  renameThreshold: 1.5", "diff.renameThreshold")]
    [InlineData("diff:\n  severity:\n    measure.exploded: high", "unknown change kind")]
    [InlineData("diff:\n  severity:\n    measure.moved: critical", "isn't breaking")]
    public void Invalid_diff_config_is_rejected(string yaml, string message)
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("tabulartools.yaml"), yaml);
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Load(temp.Combine("tabulartools.yaml")));
        Assert.Contains(message, ex.Message);
    }
}
