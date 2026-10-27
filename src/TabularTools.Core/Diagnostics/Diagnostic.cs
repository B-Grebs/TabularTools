using System.Text.Json.Serialization;

namespace TabularTools.Core.Diagnostics;

/// <summary>How serious a diagnostic is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticLevel>))]
public enum DiagnosticLevel
{
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("info")] Info,
}

/// <summary>A problem found while loading or analysing a model, with an optional source location.</summary>
/// <param name="Level">Severity.</param>
/// <param name="Code">Stable identifier, e.g. <c>TMDL001</c>.</param>
/// <param name="Message">Human-readable explanation.</param>
/// <param name="File">Path of the file the problem is in, when known.</param>
/// <param name="Line">1-based line number, when known.</param>
public sealed record Diagnostic(DiagnosticLevel Level, string Code, string Message, string? File = null, int? Line = null)
{
    public override string ToString()
    {
        var where = File is null ? "" : Line is null ? $"{File}: " : $"{File}({Line}): ";
        return $"{where}{Level.ToString().ToLowerInvariant()} {Code}: {Message}";
    }
}

/// <summary>Stable diagnostic codes.</summary>
public static class DiagnosticCodes
{
    public const string PathNotFound = "LOAD001";
    public const string NotAModel = "LOAD002";
    public const string AmbiguousModel = "LOAD003";
    public const string PbipUnresolved = "LOAD004";
    public const string TmdlFormat = "TMDL001";
    public const string TmdlUnresolvedReference = "TMDL002";
    public const string TmdlOther = "TMDL003";
    public const string TmslInvalid = "TMSL001";
}
