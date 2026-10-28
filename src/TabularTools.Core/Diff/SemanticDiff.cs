using TabularTools.Core.Config;
using TabularTools.Core.Diagnostics;
using TabularTools.Core.Git;
using TabularTools.Core.Loading;

namespace TabularTools.Core.Diff;

public sealed record DiffRequest
{
    /// <summary>Repository folder (any folder inside it). Default: the working directory.</summary>
    public string? Repo { get; init; }
    /// <summary>Base git ref, or a model folder/file for folder diffs.</summary>
    public required string Base { get; init; }
    /// <summary>Head git ref, <c>WORKTREE</c>, or a model folder/file. Default: HEAD.</summary>
    public string? Head { get; init; }
    /// <summary>The model to diff (repo-relative, relative to the working directory, or absolute). Default: every model changed between the sides.</summary>
    public string? ModelPath { get; init; }
    /// <summary>Compare against merge-base(base, head), like a pull request does. Default: true.</summary>
    public bool MergeBase { get; init; } = true;
    public ChangeSeverity? MinSeverity { get; init; }
    public bool? IncludeFormattingOnly { get; init; }
    public DiffFailOn? FailOn { get; init; }
    public double? RenameThreshold { get; init; }
    /// <summary>Explicit config file. Default: the nearest tabulartools.yaml above the repository folder or working directory.</summary>
    public string? ConfigPath { get; init; }
    public required string WorkingDirectory { get; init; }
}

/// <summary>One model's comparison, with the loaded sides so callers (review, diff-aware lint) can reuse them.</summary>
public sealed record ModelComparison(string ModelPath, LoadedSide? Base, LoadedSide? Head, DiffOutcome Outcome, ModelDiff Diff);

public sealed record DiffRun(DiffResult Result, IReadOnlyList<ModelComparison> Models, TabularToolsConfig? Config, IGitReader? Git);

/// <summary>Runs <c>diff_model</c>: resolves the two sides, finds the models to compare, and diffs each one.</summary>
public static class SemanticDiff
{
    public const string WorktreeRef = "WORKTREE";

    public static DiffRun Run(DiffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Base);
        var cwd = Path.GetFullPath(request.WorkingDirectory);
        var head = string.IsNullOrWhiteSpace(request.Head) ? "HEAD" : request.Head.Trim();

        var basePath = Path.GetFullPath(Path.Combine(cwd, request.Base));
        var headPath = Path.GetFullPath(Path.Combine(cwd, head));
        if (IsModelPath(basePath) && IsModelPath(headPath)) return RunFolders(request, basePath, headPath, cwd);

        var git = GitCliReader.Open(Path.GetFullPath(Path.Combine(cwd, request.Repo ?? ".")));
        var config = LoadConfig(request, git.RepositoryRoot);
        var options = Options(request, config);
        var diagnostics = new List<Diagnostic>();

        var worktree = head.Equals(WorktreeRef, StringComparison.OrdinalIgnoreCase);
        var headCommit = worktree ? null : git.ResolveCommit(head);
        var givenBase = git.ResolveCommit(request.Base);
        var baseCommit = givenBase;
        var usedMergeBase = false;
        if (request.MergeBase)
        {
            var mb = git.MergeBase(givenBase, headCommit ?? git.ResolveCommit("HEAD"));
            if (mb is null) diagnostics.Add(ModelSides.Warning("DIFF001", $"'{request.Base}' and '{head}' share no history; comparing with '{request.Base}' directly."));
            else { baseCommit = mb; usedMergeBase = true; }
        }

        var modelPaths = request.ModelPath is { Length: > 0 } given
            ? [ModelSides.ToRepoPath(git, cwd, given)]
            : ModelSides.ModelPathsFromChanges(git.ChangedPaths(baseCommit, headCommit));

        var comparisons = new List<ModelComparison>();
        foreach (var modelPath in modelPaths)
        {
            var baseSide = ModelSides.AtCommit(git, baseCommit, modelPath);
            var headSide = worktree
                ? ModelSides.FromDisk(Path.Combine(git.RepositoryRoot, modelPath.Replace('/', Path.DirectorySeparatorChar)), git.RepositoryRoot)
                : ModelSides.AtCommit(git, headCommit!, modelPath);
            if (baseSide is null && headSide is null)
            {
                diagnostics.Add(ModelSides.Warning("DIFF002", $"No model at '{modelPath}' in either version."));
                continue;
            }
            comparisons.Add(Compare(modelPath, baseSide, headSide, options));
        }

