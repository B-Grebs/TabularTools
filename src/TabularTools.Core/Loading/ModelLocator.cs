using System.Text.Json;
using TabularTools.Core.Diagnostics;

namespace TabularTools.Core.Loading;

/// <summary>
/// Resolves a path the user gave (a <c>.pbip</c> file, a <c>*.SemanticModel</c> folder, a TMDL
/// <c>definition</c> folder, a <c>model.bim</c> file, or a folder containing exactly one of those)
/// to the model definition that should be read.
/// </summary>
public static class ModelLocator
{
    private const string SemanticModelSuffix = ".SemanticModel";

    public static ResolvedModelSource Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);

        if (File.Exists(full))
        {
            var ext = Path.GetExtension(full);
            if (ext.Equals(".pbip", StringComparison.OrdinalIgnoreCase)) return FromPbip(full);
            if (ext.Equals(".bim", StringComparison.OrdinalIgnoreCase)) return FromBim(full, full, ModelSourceKind.Bim);
            if (Path.GetFileName(full).Equals("definition.pbism", StringComparison.OrdinalIgnoreCase))
                return FromSemanticModelFolder(Path.GetDirectoryName(full)!, full);
            throw NotAModel(full);
        }

        if (!Directory.Exists(full))
            throw new ModelLoadException(new Diagnostic(DiagnosticLevel.Error, DiagnosticCodes.PathNotFound, $"Path not found: {full}"));

        if (IsSemanticModelFolder(full)) return FromSemanticModelFolder(full, full);
        if (IsTmdlFolder(full)) return FromTmdlFolder(full, full, ModelSourceKind.TmdlFolder);

        // Convenience: a folder containing exactly one project or semantic model.
        var pbips = Directory.GetFiles(full, "*.pbip");
        var models = Directory.GetDirectories(full).Where(IsSemanticModelFolder).ToArray();
        if (pbips.Length == 1 && models.Length <= 1) return FromPbip(pbips[0]);
        if (pbips.Length == 0 && models.Length == 1) return FromSemanticModelFolder(models[0], full);
        if (pbips.Length + models.Length > 1)
        {
            var candidates = pbips.Concat(models).Select(p => Path.GetRelativePath(full, p)).Order(StringComparer.Ordinal);
            throw new ModelLoadException(new Diagnostic(DiagnosticLevel.Error, DiagnosticCodes.AmbiguousModel,
                $"Folder contains more than one model; pass one of: {string.Join(", ", candidates)}", full));
        }

        throw NotAModel(full);
    }

    private static ResolvedModelSource FromPbip(string pbipPath)
    {
        var folder = Path.GetDirectoryName(pbipPath)!;
        string? modelFolder = null;
        string? problem = null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pbipPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array)
            {
                foreach (var artifact in artifacts.EnumerateArray())
                {
                    if (TryGetPath(artifact, "semanticModel", out var smPath) || TryGetPath(artifact, "dataset", out smPath))
                    {
                        modelFolder = Path.GetFullPath(Path.Combine(folder, smPath));
                        break;
                    }
                    if (TryGetPath(artifact, "report", out var reportPath))
                    {
                        var reportFolder = Path.GetFullPath(Path.Combine(folder, reportPath));
                        modelFolder = ModelFolderFromReport(reportFolder, out problem);
                        if (modelFolder is not null) break;
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            problem = $"Could not read {Path.GetFileName(pbipPath)}: {ex.Message}";
        }

        // Fall back to a single sibling semantic model folder.
        if (modelFolder is null)
        {
            var siblings = Directory.GetDirectories(folder).Where(IsSemanticModelFolder).ToArray();
            if (siblings.Length == 1) modelFolder = siblings[0];
        }

        if (modelFolder is null || !IsSemanticModelFolder(modelFolder))
            throw new ModelLoadException(new Diagnostic(DiagnosticLevel.Error, DiagnosticCodes.PbipUnresolved,
                problem ?? "Could not find the semantic model this project uses. Pass the *.SemanticModel folder instead.", pbipPath));

        var resolved = FromSemanticModelFolder(modelFolder, pbipPath);
        return resolved with { Kind = ModelSourceKind.Pbip };
    }

    /// <summary>Reads a report's <c>definition.pbir</c> and follows a <c>byPath</c> dataset reference.</summary>
    internal static string? ModelFolderFromReport(string reportFolder, out string? problem)
    {
        problem = null;
        var pbir = Path.Combine(reportFolder, "definition.pbir");
        if (!File.Exists(pbir))
        {
            problem = $"Report folder has no definition.pbir: {reportFolder}";
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(pbir), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("datasetReference", out var reference))
        {
            problem = "definition.pbir has no datasetReference.";
            return null;
        }
        if (reference.TryGetProperty("byPath", out var byPath) && byPath.ValueKind == JsonValueKind.Object &&
            byPath.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
        {
            return Path.GetFullPath(Path.Combine(reportFolder, p.GetString()!));
        }
        if (reference.TryGetProperty("byConnection", out var byConnection) && byConnection.ValueKind == JsonValueKind.Object)
        {
            problem = "The report is bound to a published semantic model (byConnection), not a local one. Pass the *.SemanticModel folder instead.";
        }
        return null;
    }

    private static ResolvedModelSource FromSemanticModelFolder(string folder, string inputPath)
    {
        var name = ModelNameFromFolder(folder);
        var definition = Path.Combine(folder, "definition");
        if (Directory.Exists(definition) && IsTmdlFolder(definition))
            return new ResolvedModelSource(inputPath, ModelSourceKind.SemanticModelFolder, ModelFormat.Tmdl, definition, folder, name);

        var bim = Path.Combine(folder, "model.bim");
        if (File.Exists(bim))
            return new ResolvedModelSource(inputPath, ModelSourceKind.SemanticModelFolder, ModelFormat.Tmsl, bim, folder, name);

        throw new ModelLoadException(new Diagnostic(DiagnosticLevel.Error, DiagnosticCodes.NotAModel,
            "Semantic model folder has neither a TMDL 'definition' folder nor a model.bim file.", folder));
    }

    private static ResolvedModelSource FromTmdlFolder(string folder, string inputPath, ModelSourceKind kind)
    {
        var parent = Path.GetDirectoryName(folder);
        var semanticModelFolder = parent is not null && IsSemanticModelFolder(parent) ? parent : null;
        var name = semanticModelFolder is not null ? ModelNameFromFolder(semanticModelFolder) : Path.GetFileName(folder);
        return new ResolvedModelSource(inputPath, kind, ModelFormat.Tmdl, folder, semanticModelFolder, name);
    }

    private static ResolvedModelSource FromBim(string bimPath, string inputPath, ModelSourceKind kind)
    {
        var parent = Path.GetDirectoryName(bimPath)!;
        var semanticModelFolder = IsSemanticModelFolder(parent) ? parent : null;
        var name = semanticModelFolder is not null ? ModelNameFromFolder(semanticModelFolder) : Path.GetFileNameWithoutExtension(bimPath);
        return new ResolvedModelSource(inputPath, kind, ModelFormat.Tmsl, bimPath, semanticModelFolder, name);
    }

    internal static bool IsSemanticModelFolder(string folder) =>
        File.Exists(Path.Combine(folder, "definition.pbism")) ||
        (folder.EndsWith(SemanticModelSuffix, StringComparison.OrdinalIgnoreCase) &&
         (Directory.Exists(Path.Combine(folder, "definition")) || File.Exists(Path.Combine(folder, "model.bim"))));

    internal static bool IsTmdlFolder(string folder) =>
        File.Exists(Path.Combine(folder, "database.tmdl")) || File.Exists(Path.Combine(folder, "model.tmdl"));

    private static string ModelNameFromFolder(string folder)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        return name.EndsWith(SemanticModelSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^SemanticModelSuffix.Length] : name;
    }

    private static bool TryGetPath(JsonElement artifact, string property, out string path)
    {
        path = "";
        if (artifact.ValueKind == JsonValueKind.Object &&
            artifact.TryGetProperty(property, out var obj) && obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
        {
            path = p.GetString()!;
            return path.Length > 0;
        }
        return false;
    }

    private static ModelLoadException NotAModel(string path) =>
        new(new Diagnostic(DiagnosticLevel.Error, DiagnosticCodes.NotAModel,
            "Not a semantic model. Expected a .pbip file, a *.SemanticModel folder, a TMDL definition folder or a model.bim file.", path));
}
