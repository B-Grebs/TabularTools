namespace TabularTools.Core.Snapshots;

/// <summary>
/// Stable object keys shared by every engine, e.g. <c>table:Sales/measure:Total Sales</c>.
/// Relationships are keyed by their endpoints, not their (often GUID) names.
/// </summary>
public static class ObjectKeys
{
    public static string Table(string table) => $"table:{table}";
    public static string Column(string table, string column) => $"table:{table}/column:{column}";
    public static string Measure(string table, string measure) => $"table:{table}/measure:{measure}";
    public static string Partition(string table, string partition) => $"table:{table}/partition:{partition}";
    public static string Hierarchy(string table, string hierarchy) => $"table:{table}/hierarchy:{hierarchy}";
    public static string CalculationItem(string table, string item) => $"table:{table}/calculationItem:{item}";
    public static string Relationship(string fromTable, string fromColumn, string toTable, string toColumn) =>
        $"relationship:{fromTable}[{fromColumn}]->{toTable}[{toColumn}]";
    public static string Role(string role) => $"role:{role}";
    public static string Expression(string name) => $"expression:{name}";
    public static string Function(string name) => $"function:{name}";
}