        var failOn = FailOnFor(request, config);
        var result = new DiffResult
        {
            Base = new DiffSide(DiffSideKind.Ref, request.Base, baseCommit),
            Head = worktree ? new DiffSide(DiffSideKind.Worktree, WorktreeRef, Path: git.RepositoryRoot) : new DiffSide(DiffSideKind.Ref, head, headCommit),
            MergeBase = usedMergeBase,
            Models = comparisons.Select(c => c.Diff).ToList(),
            FailOn = failOn,
            Blocking = IsBlocking(comparisons.Select(c => c.Diff), failOn),
            Diagnostics = diagnostics.Count > 0 ? diagnostics : null,
            ConfigPath = config?.Path,
        };
        return new DiffRun(result, comparisons, config, git);
    }

    private static DiffRun RunFolders(DiffRequest request, string basePath, string headPath, string cwd)
    {
        var config = LoadConfig(request, headPath);
        var options = Options(request, config);
        var baseSide = ModelSides.FromDisk(basePath, cwd)!;
        var headSide = ModelSides.FromDisk(headPath, cwd)!;
        var label = Path.GetRelativePath(cwd, headSide.Model.Source.SemanticModelFolder ?? headSide.Model.Source.DefinitionPath).Replace('\\', '/');
        var comparison = Compare(label, baseSide, headSide, options);
        var failOn = FailOnFor(request, config);
        var result = new DiffResult
        {
            Base = new DiffSide(DiffSideKind.Folder, Path: Path.GetRelativePath(cwd, basePath).Replace('\\', '/')),
            Head = new DiffSide(DiffSideKind.Folder, Path: Path.GetRelativePath(cwd, headPath).Replace('\\', '/')),
            Models = [comparison.Diff],
            FailOn = failOn,
            Blocking = IsBlocking([comparison.Diff], failOn),
            ConfigPath = config?.Path,
        };
        return new DiffRun(result, [comparison], config, null);
    }

    /// <summary>Diffs one model whose sides are already loaded.</summary>
    public static ModelComparison Compare(string modelPath, LoadedSide? baseSide, LoadedSide? headSide, DiffOptions options)
    {
        var outcome = DiffEngine.Compare(
            baseSide?.ToInput() ?? new DiffInput(null),
            headSide?.ToInput() ?? new DiffInput(null),
            options);
        var status = baseSide is null ? ModelDiffStatus.Added
            : headSide is null ? ModelDiffStatus.Removed
            : outcome.Changes.Count == 0 ? ModelDiffStatus.Unchanged : ModelDiffStatus.Modified;
        var diff = new ModelDiff
        {
            ModelPath = modelPath,
            Name = (headSide ?? baseSide)!.Model.Snapshot.Name,
            Status = status,
            Summary = Summarize(outcome.Changes, outcome.FormattingOnlyHidden),
            Changes = outcome.Changes,
        };
        return new ModelComparison(modelPath, baseSide, headSide, outcome, diff);
    }

    public static DiffSummary Summarize(IReadOnlyList<Change> changes, int formattingHidden) => new(
        changes.Count(c => c.Severity == ChangeSeverity.Breaking),
        changes.Count(c => c.Severity == ChangeSeverity.High),
        changes.Count(c => c.Severity == ChangeSeverity.Medium),
        changes.Count(c => c.Severity == ChangeSeverity.Low),
        changes.Count(c => c.Severity == ChangeSeverity.Info),
        changes.Count,
        formattingHidden);

    public static bool IsBlocking(IEnumerable<ModelDiff> models, DiffFailOn failOn) =>
        failOn != DiffFailOn.None && models.Any(m => m.Changes.Any(c => (int)c.Severity >= (int)failOn));

    public static DiffOptions Options(DiffRequest request, TabularToolsConfig? config) => new()
    {
        SeverityOverrides = ConfigLoader.DiffSeverityOverrides(config),
        RenameThreshold = request.RenameThreshold ?? config?.Diff.RenameThreshold ?? ObjectMatcher.DefaultRenameThreshold,
        IncludeFormattingOnly = request.IncludeFormattingOnly ?? config?.Diff.IncludeFormattingOnly ?? false,
        MinSeverity = request.MinSeverity,
    };

    private static DiffFailOn FailOnFor(DiffRequest request, TabularToolsConfig? config) =>
        request.FailOn ?? (config?.Diff.FailOn is { } f ? ChangeSeverityParsing.ParseFailOn(f) : DiffFailOn.None);

    private static TabularToolsConfig? LoadConfig(DiffRequest request, string start) =>
        request.ConfigPath is { Length: > 0 } explicitPath
            ? ConfigLoader.Load(Path.GetFullPath(Path.Combine(request.WorkingDirectory, explicitPath)))
            : ConfigLoader.Discover(start);

    private static bool IsModelPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        try { ModelLocator.Resolve(path); return true; }
        catch (ModelLoadException) { return false; }
    }
}
