namespace TabularTools.Core.Testing;

/// <summary>JSON Schemas shipped with the tool (copies of the files in schemas/).</summary>
public static class Schemas
{
    public static string MeasureTests { get; } = Read("TabularTools.Schemas.measure-tests.json");

    private static string Read(string name)
    {
        using var stream = typeof(Schemas).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing embedded schema {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
