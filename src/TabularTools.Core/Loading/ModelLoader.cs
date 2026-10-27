using System.Security.Cryptography;
using System.Text;
using Microsoft.AnalysisServices.Tabular.Tmdl;
using TabularTools.Core.Diagnostics;
using TabularTools.Core.Snapshots;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace TabularTools.Core.Loading;

/// <summary>A model loaded from disk: the TOM database, its snapshot, and a hash of the files it came from.</summary>
public sealed record LoadedModel(
    ResolvedModelSource Source,
    string ContentHash,
    TOM.Database Database,
    ModelSnapshot Snapshot,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Loads models from TMDL folders and TMSL files. Never writes anything.</summary>
public static class ModelLoader
{
    public static LoadedModel Load(string path) => Load(ModelLocator.Resolve(path));

    public static LoadedModel Load(ResolvedModelSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var hash = ComputeContentHash(source);
        var database = source.Format == ModelFormat.Tmdl ? LoadTmdl(source) : LoadTmsl(source);

        // PBIP database.tmdl carries no name, and TMSL serialization fails on an empty one.
        if (string.IsNullOrEmpty(database.Name)) database.Name = source.Name;
        if (string.IsNullOrEmpty(database.ID)) database.ID = source.Name;

        var snapshot = SnapshotBuilder.Build(database);
        return new LoadedModel(source, hash, database, snapshot, []);
    }

    private static TOM.Database LoadTmdl(ResolvedModelSource source)
    {
        try
        {
            return TOM.TmdlSerializer.DeserializeDatabaseFromFolder(source.DefinitionPath);
        }
        catch (TmdlFormatException ex)
        {
            var file = ex.HasDocumentSourceInfo ? DocumentToFile(source.DefinitionPath, ex.Document) : null;
            int? line = ex.HasDocumentSourceInfo && ex.Line > 0 ? ex.Line : null;
            throw new ModelLoadException([new Diagnostic(DiagnosticLevel.Error, DiagnosticCodes.TmdlFormat, FirstDetail(ex.Message), file, line)], ex);
        }
        catch (TmdlDeserializationWithReferenceErrorsException ex)
        {
            var diagnostics = ReferenceErrorLines(ex.Message)
                .Select(l => new Diagnostic(DiagnosticLevel.Error, DiagnosticCodes.TmdlUnresolvedReference, l, source.DefinitionPath))
                .ToList();
            if (diagnostics.Count == 0)
                diagnostics.Add(new Diagnostic(DiagnosticLevel.Error, DiagnosticCodes.TmdlUnresolvedReference, ex.Message, source.DefinitionPath));
            throw new ModelLoadException(diagnostics, ex);
        }
        catch (Exception ex) when (ex is TmdlSerializationException or InvalidOperationException or ArgumentException or IOException)
        {
            throw new ModelLoadException([new Diagnostic(DiagnosticLevel.Error, DiagnosticCodes.TmdlOther, ex.Message, source.DefinitionPath)], ex);
        }
    }

    private static TOM.Database LoadTmsl(ResolvedModelSource source)
    {
        try
        {
            return TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(source.DefinitionPath));
        }
        catch (Exception ex) when (ex is Microsoft.AnalysisServices.JsonSerializationException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            throw new ModelLoadException([new Diagnostic(DiagnosticLevel.Error, DiagnosticCodes.TmslInvalid, ex.Message, source.DefinitionPath)], ex);
        }
    }

    /// <summary>SHA-256 over every file of the definition (relative path + bytes), so any edit changes it.</summary>
    public static string ComputeContentHash(ResolvedModelSource source)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        IEnumerable<(string Relative, string Full)> files = source.Format == ModelFormat.Tmsl
            ? [(Path.GetFileName(source.DefinitionPath), source.DefinitionPath)]
            : Directory.EnumerateFiles(source.DefinitionPath, "*", SearchOption.AllDirectories)
                .Select(f => (Path.GetRelativePath(source.DefinitionPath, f).Replace('\\', '/'), f))
                .OrderBy(f => f.Item1, StringComparer.Ordinal);

        foreach (var (relative, full) in files)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(relative));
            sha.AppendData([0]);
            sha.AppendData(File.ReadAllBytes(full));
            sha.AppendData([0]);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>TMDL reports documents as logical paths such as <c>./tables/Date</c>.</summary>
    internal static string DocumentToFile(string definitionPath, string? document)
    {
        if (string.IsNullOrEmpty(document)) return definitionPath;
        var relative = document.TrimStart('.', '/', '\\');
        if (!relative.EndsWith(".tmdl", StringComparison.OrdinalIgnoreCase)) relative += ".tmdl";
        return Path.Combine(definitionPath, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Pulls the "Detailed error" line out of TOM's multi-line TMDL error message.</summary>
    private static string FirstDetail(string message)
    {
        var lines = message.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var detail = lines.FirstOrDefault(l => l.StartsWith("Detailed error", StringComparison.OrdinalIgnoreCase));
        if (detail is not null)
        {
            var dash = detail.IndexOf('-', StringComparison.Ordinal);
            return dash >= 0 ? detail[(dash + 1)..].Trim() : detail;
        }
        return lines.FirstOrDefault() ?? message;
    }

    private static IEnumerable<string> ReferenceErrorLines(string message)
    {
        var lines = message.Split('\n').Select(l => l.Trim());
        return lines.SkipWhile(l => !l.StartsWith("Resolution Errors", StringComparison.OrdinalIgnoreCase)).Skip(1).Where(l => l.Length > 0);
    }
}
