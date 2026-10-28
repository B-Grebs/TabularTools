using System.Diagnostics;

namespace TabularTools.Tests.Shared;

/// <summary>
/// A temporary git repository built from the diff fixtures: <c>main</c> holds ExampleSales v1, <c>feature</c> holds v2.
/// Author, committer and dates are fixed, so commit ids are the same on every run and platform.
/// </summary>
internal sealed class GitFixture : IDisposable
{
    public const string ModelPath = "ExampleSales.SemanticModel";

    private readonly TempDir _dir = new();

    public GitFixture(bool checkoutFeature = true)
    {
        Git("init", "-q", "-b", "main");
        CopyVersion("ExampleSales-v1");
        Commit("v1", "2026-01-01T00:00:00Z");
        MainCommit = Git("rev-parse", "HEAD").Trim();
        Git("checkout", "-q", "-b", "feature");
        CopyVersion("ExampleSales-v2");
        Commit("v2", "2026-01-02T00:00:00Z");
        FeatureCommit = Git("rev-parse", "HEAD").Trim();
        if (!checkoutFeature) Git("checkout", "-q", "main");
    }

    public string Path => _dir.Path;

    public string MainCommit { get; }

    public string FeatureCommit { get; }

    public string Combine(params string[] parts) => _dir.Combine(parts);

    public static string DiffFixture(string version) => System.IO.Path.Combine(TestPaths.Fixtures, "diff", version);

    /// <summary>Replaces the model folder in the working tree with a fixture version.</summary>
    public void CopyVersion(string version)
    {
        var target = Combine(ModelPath);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        var source = DiffFixture(version);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(System.IO.Path.Combine(Path, System.IO.Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, System.IO.Path.Combine(Path, System.IO.Path.GetRelativePath(source, file)), overwrite: true);
    }

    public void Commit(string message, string date)
    {
        Git("add", "-A");
        Git(new Dictionary<string, string> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date }, "commit", "-q", "--no-gpg-sign", "-m", message);
    }

    public string Git(params string[] args) => Git(null, args);

    public string Git(IReadOnlyDictionary<string, string>? env, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = Path, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in new[] { "-c", "user.name=Example", "-c", "user.email=example@example.invalid", "-c", "core.autocrlf=false", "-c", "commit.gpgsign=false" }) info.ArgumentList.Add(a);
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["GIT_AUTHOR_NAME"] = "Example";
        info.Environment["GIT_AUTHOR_EMAIL"] = "example@example.invalid";
        info.Environment["GIT_COMMITTER_NAME"] = "Example";
        info.Environment["GIT_COMMITTER_EMAIL"] = "example@example.invalid";
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        if (env is not null) foreach (var (k, v) in env) info.Environment[k] = v;
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(1)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {string.Join(' ', args)} did not finish within a minute.");
        }
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error.Result}");
        return output.Result;
    }

    public void Dispose() => _dir.Dispose();
}
