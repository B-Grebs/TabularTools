using System.Text;
using System.Text.Json.Serialization;
using TabularTools.Core.Diff;

namespace TabularTools.Core.Writes;

[JsonConverter(typeof(JsonStringEnumConverter<WriteAction>))]
public enum WriteAction
{
    [JsonStringEnumMemberName("created")] Created,
    [JsonStringEnumMemberName("modified")] Modified,
    [JsonStringEnumMemberName("unchanged")] Unchanged,
}

/// <param name="Path">Absolute path of the file.</param>
/// <param name="UnifiedDiff">What changed (empty when unchanged; the whole file as additions when created).</param>
public sealed record FileChange(string Path, WriteAction Action, string UnifiedDiff);

/// <summary>Every write reports exactly which files it changed, or would change in a dry run (ARCHITECTURE §14.3).</summary>
public sealed record WriteResult(bool DryRun, IReadOnlyList<FileChange> Files);

/// <summary>
/// The single write path. Confines writes to the given roots (no <c>..</c> or symlink escapes), writes atomically
/// (temporary file, then rename), keeps an existing file's line endings and byte-order mark, and reports a diff.
/// </summary>
public sealed class FileWriter
{
    private readonly IReadOnlyList<string> _roots;

    public FileWriter(IEnumerable<string> roots)
    {
        _roots = roots.Select(r => Real(Path.GetFullPath(r))).Distinct(PathComparer).ToList();
        if (_roots.Count == 0) throw new ArgumentException("A file writer needs at least one root folder.");
    }

    public IReadOnlyList<string> Roots => _roots;

    public WriteResult Write(string path, string content, bool dryRun)
    {
        var full = Path.GetFullPath(path);
        var real = Real(full);
        if (!_roots.Any(root => IsUnder(real, root)))
            throw new ArgumentException($"Refusing to write {full}: it's outside the allowed folders ({string.Join(", ", _roots)}).");
        if (Directory.Exists(full)) throw new ArgumentException($"Refusing to write {full}: it's a folder.");

        var exists = File.Exists(full);
        var before = exists ? File.ReadAllText(full) : null;
        var bom = exists && HasBom(full);
        if (before is not null && before.Contains("\r\n", StringComparison.Ordinal))
            content = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

        var action = before is null ? WriteAction.Created : string.Equals(before, content, StringComparison.Ordinal) ? WriteAction.Unchanged : WriteAction.Modified;
        var diff = action == WriteAction.Unchanged ? "" : TextDiff.Unified(Lf(before) ?? "", Lf(content));
        if (!dryRun && action != WriteAction.Unchanged)
        {
            var dir = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(dir);
            var temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temp, content, new UTF8Encoding(bom));
                File.Move(temp, full, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        return new WriteResult(dryRun, [new FileChange(full, action, diff)]);
    }

    private static string? Lf(string? text) => text?.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static bool HasBom(string path)
    {
        Span<byte> head = stackalloc byte[3];
        using var stream = File.OpenRead(path);
        return stream.Read(head) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var r = Path.TrimEndingDirectorySeparator(root);
        return path.Equals(r, comparison) || path.StartsWith(r + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>The path with every existing symlink along it resolved (the not-yet-existing tail is kept as is).</summary>
    internal static string Real(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        var parts = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            FileSystemInfo? info = Directory.Exists(next) ? new DirectoryInfo(next) : File.Exists(next) ? new FileInfo(next) : null;
            if (info is null) return Path.Combine([next, .. parts[(i + 1)..]]);
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                next = target is null ? next : Path.GetFullPath(target.FullName);
                next = Real(next);
            }
            current = next;
        }
        return current;
    }
}
