using TabularTools.Core.Dax;

namespace TabularTools.Core.Tests;

public class DaxLexerTests
{
    private static List<(DaxTokenType, string)> Sig(string dax) => DaxLexer.Significant(dax).Select(t => (t.Type, t.Value)).ToList();

    [Fact]
    public void Tokenizes_references_strings_numbers_and_operators()
    {
        var tokens = Sig("SUMX('Sales Order', 'Sales Order'[Qty] * [Price]) / 2.5 & \"x\"\"y\"");

        Assert.Equal(
        [
            (DaxTokenType.FUNCTION, "SUMX"), (DaxTokenType.OPEN_PARENS, "("), (DaxTokenType.TABLE, "Sales Order"), (DaxTokenType.COMMA, ","),
            (DaxTokenType.TABLE, "Sales Order"), (DaxTokenType.COLUMN_OR_MEASURE, "Qty"), (DaxTokenType.MULT, "*"),
            (DaxTokenType.COLUMN_OR_MEASURE, "Price"), (DaxTokenType.CLOSE_PARENS, ")"), (DaxTokenType.DIV, "/"),
            (DaxTokenType.REAL_LITERAL, "2.5"), (DaxTokenType.CONCAT, "&"), (DaxTokenType.STRING_LITERAL, "x\"y"),
        ], tokens);
    }

    [Fact]
    public void Unescapes_doubled_quotes_and_brackets()
    {
        var tokens = Sig("'O''Brien'[Col]]x]");

        Assert.Equal([(DaxTokenType.TABLE, "O'Brien"), (DaxTokenType.COLUMN_OR_MEASURE, "Col]x")], tokens);
    }

    [Fact]
    public void Recognises_comments_keywords_and_multi_character_operators()
    {
        var tokens = DaxLexer.Tokenize("VAR x = 1 -- note\n/* block */ RETURN IF(x <> 2 && NOT(x >= 3), TRUE(), FALSE)");

        Assert.Contains(tokens, t => t.Type == DaxTokenType.SINGLE_LINE_COMMENT && t.Text == "-- note");
        Assert.Contains(tokens, t => t.Type == DaxTokenType.DELIMITED_COMMENT && t.Line == 2);
        var types = tokens.Where(t => !t.IsTrivia).Select(t => t.Type).ToList();
        Assert.Contains(DaxTokenType.VAR, types);
        Assert.Contains(DaxTokenType.RETURN, types);
        Assert.Contains(DaxTokenType.NOT_EQUALS, types);
        Assert.Contains(DaxTokenType.AND, types);
        Assert.Contains(DaxTokenType.GREATER_THAN_OR_EQUAL, types);
        Assert.Equal(DaxTokenType.FUNCTION, tokens.Single(t => t.Text == "NOT").Type);
        Assert.Equal(DaxTokenType.FUNCTION, tokens.Single(t => t.Text == "TRUE").Type);
        Assert.Equal(DaxTokenType.FALSE, tokens.Single(t => t.Text == "FALSE").Type);
    }

    [Fact]
    public void Integer_and_real_literals_are_distinguished()
    {
        Assert.Equal(DaxTokenType.INTEGER_LITERAL, Sig("42")[0].Item1);
        Assert.Equal(DaxTokenType.REAL_LITERAL, Sig("0.5")[0].Item1);
        Assert.Equal(DaxTokenType.REAL_LITERAL, Sig(".5")[0].Item1);
        Assert.Equal(DaxTokenType.REAL_LITERAL, Sig("1e-3")[0].Item1);
    }

    [Fact]
    public void Tracks_lines_and_columns()
    {
        var tokens = DaxLexer.Significant("SUM(\r\n  Sales[Amount]\n)");

        var table = tokens.Single(t => t.Value == "Sales");
        Assert.Equal((2, 3), (table.Line, table.Column));
        Assert.Equal(3, tokens[^1].Line);
    }

    [Fact]
    public void Unterminated_constructs_do_not_throw()
    {
        Assert.Equal(DaxTokenType.STRING_LITERAL, Sig("\"open")[0].Item1);
        Assert.Equal(DaxTokenType.COLUMN_OR_MEASURE, Sig("[open")[0].Item1);
        Assert.Single(DaxLexer.Tokenize("/* open"));
    }

    [Fact]
    public void Normalize_ignores_whitespace_comments_and_function_case()
    {
        Assert.Equal(DaxLexer.Normalize("sum ( Sales[Amount] ) // total"), DaxLexer.Normalize("SUM(Sales[Amount])"));
        Assert.NotEqual(DaxLexer.Normalize("SUM(Sales[Amount])"), DaxLexer.Normalize("SUM(Sales[Qty])"));
    }
}
