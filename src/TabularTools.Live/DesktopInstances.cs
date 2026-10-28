namespace TabularTools.Live;

/// <summary>Finds the local Analysis Services port of a running Power BI Desktop (Windows only).</summary>
public static class DesktopInstances
{
    /// <summary>Ports from <c>%LOCALAPPDATA%\Microsoft\Power BI Desktop\AnalysisServicesWorkspaces\*\Data\msmdsrv.port.txt</c> (and the Store app's folder), newest first.</summary>
    public static IReadOnlyList<int> FindPorts()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(local, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces"),
            Path.Combine(user, "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces"),
        };
        return roots.Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "msmdsrv.port.txt", SearchOption.AllDirectories))
            .Select(f => (File: f, Time: File.GetLastWriteTimeUtc(f)))
            .OrderByDescending(x => x.Time)
            .Select(x => ParsePort(File.ReadAllBytes(x.File)))
            .Where(p => p is not null)
            .Select(p => p!.Value)
            .Distinct()
            .ToList();
    }

    /// <summary>The file is UTF-16 (sometimes UTF-8) digits.</summary>
    internal static int? ParsePort(byte[] bytes)
    {
        var digits = new string(bytes.Where(b => b is >= (byte)'0' and <= (byte)'9').Select(b => (char)b).ToArray());
        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536 ? port : null;
    }
}
