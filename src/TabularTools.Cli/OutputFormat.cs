using System.CommandLine;

namespace TabularTools.Cli;

public enum OutputFormat { Text, Markdown, Json }

/// <summary>Options every command shares: --format and --out.</summary>
internal sealed class OutputOptions
{
    public Option<OutputFormat?> Format { get; } = new("--format", "-f")
    {
        Description = "Output format: text, markdown or json. Default: text in a terminal, json when output is redirected.",
    };

    public Option<FileInfo?> Out { get; } = new("--out", "-o")
    {
        Description = "Write the output to this file instead of standard output.",
    };

    public void AddTo(Command command)
    {
        command.Options.Add(Format);
        command.Options.Add(Out);
    }

    public OutputFormat ResolveFormat(ParseResult parse, CliContext context) =>
        parse.GetValue(Format) ?? (context.OutputIsTerminal ? OutputFormat.Text : OutputFormat.Json);

    public void Write(ParseResult parse, CliContext context, string content)
    {
        var file = parse.GetValue(Out);
        if (file is null)
        {
            context.Out.Write(content);
            if (!content.EndsWith('\n')) context.Out.WriteLine();
            return;
        }
        if (file.Directory is { Exists: false } dir) dir.Create();
        File.WriteAllText(file.FullName, content);
        context.Error.WriteLine($"Wrote {file.FullName}");
    }
}

/// <summary>Where a command writes, so tests can capture it.</summary>
public sealed record CliContext(TextWriter Out, TextWriter Error, bool OutputIsTerminal, string WorkingDirectory)
{
    public static CliContext Console() => new(System.Console.Out, System.Console.Error, !System.Console.IsOutputRedirected, Directory.GetCurrentDirectory());

    /// <summary>Environment variables (replaceable in tests).</summary>
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>Creates the PR commenter; tests substitute a fake.</summary>
    public Func<Ado.AdoContext, Ado.IPullRequestCommenter>? CommenterFactory { get; init; }
}
