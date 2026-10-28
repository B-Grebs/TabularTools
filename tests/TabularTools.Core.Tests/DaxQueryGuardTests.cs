using TabularTools.Core.Queries;

namespace TabularTools.Core.Tests;

public sealed class DaxQueryGuardTests
{
    [Theory]
    [InlineData("EVALUATE ROW(\"x\", 1)")]
    [InlineData("  -- comment first\n/* block */\n evaluate 'Sales'")]
    [InlineData("DEFINE MEASURE Sales[X] = 1 EVALUATE ROW(\"x\", [X])")]
    [InlineData("﻿EVALUATE {1}")]
    [InlineData("EVALUATE ROW(\"x\", \"a;b\")")] // ';' inside a string is fine
    [InlineData("SELECT * FROM $SYSTEM.TMSCHEMA_TABLES")]
    [InlineData("select [Name], [ID] from $system.TMSCHEMA_MEASURES where [TableID] = 5")]
    [InlineData("SELECT * FROM $SYSTEM.DISCOVER_SESSIONS ORDER BY SESSION_ID")]
    public void Allows_dax_queries_and_system_dmvs(string query) => Assert.Null(DaxQueryGuard.Reject(query));

    [Theory]
    [InlineData("", "empty")]
    [InlineData("-- only a comment", "only comments")]
    [InlineData("{ \"refresh\": { \"type\": \"full\" } }", "XMLA and TMSL")]
    [InlineData("<Batch xmlns=\"http://schemas.microsoft.com/analysisservices/2003/engine\"/>", "XMLA and TMSL")]
    [InlineData("EVALUATE ROW(\"x\", 1); { \"delete\": {} }", "one statement")]
    [InlineData("DEFINE MEASURE Sales[X] = 1", "at least one EVALUATE")]
    [InlineData("CREATE MEMBER CURRENTCUBE.[Measures].[X] AS 1", "starts with 'CREATE'")]
    [InlineData("SELECT * FROM Sales", "$SYSTEM")]
    [InlineData("SELECT * FROM $SYSTEM.TMSCHEMA_TABLES, $SYSTEM.TMSCHEMA_COLUMNS FROM x", "exactly one FROM")]
    [InlineData("SELECT * INTO x FROM $SYSTEM.TMSCHEMA_TABLES", "'INTO'")]
    [InlineData("SELECT * FROM $ SYSTEM.TMSCHEMA_TABLES", "$SYSTEM")]
    [InlineData("SELECT * FROM $SYSTEM.", "$SYSTEM")]
    [InlineData("CALL SystemRestart()", "starts with 'CALL'")]
    public void Rejects_everything_else_with_a_reason(string query, string reason)
    {
        var rejection = DaxQueryGuard.Reject(query);
        Assert.NotNull(rejection);
        Assert.Contains(reason, rejection, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => DaxQueryGuard.Check(query));
    }
}
