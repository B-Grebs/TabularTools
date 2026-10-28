using TabularTools.Core.Queries;

namespace TabularTools.Live;

/// <summary>Creates live executors for the hosts. Recorded profiles are handled by Core before this is called.</summary>
public sealed class DaxExecutorFactory(Func<string, string?>? environment = null, bool interactiveAllowed = true) : IDaxExecutorFactory
{
    public IDaxExecutor Create(ResolvedProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.IsRecorded) return new RecordedDaxExecutor(profile.RecordingsPath!, profile.Database);
        var endpoint = EndpointPolicy.Parse(profile.Endpoint) ?? throw new ArgumentException($"Profile '{profile.Name}': endpoint '{profile.Endpoint}' isn't valid.");
        var local = endpoint.Kind is EndpointKind.Localhost or EndpointKind.Desktop;
        var tokens = local || profile.Auth == AuthMode.None ? null : TokenProviders.For(profile, environment, interactiveAllowed);
        return new XmlaDaxExecutor(profile, tokens);
    }
}
