using System.Diagnostics;
using System.Text;

namespace TabularTools.Core.Git;

/// <summary>A git problem: git missing, not a repository, unknown ref, or a shallow clone without the base commit.</summary>
public sealed class GitException(string message, string? hint = null) : Exception(hint is null ? message : $"{message} {hint}")
{
    public string? Hint { get; } = hint;
}

/// <summary>One file in a commit's tree.</summary>
/// <param name="Path">Repo-relative path with forward slashes.</param>
/// <param name="ObjectId">Blob id.</param>
public sealed record GitTreeEntry(string Path, string ObjectId);

/// <summary>Read-only access to a git repository. Never checks anything out and never writes to the repository.</summary>
public interface IGitReader
{
    /// <summary>The repository's top-level folder.</summary>
    string RepositoryRoot { get; }

    /// <summary>Full commit id for a ref (branch, tag, sha, <c>HEAD~1</c>, ...).</summary>
    string ResolveCommit(string revision);

    /// <summary>The best common ancestor of two commits, or null when they share no history.</summary>
    string? MergeBase(string a, string b);

    /// <summary>Files under <paramref name="path"/> (repo-relative; empty for the whole tree) at a commit.</summary>
    IReadOnlyList<GitTreeEntry> ListTree(string commit, string path);

    /// <summary>Contents of several blobs, read through one git process.</summary>
    IReadOnlyDictionary<string, byte[]> ReadBlobs(IEnumerable<string> objectIds);

    /// <summary>Repo-relative paths that differ between two commits, or between a commit and the working tree when <paramref name="head"/> is null.</summary>
    IReadOnlyList<string> ChangedPaths(string baseCommit, string? head);

    bool IsShallow { get; }

    /// <summary>
    /// Repo-relative path (forward slashes) of an existing file or folder in the working tree, as git sees it. Robust to
    /// symlinks and Windows short (8.3) names, which make plain string comparison with <see cref="RepositoryRoot"/> fail.
    /// Returns null when the path doesn't exist or is outside the repository.
    /// </summary>
    string? RelativePath(string fullPath);
}

/// <summary><see cref="IGitReader"/> over the git command-line tool (ADR-0003).</summary>
public sealed class GitCliReader : IGitReader
{
    private readonly string _gitDir;

    private GitCliReader(string root)
    {
        RepositoryRoot = root;
        _gitDir = root;
    }

    public string RepositoryRoot { get; }

    /// <summary>Opens the repository that contains <paramref name="path"/>.</summary>
    public static GitCliReader Open(string path)
    {
        var full = Path.GetFullPath(path);
        var start = Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? full;
        if (!Directory.Exists(start)) throw new GitException($"Folder not found: {start}");
        var result = Run(start, ["rev-parse", "--show-toplevel"]);
        if (result.ExitCode != 0)
            throw new GitException($"Not a git repository: {start}.", "Pass --repo with the repository folder, or compare two folders instead of refs.");
        return new GitCliReader(Path.GetFullPath(result.Text.Trim()));
    }

    public bool IsShallow => Git(["rev-parse", "--is-shallow-repository"]).Trim() == "true";

    public string ResolveCommit(string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        if (revision.StartsWith('-')) throw new GitException($"Invalid git ref '{revision}'.");
        var result = Run(_gitDir, ["rev-parse", "--verify", "--quiet", "--end-of-options", revision + "^{commit}"]);
        if (result.ExitCode == 0) return result.Text.Trim();

        var hint = IsShallow
            ? "The repository is a shallow clone; fetch full history (in Azure Pipelines: checkout with fetchDepth: 0)."
            : "Check the branch name; in CI, remote branches are usually named origin/<branch>.";
        throw new GitException($"Unknown git ref '{revision}'.", hint);
    }

    public string? MergeBase(string a, string b)
    {
        var result = Run(_gitDir, ["merge-base", a, b]);
        if (result.ExitCode == 0) return result.Text.Trim();
        if (result.ExitCode == 1 && IsShallow)
            throw new GitException($"No merge base between {Short(a)} and {Short(b)}: the repository is a shallow clone.",
                "Fetch full history (in Azure Pipelines: checkout with fetchDepth: 0), or pass --no-merge-base.");
        if (result.ExitCode == 1) return null;
        throw new GitException($"git merge-base failed: {result.Error.Trim()}");
    }

