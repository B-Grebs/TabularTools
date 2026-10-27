namespace TabularTools.Mcp;

/// <summary>Command-line options for the MCP server.</summary>
public sealed record ServerOptions
{
    public IReadOnlyList<string> Roots { get; init; } = [Directory.GetCurrentDirectory()];

    /// <summary>Register tools that write files. Off by default (read-only server).</summary>
    public bool AllowWrites { get; init; }

    /// <summary>Hide tools that talk to a live semantic model.</summary>
    public bool DisableLive { get; init; }

    public bool ShowHelp { get; init; }

    public bool ShowVersion { get; init; }

    public const string Usage = """
        Usage: tabulartools-mcp [options]

        Runs the TabularTools MCP server over stdio. Logs go to stderr.

        Options:
          --root <dir>      Folder that relative model paths resolve against (repeatable; default: current folder).
          --allow-writes    Register tools that write files (off by default: read-only).
          --disable-live    Hide tools that query a live semantic model.
          --version         Print the version and exit.
          -h, --help        Show this help and exit.
        """;

    public static ServerOptions Parse(IReadOnlyList<string> args)
    {
        var roots = new List<string>();
        var options = new ServerOptions();
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--root":
                    if (i + 1 >= args.Count) throw new ArgumentException("--root needs a folder.");
                    var root = Path.GetFullPath(args[++i]);
                    if (!Directory.Exists(root)) throw new ArgumentException($"--root folder not found: {root}");
                    roots.Add(root);
                    break;
                case "--allow-writes": options = options with { AllowWrites = true }; break;
                case "--disable-live": options = options with { DisableLive = true }; break;
                case "--version": options = options with { ShowVersion = true }; break;
                case "-h" or "--help": options = options with { ShowHelp = true }; break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        return roots.Count == 0 ? options : options with { Roots = roots };
    }
}
