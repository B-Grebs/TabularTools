using System.Reflection;

namespace TabularTools.Core;

/// <summary>Version information reported by both hosts.</summary>
public static class ProductInfo
{
    public const string Name = "TabularTools";

    public static string Version { get; } =
        typeof(ProductInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ProductInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>Version of the Analysis Services (TOM) library used to read models.</summary>
    public static string TomVersion { get; } =
        typeof(Microsoft.AnalysisServices.Tabular.Database).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
        ?? typeof(Microsoft.AnalysisServices.Tabular.Database).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
