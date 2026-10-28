using System.Text;
using TabularTools.Core.Dax;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace TabularTools.Core.Lint;

/// <summary>
/// Rewrites a Best Practice Analyzer expression from Tabular Editor's Dynamic LINQ dialect into what
/// System.Linq.Dynamic.Core accepts:
/// <list type="bullet">
/// <item>string literals are raw in Tabular Editor (backslashes are literal, <c>""</c> is a quote), so they are re-escaped;</item>
/// <item><c>outerIt</c> → <c>parent</c>, <c>current</c> → <c>root</c>;</item>
/// <item>enum literals such as <c>DataType.Double</c> → <c>"Double"</c> (the compatibility view exposes enums as strings);</item>
/// <item>bare DAX token type names (<c>DIV</c>, <c>INTEGER_LITERAL</c>) → string literals;</item>
/// <item><c>IsNullOrWhitespace</c> → <c>IsNullOrWhiteSpace</c> (Tabular Editor resolves methods case-insensitively).</item>
/// </list>
/// </summary>
public static class DialectAdapter
{
    private static readonly Dictionary<string, HashSet<string>> EnumTypes = new(StringComparer.Ordinal)
    {
        ["DataType"] = Names<TOM.DataType>(),
        ["CrossFilteringBehavior"] = Names<TOM.CrossFilteringBehavior>(),
        ["AggregateFunction"] = Names<TOM.AggregateFunction>(),
        ["RelationshipEndCardinality"] = Names<TOM.RelationshipEndCardinality>(),
        ["SecurityFilteringBehavior"] = Names<TOM.SecurityFilteringBehavior>(),
        ["ModeType"] = Names<TOM.ModeType>(),
        ["PartitionSourceType"] = Names<TOM.PartitionSourceType>(),
        ["ColumnType"] = Names<TOM.ColumnType>(),
        ["ObjectType"] = Names<TOM.ObjectType>(),
        ["MetadataPermission"] = Names<TOM.MetadataPermission>(),
        ["ModelPermission"] = Names<TOM.ModelPermission>(),
        ["PowerBIDataSourceVersion"] = Names<TOM.PowerBIDataSourceVersion>(),
        ["DataSourceType"] = Names<TOM.DataSourceType>(),
        ["ExpressionKind"] = Names<TOM.ExpressionKind>(),
        ["DateTimeRelationshipBehavior"] = Names<TOM.DateTimeRelationshipBehavior>(),
    };

    private static readonly HashSet<string> TokenTypeNames = Names<DaxTokenType>();

    private static readonly Dictionary<string, string> MethodRenames = new(StringComparer.Ordinal)
    {
        ["IsNullOrWhitespace"] = "IsNullOrWhiteSpace",
        ["IsNullOrempty"] = "IsNullOrEmpty",
    };

    public static string Translate(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var sb = new StringBuilder(expression.Length + 16);
        var i = 0;
        while (i < expression.Length)
        {
            var c = expression[i];

            if (c == '"')
            {
                i++;
                var content = new StringBuilder();
                while (i < expression.Length)
                {
                    if (expression[i] == '"')
                    {
                        if (i + 1 < expression.Length && expression[i + 1] == '"') { content.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    content.Append(expression[i++]);
                }
                AppendString(sb, content.ToString());
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_')) i++;
                var word = expression[start..i];
                var afterDot = PreviousNonSpace(expression, start) == '.';
                var next = NextNonSpace(expression, i);

                if (MethodRenames.TryGetValue(word, out var renamed))
                {
                    sb.Append(renamed);
                    continue;
                }

                if (!afterDot)
                {
                    if (word.Equals("outerIt", StringComparison.OrdinalIgnoreCase)) { sb.Append("parent"); continue; }
                    if (word == "current") { sb.Append("root"); continue; }

                    // Enum literal: DataType.Double → "Double"
                    if (next == '.' && EnumTypes.TryGetValue(word, out var members))
                    {
                        var dot = expression.IndexOf('.', i);
                        var memberStart = dot + 1;
                        while (memberStart < expression.Length && char.IsWhiteSpace(expression[memberStart])) memberStart++;
                        var memberEnd = memberStart;
                        while (memberEnd < expression.Length && (char.IsLetterOrDigit(expression[memberEnd]) || expression[memberEnd] == '_')) memberEnd++;
                        var member = expression[memberStart..memberEnd];
                        if (members.Contains(member))
                        {
                            AppendString(sb, member);
                            i = memberEnd;
                            continue;
                        }
                    }

                    // Bare DAX token type: Type = DIV → Type = "DIV"
                    if (TokenTypeNames.Contains(word) && next is not '(' and not '.')
                    {
                        AppendString(sb, word);
                        continue;
                    }
                }

                sb.Append(word);
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static void AppendString(StringBuilder sb, string content)
    {
        sb.Append('"');
        foreach (var ch in content)
        {
            if (ch is '\\' or '"') sb.Append('\\');
            sb.Append(ch);
        }
        sb.Append('"');
    }

    private static char PreviousNonSpace(string s, int i)
    {
        for (var k = i - 1; k >= 0; k--)
            if (!char.IsWhiteSpace(s[k])) return s[k];
        return '\0';
    }

    private static char NextNonSpace(string s, int i)
    {
        for (var k = i; k < s.Length; k++)
            if (!char.IsWhiteSpace(s[k])) return s[k];
        return '\0';
    }

    private static HashSet<string> Names<T>() where T : struct, Enum => new(Enum.GetNames<T>(), StringComparer.Ordinal);
}
