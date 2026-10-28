using TabularTools.Core.Loading;

namespace TabularTools.Cli.Commands;

/// <summary>Maps expected failures to exit codes and readable messages.</summary>
internal static class CommandRunner
{
    public static int Run(CliContext context, Func<int> body)
    {
        try
        {
            return body();
        }
        catch (ModelLoadException ex)
        {
            foreach (var d in ex.Diagnostics) context.Error.WriteLine(d.ToString());
            return ex.IsResolutionFailure ? ExitCodes.UsageError : ExitCodes.ModelError;
        }
        catch (TabularTools.Core.Git.GitException ex)
        {
            context.Error.WriteLine("error: " + ex.Message);
            return ExitCodes.EnvironmentError;
        }
        catch (ArgumentException ex)
        {
            context.Error.WriteLine("error: " + ex.Message);
            return ExitCodes.UsageError;
        }
        catch (IOException ex)
        {
            context.Error.WriteLine("error: " + ex.Message);
            return ExitCodes.EnvironmentError;
        }
    }
}
