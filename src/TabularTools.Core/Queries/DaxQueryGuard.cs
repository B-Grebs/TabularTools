using TabularTools.Core.Dax;

namespace TabularTools.Core.Queries;

/// <summary>
/// The read-only gate in front of every live query (ARCHITECTURE §14.2). The XMLA endpoint also accepts TMSL and XMLA
/// commands, and callers often have write rights, so only two shapes get through: DAX queries (first keyword DEFINE or
/// EVALUATE) and <c>SELECT … FROM $SYSTEM.*</c> DMV selects. Everything else is rejected before a connection opens.
/// </summary>
public static class DaxQueryGuard
{
    /// <summary>Throws <see cref="ArgumentException"/> with the reason when <paramref name="query"/> isn't allowed.</summary>
    public static void Check(string? query)
    {
        if (Reject(query) is { } reason) throw new ArgumentException(reason);
    }

    /// <summary>Null when the query is allowed, otherwise why it isn't.</summary>
    public static string? Reject(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "The query is empty.";
        var trimmed = query.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith('<') || trimmed.StartsWith('{'))
            return "XMLA and TMSL commands aren't allowed. Only DAX queries (DEFINE/EVALUATE) and $SYSTEM DMV selects can run.";

        var tokens = DaxLexer.Significant(query.TrimStart('\uFEFF'));
        if (tokens.Count == 0) return "The query has no statements, only comments.";
        if (tokens.Any(t => t.Type == DaxTokenType.UNKNOWN && t.Text == ";"))
            return "Only one statement can run; remove the ';'.";

        var first = tokens[0];
        if (first.Type == DaxTokenType.KEYWORD && (Is(first, "DEFINE") || Is(first, "EVALUATE")))
        {
            if (!tokens.Any(t => t.Type == DaxTokenType.KEYWORD && Is(t, "EVALUATE")))
                return "A DAX query needs at least one EVALUATE.";
            return null;
        }

        if (first.Type == DaxTokenType.TABLE_OR_VARIABLE && Is(first, "SELECT")) return CheckDmv(tokens);

        return $"Only DAX queries (starting with DEFINE or EVALUATE) and SELECT … FROM $SYSTEM.<view> DMV queries can run; this starts with '{first.Text}'.";
    }

    private static string? CheckDmv(IReadOnlyList<DaxToken> tokens)
    {
        // SELECT <columns> FROM $SYSTEM.<VIEW> [WHERE …] [ORDER BY …]: exactly one FROM, naming a $SYSTEM rowset.
        var froms = Enumerable.Range(0, tokens.Count).Where(i => tokens[i].Type == DaxTokenType.TABLE_OR_VARIABLE && Is(tokens[i], "FROM")).ToList();
        if (froms.Count != 1) return "A DMV query needs exactly one FROM $SYSTEM.<view>.";
        var i = froms[0];
        var ok = i + 2 < tokens.Count
                 && tokens[i + 1].Text == "$"
                 && tokens[i + 1].Start + 1 == tokens[i + 2].Start
                 && tokens[i + 2].Text.StartsWith("SYSTEM.", StringComparison.OrdinalIgnoreCase)
                 && tokens[i + 2].Text.Length > "SYSTEM.".Length;
        if (!ok) return "DMV queries can only read $SYSTEM views, e.g. SELECT * FROM $SYSTEM.TMSCHEMA_TABLES.";
        foreach (var word in new[] { "INTO", "DROP", "CREATE", "ALTER", "INSERT", "DELETE", "UPDATE", "CALL", "OPENQUERY", "OPENROWSET" })
            if (tokens.Any(t => t.Type is DaxTokenType.TABLE_OR_VARIABLE or DaxTokenType.FUNCTION && Is(t, word)))
                return $"'{word}' isn't allowed in a DMV query.";
        return null;
    }

    private static bool Is(DaxToken token, string word) => token.Text.Equals(word, StringComparison.OrdinalIgnoreCase);
}
