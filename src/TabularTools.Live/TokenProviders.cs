using Azure.Core;
using Azure.Identity;
using TabularTools.Core.Queries;

namespace TabularTools.Live;

/// <summary>Gets access tokens for XMLA endpoints. Tokens are never logged or stored by TabularTools.</summary>
public interface ITokenProvider
{
    Task<AccessToken> GetTokenAsync(string scope, CancellationToken cancellationToken);
}

/// <summary>Builds the token provider for a profile's auth mode (ARCHITECTURE §13.2).</summary>
public static class TokenProviders
{
    public static ITokenProvider For(ResolvedProfile profile, Func<string, string?>? environment = null, bool interactiveAllowed = true)
    {
        ArgumentNullException.ThrowIfNull(profile);
        environment ??= Environment.GetEnvironmentVariable;
        return profile.Auth switch
        {
            AuthMode.Token => new EnvironmentTokenProvider(profile.TokenEnv!, environment),
            AuthMode.None => throw new ArgumentException($"Profile '{profile.Name}' uses auth 'none', which only works with local engines."),
            _ => new CredentialTokenProvider(Credential(profile.Auth, environment, interactiveAllowed), profile.Auth),
        };
    }

    /// <summary>The Azure.Identity credential for a mode. The default chain never prompts in CI or when prompts aren't allowed.</summary>
    public static TokenCredential Credential(AuthMode mode, Func<string, string?> environment, bool interactiveAllowed)
    {
        return mode switch
        {
            AuthMode.AzureCli => new AzureCliCredential(),
            AuthMode.ServicePrincipal => new EnvironmentCredential(),
            AuthMode.Interactive => new InteractiveBrowserCredential(),
            AuthMode.DeviceCode => new DeviceCodeCredential(new DeviceCodeCredentialOptions
            {
                // stdout may carry the MCP protocol, so the sign-in instructions go to stderr.
                DeviceCodeCallback = (code, _) => { Console.Error.WriteLine(code.Message); return Task.CompletedTask; },
            }),
            _ => DefaultChain(environment, interactiveAllowed),
        };
    }

    private static ChainedTokenCredential DefaultChain(Func<string, string?> environment, bool interactiveAllowed)
    {
        var chain = new List<TokenCredential> { new EnvironmentCredential(), new WorkloadIdentityCredential(), new AzureCliCredential() };
        if (interactiveAllowed && OperatingSystem.IsWindows() && !IsCi(environment)) chain.Add(new InteractiveBrowserCredential());
        return new ChainedTokenCredential([.. chain]);
    }

    /// <summary>True on a build agent (Azure Pipelines, GitHub Actions, or anything setting CI), where nobody can answer a prompt.</summary>
    public static bool IsCi(Func<string, string?> environment) =>
        !string.IsNullOrEmpty(environment("TF_BUILD")) || !string.IsNullOrEmpty(environment("GITHUB_ACTIONS")) || !string.IsNullOrEmpty(environment("CI"));
}

internal sealed class CredentialTokenProvider(TokenCredential credential, AuthMode mode) : ITokenProvider
{
    public async Task<AccessToken> GetTokenAsync(string scope, CancellationToken cancellationToken)
    {
        try
        {
            return await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken).ConfigureAwait(false);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new LiveException($"Couldn't get a token ({mode}): {FirstLine(ex.Message)}", Hint(mode), ex);
        }
    }

    private static string Hint(AuthMode mode) => mode switch
    {
        AuthMode.AzureCli => "Run 'az login' (or use the AzureCLI@2 task in Azure Pipelines).",
        AuthMode.ServicePrincipal => "Set AZURE_TENANT_ID, AZURE_CLIENT_ID and AZURE_CLIENT_SECRET (or AZURE_CLIENT_CERTIFICATE_PATH).",
        AuthMode.DeviceCode or AuthMode.Interactive => "Complete the sign-in, or pick another auth mode for this profile.",
        _ => "Sign in with 'az login', or set AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET, or choose an auth mode in the profile.",
    };

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();
}

internal sealed class EnvironmentTokenProvider(string variable, Func<string, string?> environment) : ITokenProvider
{
    public Task<AccessToken> GetTokenAsync(string scope, CancellationToken cancellationToken)
    {
        var token = environment(variable);
        if (string.IsNullOrWhiteSpace(token))
            throw new LiveException($"The environment variable {variable} is empty.", "Set it to an access token for the Power BI API before running.");
        // The expiry isn't known; ADOMD asks again through OnAccessTokenExpired if the service rejects it.
        return Task.FromResult(new AccessToken(token.Trim(), DateTimeOffset.UtcNow.AddMinutes(50)));
    }
}
