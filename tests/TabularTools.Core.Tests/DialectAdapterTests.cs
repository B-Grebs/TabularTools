using TabularTools.Core.Lint;

namespace TabularTools.Core.Tests;

public class DialectAdapterTests
{
    [Theory]
    [InlineData("DataType = DataType.Double", "DataType = \"Double\"")]
    [InlineData("CrossFilteringBehavior == CrossFilteringBehavior.BothDirections", "CrossFilteringBehavior == \"BothDirections\"")]
    [InlineData("FromColumn.DataType != ToColumn.DataType", "FromColumn.DataType != ToColumn.DataType")]
    [InlineData("DataType.ToString() == \"Int64\"", "DataType.ToString() == \"Int64\"")]
    [InlineData("Model.AllMeasures.Any(Expression = outerIt.Expression and it <> OuterIt)", "Model.AllMeasures.Any(Expression = parent.Expression and it <> parent)")]
    [InlineData("UsedInRelationships.Any(current.Name == FromTable.Name)", "UsedInRelationships.Any(root.Name == FromTable.Name)")]
    [InlineData("string.IsNullOrWhitespace(Description)", "string.IsNullOrWhiteSpace(Description)")]
    [InlineData("Tokenize().Any(Type = DIV and Next.Type <> INTEGER_LITERAL)", "Tokenize().Any(Type = \"DIV\" and Next.Type <> \"INTEGER_LITERAL\")")]
    public void Rewrites_tabular_editor_dialect(string input, string expected)
    {
        Assert.Equal(expected, DialectAdapter.Translate(input));
    }

    [Fact]
    public void String_literals_are_raw_in_tabular_editor_and_re_escaped()
    {
        Assert.Equal("RegEx.IsMatch(Expression,\"\\\\]\\\\s*\\\\/\")", DialectAdapter.Translate("RegEx.IsMatch(Expression,\"\\]\\s*\\/\")"));
        Assert.Equal("Query.Contains(\"[Query=\\\"SELECT\")", DialectAdapter.Translate("Query.Contains(\"[Query=\"\"SELECT\")"));
    }

    [Fact]
    public void Identifiers_inside_strings_are_left_alone()
    {
        Assert.Equal("Name = \"current DataType.Double outerIt\"", DialectAdapter.Translate("Name = \"current DataType.Double outerIt\""));
    }

    [Fact]
    public void Unknown_enum_members_are_not_rewritten()
    {
        Assert.Equal("DataType.Nonsense", DialectAdapter.Translate("DataType.Nonsense"));
    }
}
