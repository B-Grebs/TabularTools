using System.Security.Cryptography;
using System.Text;
using TabularTools.Core.Diagnostics;

namespace TabularTools.Core.Loading;

/// <summary>
/// Opens models by path or handle and caches them. A cached model is reused only while its files are
/// unchanged (same content hash), so edits made by other tools are picked up on the next call.
/// </summary>
public sealed class ModelWorkspace
{
    public const string HandlePrefix = "m-";

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _handleToInput = new(StringComparer.Ordinal);
    private readonly LinkedList<(string DefinitionPath, LoadedModel Model)> _cache = new();
    private readonly int _capacity;

    public ModelWorkspace(IEnumerable<string>? roots = null, int capacity = 16)
    {
        Roots = (roots ?? [Directory.GetCurrentDirectory()]).Select(Path.GetFullPath).ToArray();
        if (Roots.Count == 0) Roots = [Directory.GetCurrentDirectory()];
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>Folders relative paths are resolved against (the first one) and that writes are confined to.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>Opens a model from a path (absolute, or relative to the first root) or a handle from an earlier call.</summary>
    public OpenedModel Open(string? pathOrHandle)
    {
        var input = ResolveInput(pathOrHandle);
        var source = ModelLocator.Resolve(input);
        var hash = ModelLoader.ComputeContentHash(source);
        var handle = HandleFor(source.DefinitionPath);

        lock (_gate)
        {
            _handleToInput[handle] = source.InputPath;
            var node = _cache.First;
            while (node is not null)
            {
                if (string.Equals(node.Value.DefinitionPath, source.DefinitionPath, StringComparison.Ordinal))
                {
                    if (node.Value.Model.ContentHash == hash)
                    {
                        _cache.Remove(node);
                        _cache.AddFirst(node);
                        return new OpenedModel(handle, node.Value.Model, FromCache: true);
                    }
                    _cache.Remove(node);
                    break;
                }
                node = node.Next;
            }
        }

        var loaded = ModelLoader.Load(source);
        lock (_gate)
        {
            _cache.AddFirst((source.DefinitionPath, loaded));
            while (_cache.Count > _capacity) _cache.RemoveLast();
        }
        return new OpenedModel(handle, loaded, FromCache: false);
    }

    private string ResolveInput(string? pathOrHandle)
    {
        if (string.IsNullOrWhiteSpace(pathOrHandle)) return Roots[0];

        if (pathOrHandle.StartsWith(HandlePrefix, StringComparison.Ordinal) && !LooksLikePath(pathOrHandle))
        {
            lock (_gate)
            {
                if (_handleToInput.TryGetValue(pathOrHandle, out var known)) return known;
            }
            throw new ModelLoadException(new Diagnostic(DiagnosticLevel.Error, DiagnosticCodes.PathNotFound,
                $"Unknown model handle '{pathOrHandle}'. Handles last only for this session; pass the model path instead."));
        }

        return Path.IsPathRooted(pathOrHandle) ? pathOrHandle : Path.GetFullPath(Path.Combine(Roots[0], pathOrHandle));
    }

    private bool LooksLikePath(string value) =>
        value.IndexOfAny(['/', '\\', '.']) >= 0 || Directory.Exists(Path.Combine(Roots[0], value)) || File.Exists(Path.Combine(Roots[0], value));

    private static string HandleFor(string definitionPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(definitionPath));
        return HandlePrefix + Convert.ToHexStringLower(bytes)[..10];
    }
}

/// <summary>Result of <see cref="ModelWorkspace.Open"/>.</summary>
public sealed record OpenedModel(string Handle, LoadedModel Model, bool FromCache);
