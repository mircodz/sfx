using Microsoft.ServiceFabric.Common.Security;

namespace Sfx.Core.Auth;

/// <summary>How to recognise the cluster's server certificate: by thumbprint, or by common name.</summary>
public sealed record ServerIdentity(IReadOnlyList<string> Thumbprints, IReadOnlyList<string> CommonNames)
{
    public static readonly IReadOnlyList<AuthOptionInfo> Options =
    [
        new("server-thumbprint", "expected cluster certificate thumbprint (repeatable)"),
        new("server-cn", "expected cluster certificate common name (repeatable)"),
    ];

    public static ServerIdentity FromOptions(AuthOptions options)
    {
        var identity = new ServerIdentity(options.GetAll("server-thumbprint"), options.GetAll("server-cn"));
        return identity.Thumbprints.Count == 0 && identity.CommonNames.Count == 0
            ? throw new AuthConfigurationException("--server-thumbprint or --server-cn is required.")
            : identity;
    }

    public RemoteX509SecuritySettings ToRemoteSettings() =>
        Thumbprints.Count > 0
            ? new RemoteX509SecuritySettings(Thumbprints.Select(CertificateLoader.NormalizeThumbprint).ToList(), false)
            : new RemoteX509SecuritySettings(CommonNames.Select(name => new X509Name(name, null)).ToList(), false);
}
