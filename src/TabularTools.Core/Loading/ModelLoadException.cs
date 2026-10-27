using TabularTools.Core.Diagnostics;

namespace TabularTools.Core.Loading;

/// <summary>Thrown when a path can't be resolved to a model, or the model definition is invalid.</summary>
public sealed class ModelLoadException : Exception
{
    public ModelLoadException(IReadOnlyList<Diagnostic> diagnostics, Exception? inner = null)
        : base(BuildMessage(diagnostics), inner)
    {
        Diagnostics = diagnostics;
    }

    public ModelLoadException(Diagnostic diagnostic) : this([diagnostic]) { }

    /// <summary>The problems that stopped the load. Always at least one.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>True when the path itself was the problem (not found, not a model), not the model's content.</summary>
    public bool IsResolutionFailure => Diagnostics.All(d => d.Code.StartsWith("LOAD", StringComparison.Ordinal));

    private static string BuildMessage(IReadOnlyList<Diagnostic> diagnostics) =>
        diagnostics.Count == 1
            ? diagnostics[0].Message
            : $"{diagnostics.Count} problems:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics.Select(d => "  " + d))}";
}
