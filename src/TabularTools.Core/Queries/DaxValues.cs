using System.Globalization;

namespace TabularTools.Core.Queries;

/// <summary>Normalizes engine values to the small set results use, and compares them.</summary>
public static class DaxValues
{
    /// <summary>Converts a raw value (from ADOMD or a file) to null, bool, long, decimal, double or string.</summary>
    public static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        bool or long or decimal or string => value,
        int i => (long)i,
        short s => (long)s,
        byte b => (long)b,
        sbyte sb => (long)sb,
        uint ui => (long)ui,
        ushort us => (long)us,
        ulong ul => ul <= long.MaxValue ? (long)ul : (double)ul,
        float f => Double(f),
        double d => Double(d),
        DateTime dt => dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        Guid g => g.ToString(),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static object Double(double d) =>
        double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "Infinity" : double.IsNegativeInfinity(d) ? "-Infinity" : d;

    /// <summary>Type name for a .NET column type.</summary>
    public static string TypeName(Type? type) => Type.GetTypeCode(type) switch
    {
        TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or TypeCode.Byte or TypeCode.SByte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64 => "int64",
        TypeCode.Double or TypeCode.Single => "double",
        TypeCode.Decimal => "decimal",
        TypeCode.Boolean => "boolean",
        TypeCode.DateTime => "dateTime",
        TypeCode.String => "string",
        _ => type?.Name ?? "other",
    };

    public static bool TryNumber(object? value, out double number)
    {
        switch (value)
        {
            case long l: number = l; return true;
            case decimal m: number = (double)m; return true;
            case double d: number = d; return true;
            default: number = 0; return false;
        }
    }

    /// <summary>Whether two values match, numbers within the tolerance: |a − b| ≤ max(absolute, relative × |b|).</summary>
    public static bool Equal(object? a, object? b, Tolerance tolerance)
    {
        if (a is null || b is null) return a is null && b is null;
        var aNumber = TryNumber(a, out var x);
        var bNumber = TryNumber(b, out var y);
        if (aNumber && bNumber)
        {
            if (a is decimal da && b is decimal db && tolerance.Absolute == 0 && tolerance.Relative == 0) return da == db;
            return Math.Abs(x - y) <= Math.Max(tolerance.Absolute, tolerance.Relative * Math.Abs(y));
        }
        if (aNumber || bNumber) return false; // a number and text are different values, even "1" and 1
        if (a is bool ba && b is bool bb) return ba == bb;
        if (a is bool || b is bool) return false;
        return string.Equals(Format(a), Format(b), StringComparison.Ordinal);
    }

    /// <summary>head − base when both are numbers, else null.</summary>
    public static double? Delta(object? @base, object? head) =>
        TryNumber(@base, out var b) && TryNumber(head, out var h) ? h - b : null;

    /// <summary>Invariant text of a value; used for keys and display.</summary>
    public static string Format(object? value) => value switch
    {
        null => "BLANK",
        bool b => b ? "TRUE" : "FALSE",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}

/// <summary>Numeric tolerance for comparisons.</summary>
public sealed record Tolerance(double Absolute, double Relative)
{
    /// <summary>Default when neither the test file nor the config sets one: tiny, to absorb floating-point noise.</summary>
    public static Tolerance Default { get; } = new(1e-9, 1e-9);
}
