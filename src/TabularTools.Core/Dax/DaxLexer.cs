using System.Text;

namespace TabularTools.Core.Dax;

/// <summary>A small, forgiving DAX lexer. Never throws: unterminated constructs run to the end of the text.</summary>
public static class DaxLexer
{
    private static readonly Dictionary<string, DaxTokenType> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VAR"] = DaxTokenType.VAR,
        ["RETURN"] = DaxTokenType.RETURN,
        ["IN"] = DaxTokenType.IN,
        ["NOT"] = DaxTokenType.NOT,
        ["TRUE"] = DaxTokenType.TRUE,
        ["FALSE"] = DaxTokenType.FALSE,
        ["DEFINE"] = DaxTokenType.KEYWORD,
        ["EVALUATE"] = DaxTokenType.KEYWORD,
        ["MEASURE"] = DaxTokenType.KEYWORD,
        ["ORDER"] = DaxTokenType.KEYWORD,
        ["BY"] = DaxTokenType.KEYWORD,
        ["ASC"] = DaxTokenType.KEYWORD,
        ["DESC"] = DaxTokenType.KEYWORD,
        ["START"] = DaxTokenType.KEYWORD,
        ["AT"] = DaxTokenType.KEYWORD,
        ["COLUMN"] = DaxTokenType.KEYWORD,
        ["TABLE"] = DaxTokenType.KEYWORD,
        ["FUNCTION"] = DaxTokenType.KEYWORD,
    };

    public static IReadOnlyList<DaxToken> Tokenize(string? text)
    {
        var tokens = new List<DaxToken>();
        if (string.IsNullOrEmpty(text)) return tokens;

        int i = 0, line = 1, lineStart = 0;
        while (i < text.Length)
        {
            var start = i;
            var startLine = line;
            var column = i - lineStart + 1;
            var c = text[i];
            DaxTokenType type;
            string? value = null;

            if (char.IsWhiteSpace(c))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                type = DaxTokenType.WHITESPACES;
            }
            else if (c == '/' && Peek(text, i + 1) == '/' || c == '-' && Peek(text, i + 1) == '-')
            {
                while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
                type = DaxTokenType.SINGLE_LINE_COMMENT;
            }
            else if (c == '/' && Peek(text, i + 1) == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                type = DaxTokenType.DELIMITED_COMMENT;
            }
            else if (c == '"')
            {
                value = ReadQuoted(text, ref i, '"', '"');
                type = DaxTokenType.STRING_LITERAL;
            }
            else if (c == '\'')
            {
                value = ReadQuoted(text, ref i, '\'', '\'');
                type = DaxTokenType.TABLE;
            }
            else if (c == '[')
            {
                value = ReadQuoted(text, ref i, '[', ']');
                type = DaxTokenType.COLUMN_OR_MEASURE;
            }
            else if (char.IsDigit(c) || c == '.' && char.IsDigit(Peek(text, i + 1)))
            {
                type = ReadNumber(text, ref i);
            }
            else if (char.IsLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.')) i++;
                var word = text[start..i];
                type = Keywords.TryGetValue(word, out var keyword) ? keyword : DaxTokenType.TABLE_OR_VARIABLE;
                if (type is DaxTokenType.TABLE_OR_VARIABLE or DaxTokenType.TRUE or DaxTokenType.FALSE or DaxTokenType.NOT &&
                    NextNonWhitespace(text, i) == '(')
                    type = DaxTokenType.FUNCTION;
            }
            else
            {
                (type, var length) = Operator(text, i);
                i += length;
            }

            var tokenText = text[start..i];
            tokens.Add(new DaxToken(type, tokenText, value ?? tokenText, start, startLine, column));

            // Track line numbers across the token's text.
            for (var k = start; k < i; k++)
            {
                if (text[k] == '\n' || (text[k] == '\r' && Peek(text, k + 1) != '\n'))
                {
                    line++;
                    lineStart = k + 1;
                }
            }
        }
        return tokens;
    }

    /// <summary>Tokens without whitespace and comments.</summary>
    public static IReadOnlyList<DaxToken> Significant(string? text) => Tokenize(text).Where(t => !t.IsTrivia).ToList();

    /// <summary>
    /// The expression reduced to its significant tokens joined by single spaces. Two expressions that differ only in
    /// whitespace or comments normalize to the same string. Keywords and function names compare case-insensitively.
    /// </summary>
    public static string Normalize(string? text) =>
        string.Join(' ', Significant(text).Select(t => t.Type is DaxTokenType.FUNCTION or DaxTokenType.KEYWORD or DaxTokenType.VAR or DaxTokenType.RETURN
            or DaxTokenType.IN or DaxTokenType.NOT or DaxTokenType.TRUE or DaxTokenType.FALSE ? t.Text.ToUpperInvariant() : t.Text));

    private static string ReadQuoted(string text, ref int i, char open, char close)
    {
        var sb = new StringBuilder();
        i++; // opening
        while (i < text.Length)
        {
            if (text[i] == close)
            {
                if (Peek(text, i + 1) == close) { sb.Append(close); i += 2; continue; }
                i++;
                return sb.ToString();
            }
            sb.Append(text[i++]);
        }
        return sb.ToString();
    }

    private static DaxTokenType ReadNumber(string text, ref int i)
    {
        var real = false;
        while (i < text.Length && char.IsDigit(text[i])) i++;
        if (i < text.Length && text[i] == '.' && (i + 1 >= text.Length || char.IsDigit(text[i + 1]) || !char.IsLetter(text[i + 1])))
        {
            real = true;
            i++;
            while (i < text.Length && char.IsDigit(text[i])) i++;
        }
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E') &&
            (char.IsDigit(Peek(text, i + 1)) || (Peek(text, i + 1) is '+' or '-' && char.IsDigit(Peek(text, i + 2)))))
        {
            real = true;
            i += 2;
            while (i < text.Length && char.IsDigit(text[i])) i++;
        }
        return real ? DaxTokenType.REAL_LITERAL : DaxTokenType.INTEGER_LITERAL;
    }

    private static (DaxTokenType Type, int Length) Operator(string text, int i)
    {
        var two = i + 1 < text.Length ? text.Substring(i, 2) : "";
        return two switch
        {
            "==" => (DaxTokenType.STRICT_EQUALS, 2),
            "<>" => (DaxTokenType.NOT_EQUALS, 2),
            "<=" => (DaxTokenType.LESS_THAN_OR_EQUAL, 2),
            ">=" => (DaxTokenType.GREATER_THAN_OR_EQUAL, 2),
            "&&" => (DaxTokenType.AND, 2),
            "||" => (DaxTokenType.OR, 2),
            "=>" => (DaxTokenType.ARROW, 2),
            _ => text[i] switch
            {
                '+' => (DaxTokenType.PLUS, 1),
                '-' => (DaxTokenType.MINUS, 1),
                '*' => (DaxTokenType.MULT, 1),
                '/' => (DaxTokenType.DIV, 1),
                '^' => (DaxTokenType.POW, 1),
                '&' => (DaxTokenType.CONCAT, 1),
                '=' => (DaxTokenType.EQUALS, 1),
                '<' => (DaxTokenType.LESS_THAN, 1),
                '>' => (DaxTokenType.GREATER_THAN, 1),
                ',' => (DaxTokenType.COMMA, 1),
                ':' => (DaxTokenType.COLON, 1),
                '.' => (DaxTokenType.DOT, 1),
                '(' => (DaxTokenType.OPEN_PARENS, 1),
                ')' => (DaxTokenType.CLOSE_PARENS, 1),
                '{' => (DaxTokenType.OPEN_CURLY, 1),
                '}' => (DaxTokenType.CLOSE_CURLY, 1),
                _ => (DaxTokenType.UNKNOWN, 1),
            },
        };
    }

    private static char Peek(string text, int i) => i < text.Length ? text[i] : '\0';

    private static char NextNonWhitespace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return Peek(text, i);
    }
}
