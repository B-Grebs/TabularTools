using TabularTools.Core.Diff;
using TabularTools.Core.Git;
using TabularTools.Core.Loading;

namespace TabularTools.Core.Lint;

/// <summary>
/// Diff-aware lint (ARCHITECTURE §9.4): lints the base version too, and marks a violation as new unless the same rule
/// fired on the same object in base. Objects are matched with the diff engine, so renames don't create "new" violations.
/// Only new violations block.
/// </summary>
public static class DiffAwareLint
{
    /// <summary>Lints <paramref name="head"/> and compares it with the model as of <paramref name="baseRef"/>.</summary>
    /// <param name="mergeBase">Compare with merge-base(baseRef, HEAD), like a pull request does.</param>
    public static LintResult Run(LoadedModel head, LintOptions options, string baseRef, string workingDirectory, bool mergeBase = true, string? handle = null)
    {
        ArgumentNullException.ThrowIfNull(head);
        var modelFolder = head.Source.SemanticModelFolder ?? head.Source.DefinitionPath;
        var git = GitCliReader.Open(modelFolder);
        var repoPath = ModelSides.ToRepoPath(git, workingDirectory, modelFolder);
        var commit = git.ResolveCommit(baseRef);
        if (mergeBase && git.MergeBase(commit, git.ResolveCommit("HEAD")) is { } mb) commit = mb;
        var baseline = ModelSides.AtCommit(git, commit, repoPath);
        return Run(head, baseline?.Model, options, baseRef, commit, handle);
    }

    /// <summary>Lints both versions and marks new violations. A missing base model makes every violation new.</summary>
    public static LintResult Run(LoadedModel head, LoadedModel? @base, LintOptions options, string baseRef, string baseCommit, string? handle = null)
    {
        var headResult = LintEngine.Run(head, options, handle);
        var existing = new HashSet<(string Rule, string Key)>();
        if (@base is not null)
        {
            var baseResult = LintEngine.Run(@base, options);
            var keyMap = ObjectMatcher.Match(@base.Snapshot, head.Snapshot,
                options.Config?.Diff.RenameThreshold ?? ObjectMatcher.DefaultRenameThreshold).KeyMap;
            foreach (var v in baseResult.Violations)
                existing.Add((v.RuleId, keyMap.GetValueOrDefault(v.Object.Key, v.Object.Key)));
        }
        return Mark(headResult, existing, baseRef, baseCommit);
    }

    internal static LintResult Mark(LintResult head, ISet<(string Rule, string Key)> existing, string baseRef, string baseCommit)
    {
        var violations = head.Violations
            .Select(v => v with { IsNew = !existing.Contains((v.RuleId, v.Object.Key)) })
            .OrderByDescending(v => v.IsNew == true)
            .ThenByDescending(v => v.Severity)
            .ThenBy(v => v.RuleId, StringComparer.Ordinal)
            .ThenBy(v => v.Object.Key, StringComparer.Ordinal)
            .ToList();
        var newCount = violations.Count(v => v.IsNew == true);
        return head with
        {
            Violations = violations,
            Summary = head.Summary with { New = newCount, Existing = violations.Count - newCount },
            Blocking = head.FailOn != FailOn.None && violations.Any(v => v.IsNew == true && (int)v.Severity >= (int)head.FailOn),
            BaseRef = baseRef,
            BaseCommit = baseCommit,
        };
    }
}
