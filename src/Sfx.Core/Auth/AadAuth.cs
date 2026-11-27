using Microsoft.Identity.Client;
using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Security;

namespace Sfx.Core.Auth;

/// <summary>
/// Microsoft Entra ID (Azure AD) sign-in using the device code flow, which needs no browser redirect. Not
/// <paramref name="interactive"/>, it only signs in silently.
/// </summary>
public sealed class AadAuth(ServerIdentity server, Func<string, Task> notify, bool interactive = true) : IClusterAuth
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IPublicClientApplication? _app;

    public ServiceFabricClientBuilder Configure(ServiceFabricClientBuilder builder) =>
        builder.UseAzureActiveDirectorySecurity(_ => Task.FromResult<SecuritySettings>(
            new AzureActiveDirectorySecuritySettings(GetTokenAsync, server.ToRemoteSettings())));

    internal async Task<string> GetTokenAsync(AadMetadata metadata, CancellationToken cancellationToken)
    {
        // Serialized so concurrent requests don't each start a device code prompt.
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _app ??= PublicClientApplicationBuilder.Create(metadata.Client).WithAuthority(metadata.Authority).Build();
            string[] scopes = [$"{metadata.Cluster}/.default"];

            var account = (await _app.GetAccountsAsync()).FirstOrDefault();
            if (account is not null)
            {
                try
                {
                    return (await _app.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellationToken)).AccessToken;
                }
                catch (MsalUiRequiredException)
                {
                }
            }
            if (!interactive)
            {
                throw new SignInNeedsPersonException("device code");
            }

            var result = await _app
                .AcquireTokenWithDeviceCode(scopes, code => notify(code.Message))
                .ExecuteAsync(cancellationToken);
            return result.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }
}

public sealed class AadAuthProvider : IClusterAuthProvider
{
    public string Name => "aad";

    public string Description => "Microsoft Entra ID (Azure AD), device code sign-in";

    public bool RequiresTls => true;

    public IReadOnlyList<AuthOptionInfo> Options => ServerIdentity.Options;

    public IClusterAuth Create(AuthContext context) =>
        new AadAuth(ServerIdentity.FromOptions(context.Options), context.Notify, context.Interactive);
}
