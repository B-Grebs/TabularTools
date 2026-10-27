using System.Text.Json.Serialization;

namespace TabularTools.Core.Loading;

/// <summary>What kind of input a model was loaded from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelSourceKind>))]
public enum ModelSourceKind
{
    /// <summary>A <c>*.pbip</c> project file.</summary>
    [JsonStringEnumMemberName("pbip")] Pbip,
    /// <summary>A <c>*.SemanticModel</c> folder.</summary>
    [JsonStringEnumMemberName("semanticModel")] SemanticModelFolder,
    /// <summary>A TMDL definition folder.</summary>
    [JsonStringEnumMemberName("tmdl")] TmdlFolder,
    /// <summary>A TMSL <c>model.bim</c> file.</summary>
    [JsonStringEnumMemberName("bim")] Bim,
}

/// <summary>The serialization format of the model definition itself.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelFormat>))]
public enum ModelFormat
{
    [JsonStringEnumMemberName("tmdl")] Tmdl,
    [JsonStringEnumMemberName("tmsl")] Tmsl,
}

/// <summary>A user-supplied path resolved to the model definition it points at.</summary>
/// <param name="InputPath">The full path the user gave.</param>
/// <param name="Kind">What the input path was.</param>
/// <param name="Format">TMDL folder or TMSL file.</param>
/// <param name="DefinitionPath">The TMDL folder or the <c>model.bim</c> file that is actually read.</param>
/// <param name="SemanticModelFolder">The enclosing <c>*.SemanticModel</c> folder, when there is one.</param>
/// <param name="Name">Model name, taken from the <c>*.SemanticModel</c> folder name or the definition folder.</param>
public sealed record ResolvedModelSource(
    string InputPath,
    ModelSourceKind Kind,
    ModelFormat Format,
    string DefinitionPath,
    string? SemanticModelFolder,
    string Name);