    public IReadOnlyList<GitTreeEntry> ListTree(string commit, string path)
    {
        var args = new List<string> { "ls-tree", "-r", "-z", "--full-tree", commit };
        if (!string.IsNullOrEmpty(path)) { args.Add("--"); args.Add(path.TrimEnd('/')); }
        var output = Git(args);
        var entries = new List<GitTreeEntry>();
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> <type> <id>\t<path>"
            var tab = record.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0) continue;
            var meta = record[..tab].Split(' ');
            if (meta.Length < 3 || meta[1] != "blob") continue;
            entries.Add(new GitTreeEntry(record[(tab + 1)..], meta[2]));
        }
        return entries;
    }

    public IReadOnlyDictionary<string, byte[]> ReadBlobs(IEnumerable<string> objectIds)
    {
        var ids = objectIds.Distinct(StringComparer.Ordinal).ToList();
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (ids.Count == 0) return blobs;

        using var process = Start(_gitDir, ["cat-file", "--batch"], redirectInput: true);
        var errorTask = process.StandardError.ReadToEndAsync();
        var writer = Task.Run(() =>
        {
            foreach (var id in ids) process.StandardInput.Write(id + "\n");
            process.StandardInput.Close();
        });

        var stdout = process.StandardOutput.BaseStream;
        foreach (var _ in ids)
        {
            var header = ReadLine(stdout) ?? throw new GitException("git cat-file ended early.");
            var parts = header.Split(' ');
            if (parts.Length < 3 || parts[1] == "missing") throw new GitException($"git object not found: {parts[0]}");
            var size = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            var buffer = new byte[size];
            stdout.ReadExactly(buffer);
            stdout.ReadByte(); // trailing newline
            blobs[parts[0]] = buffer;
        }
        writer.Wait();
        process.WaitForExit();
        _ = errorTask.Result;
        return blobs;
    }

    public IReadOnlyList<string> ChangedPaths(string baseCommit, string? head)
    {
        var args = new List<string> { "-c", "core.quotePath=false", "diff", "--name-only", "-z", "--no-renames", baseCommit };
        if (head is not null) args.Add(head);
        var paths = Git(args).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (head is null)
            paths.AddRange(Git(["ls-files", "-z", "--others", "--exclude-standard"]).Split('\0', StringSplitOptions.RemoveEmptyEntries));
        return paths.Distinct(StringComparer.Ordinal).ToList();
    }

    public string? RelativePath(string fullPath)
    {
        var isFile = File.Exists(fullPath);
        var dir = isFile ? Path.GetDirectoryName(fullPath) : fullPath;
        if (dir is null || !Directory.Exists(dir)) return null;
        var result = Run(dir, ["rev-parse", "--show-toplevel", "--show-prefix"]);
        if (result.ExitCode != 0) return null;
        var lines = result.Text.Split('\n');
        if (lines.Length < 2) return null;
        var top = Path.GetFullPath(lines[0].Trim());
        if (!string.Equals(top, RepositoryRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        var prefix = lines[1].Trim().TrimEnd('/');
        if (isFile) prefix = prefix.Length == 0 ? Path.GetFileName(fullPath) : prefix + "/" + Path.GetFileName(fullPath);
        return prefix;
    }

    public static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;

    private string Git(IReadOnlyList<string> args)
    {
        var result = Run(_gitDir, args);
        if (result.ExitCode != 0) throw new GitException($"git {args.FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('='))} failed: {result.Error.Trim()}");
        return result.Text;
    }

    private sealed record RunResult(int ExitCode, string Text, string Error);

    private static RunResult Run(string workingDirectory, IReadOnlyList<string> args)
    {
        using var process = Start(workingDirectory, args, redirectInput: false);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new RunResult(process.ExitCode, output.Result, error.Result);
    }

    private static Process Start(string workingDirectory, IReadOnlyList<string> args, bool redirectInput)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Never prompt for credentials and never page output; everything here is local and read-only.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_PAGER"] = "cat";
        info.Environment["LC_ALL"] = "C";
        foreach (var arg in args) info.ArgumentList.Add(arg);
        try
        {
            return Process.Start(info) ?? throw new GitException("Could not start git.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GitException($"git was not found on PATH ({ex.Message}).", "Install git, or compare two folders instead of refs.");
        }
    }

    private static string? ReadLine(Stream stream)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (b == '\n') return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add((byte)b);
        }
    }
}
