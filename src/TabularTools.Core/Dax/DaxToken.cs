namespace TabularTools.Core.Dax;

/// <summary>
/// DAX token types. Names use Tabular Editor's spelling (e.g. <c>DIV</c>, <c>INTEGER_LITERAL</c>) so that
/// Best Practice Analyzer rules using <c>Tokenize()</c> work unchanged.
/// </summary>
#pragma warning disable CA1707 // Identifiers should not contain underscores: names match Tabular Editor's token types.
public enum DaxTokenType
{
    WHITESPACES,
    SINGLE_LINE_COMMENT,
    DELIMITED_COMMENT,
    STRING_LITERAL,
    INTEGER_LITERAL,
    REAL_LITERAL,
    /// <summary>Quoted table name: <c>'Sales Order'</c>.</summary>
    TABLE,
    /// <summary>Unquoted identifier: a table, a variable, or a keyword-like name.</summary>
    TABLE_OR_VARIABLE,
    /// <summary>Bracketed name: <c>[Amount]</c>.</summary>
    COLUMN_OR_MEASURE,
    /// <summary>Identifier immediately followed by an opening parenthesis.</summary>
    FUNCTION,
    VAR,
    RETURN,
    IN,
    NOT,
    TRUE,
    FALSE,
    /// <summary>Query keywords: DEFINE, EVALUATE, MEASURE, ORDER, BY, ASC, DESC, START, AT, COLUMN, TABLE, FUNCTION.</summary>
    KEYWORD,
    PLUS,
    MINUS,
    MULT,
    DIV,
    POW,
    CONCAT,
    EQUALS,
    STRICT_EQUALS,
    NOT_EQUALS,
    LESS_THAN,
    LESS_THAN_OR_EQUAL,
    GREATER_THAN,
    GREATER_THAN_OR_EQUAL,
    AND,
    OR,
    COMMA,
    COLON,
    DOT,
    ARROW,
    OPEN_PARENS,
    CLOSE_PARENS,
    OPEN_CURLY,
    CLOSE_CURLY,
    UNKNOWN,
}
#pragma warning restore CA1707

/// <summary>One DAX token.</summary>
/// <param name="Type">Token type.</param>
/// <param name="Text">Exact source text.</param>
/// <param name="Value">Unescaped content for names and strings (<c>'A''s'</c> → <c>A's</c>); otherwise the text.</param>
/// <param name="Start">0-based offset in the expression.</param>
/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column.</param>
public sealed record DaxToken(DaxTokenType Type, string Text, string Value, int Start, int Line, int Column)
{
    public bool IsTrivia => Type is DaxTokenType.WHITESPACES or DaxTokenType.SINGLE_LINE_COMMENT or DaxTokenType.DELIMITED_COMMENT;
}
