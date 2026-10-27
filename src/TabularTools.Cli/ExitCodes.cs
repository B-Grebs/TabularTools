namespace TabularTools.Cli;

/// <summary>Exit codes shared by every command (see docs/ARCHITECTURE.md §7).</summary>
public static class ExitCodes
{
    /// <summary>Success, no blocking findings.</summary>
    public const int Success = 0;
    /// <summary>Blocking findings at or above the --fail-on threshold.</summary>
    public const int BlockingFindings = 1;
    /// <summary>Usage or config error, including a path that isn't a model.</summary>
    public const int UsageError = 2;
    /// <summary>The model's TMDL/TMSL is invalid.</summary>
    public const int ModelError = 3;
    /// <summary>Environment problem: auth, network, git.</summary>
    public const int EnvironmentError = 4;
}
