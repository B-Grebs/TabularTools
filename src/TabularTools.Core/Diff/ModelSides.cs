using System.Text;
using TabularTools.Core.Diagnostics;
using TabularTools.Core.Git;
using TabularTools.Core.Loading;

namespace TabularTools.Core.Diff;

/// <summary>A model loaded for one side of a comparison, with its source locations.</summary>
public sealed record LoadedSide(LoadedModel Model, SourceLocationIndex Locations)
{
    public DiffInput ToInput() => new(Model.Snapshot, Model.Dependencies, Locations);
}

/// <summary>Loads a model as it is at a git commit, in the working tree, or in a folder.</summary>
public static class ModelSides
{
    /// <summary>
    /// Loads the model at <paramref name="modelPath"/> (repo-relative) as of <paramref name="commit"/>. Returns null when the
    /// path doesn't exist at that commit. The files are read from git objects and written to a temporary folder that is
    /// deleted before returning; nothing in the repository or working tree is touched.
    /// </summary>
    public static LoadedSide? AtCommit(IGitReader git, string commit, string modelPath)
    {
        modelPath = NormalizeRepoPath(modelPath);
        var entries = git.ListTree(commit, modelPath)
            .Where(e => e.Path == modelPath || e.Path.StartsWith(modelPath + "/", StringComparison.Ordinal))
            .ToList();
        if (entries.Count == 0) return null;

        var blobs = git.ReadBlobs(entries.Select(e => e.ObjectId));
        var temp = Path.Combine(Path.GetTempPath(), "tabulartools", "git-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            foreach (var entry in entries)
            {
                var target = Path.Combine(temp, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, blobs[entry.ObjectId]);
            }

            LoadedModel model;
            try
            {
                model = ModelLoader.Load(ModelLocator.Resolve(Path.Combine(temp, modelPath.Replace('/', Path.DirectorySeparatorChar))));
            }
            catch (ModelLoadException ex)
            {
                // Point diagnostics at "<commit>:<repo path>" instead of the temporary folder.
                var prefix = GitCliReader.Short(commit) + ":";
                throw new ModelLoadException(ex.Diagnostics.Select(d => d with
                {
                    File = d.File is null ? null : prefix + Path.GetRelativePath(temp, d.File).Replace('\\', '/'),
                    Message = $"{d.Message} (at {GitCliReader.Short(commit)})",
                }).ToList(), ex);
            }

            var texts = entries.Select(e => new KeyValuePair<string, string>(e.Path, Decode(blobs[e.ObjectId])));
            return new LoadedSide(model, SourceLocationIndex.Build(texts, model.Snapshot));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Loads a model from disk. <paramref name="displayRoot"/> is the folder location paths are shown relative to.</summary>
    public static LoadedSide? FromDisk(string path, string displayRoot)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return null;
        var model = ModelLoader.Load(ModelLocator.Resolve(path));
        return new LoadedSide(model, LocationsFromDisk(model.Source, model.Snapshot, displayRoot));
    }

    public static SourceLocationIndex LocationsFromDisk(ResolvedModelSource source, Snapshots.ModelSnapshot snapshot, string displayRoot)
    {
        if (source.Format != ModelFormat.Tmdl) return SourceLocationIndex.Empty;
        var files = Directory.EnumerateFiles(source.DefinitionPath, "*.tmdl", SearchOption.AllDirectories)
            .Select(f => new KeyValuePair<string, string>(Path.GetRelativePath(displayRoot, f).Replace('\\', '/'), File.ReadAllText(f)));
        return SourceLocationIndex.Build(files, snapshot);
    }

    internal static string NormalizeRepoPath(string path) => path.Replace('\\', '/').Trim('/');

    private static string Decode(byte[] bytes) => new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');

    /// <summary>
    /// The repo-relative folders of semantic models touched by a set of changed paths: the nearest <c>*.SemanticModel</c>
    /// ancestor of each path, plus standalone <c>model.bim</c> files.
    /// </summary>
    public static IReadOnlyList<string> ModelPathsFromChanges(IEnumerable<string> changedPaths)
    {
        var models = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in changedPaths)
        {
            var path = NormalizeRepoPath(raw);
            var segments = path.Split('/');
            var index = Array.FindLastIndex(segments, s => s.EndsWith(".SemanticModel", StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index < segments.Length - 1) models.Add(string.Join('/', segments.Take(index + 1)));
            else if (segments[^1].Equals("model.bim", StringComparison.OrdinalIgnoreCase)) models.Add(path);
        }
        return models.ToList();
    }

    /// <summary>A model path given by the user (absolute, relative to the working folder, or repo-relative), as a repo-relative path.</summary>
    public static string ToRepoPath(IGitReader git, string workingDirectory, string modelPath)
    {
        var repoRoot = git.RepositoryRoot;
        string? full = null;
        if (Path.IsPathRooted(modelPath)) full = Path.GetFullPath(modelPath);
        else
        {
            var fromCwd = Path.GetFullPath(Path.Combine(workingDirectory, modelPath));
            var fromRoot = Path.GetFullPath(Path.Combine(repoRoot, modelPath));
            full = File.Exists(fromCwd) || Directory.Exists(fromCwd) ? fromCwd : fromRoot;
        }

        // A .pbip, definition.pbism or TMDL folder on disk is resolved to the semantic model folder it points at.
        if (File.Exists(full) || Directory.Exists(full))
        {
            try
            {
                var resolved = ModelLocator.Resolve(full);
                full = resolved.SemanticModelFolder ?? resolved.DefinitionPath;
            }
            catch (ModelLoadException)
            {
                // Not loadable on disk (maybe only exists at the base ref); use the path as given.
            }
        }

        if (git.RelativePath(full) is { Length: > 0 } fromGit) return fromGit;
        var relative = Path.GetRelativePath(repoRoot, full);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException($"Model path '{modelPath}' is outside the repository {repoRoot}.");
        return NormalizeRepoPath(relative);
    }

    internal static Diagnostic Warning(string code, string message) => new(DiagnosticLevel.Warning, code, message);
}
