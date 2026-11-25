using System.Security.Cryptography.X509Certificates;
using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Common.Security;

namespace Sfx.Core.Auth;

public sealed class CertificateAuth(X509Certificate2 certificate, ServerIdentity server) : IClusterAuth
{
    public X509Certificate2 Certificate => certificate;

    public ServerIdentity Server => server;

    public ServiceFabricClientBuilder Configure(ServiceFabricClientBuilder builder) =>
        builder.UseX509Security(_ => Task.FromResult<SecuritySettings>(new X509SecuritySettings(certificate, server.ToRemoteSettings())));
}

public sealed class CertificateAuthProvider : IClusterAuthProvider
{
    public string Name => "cert";

    public string Description => "X.509 client certificate";

    public bool RequiresTls => true;

    public IReadOnlyList<AuthOptionInfo> Options { get; } =
    [
        new("cert-thumbprint", "client certificate thumbprint, looked up in the certificate store"),
        new("cert-store", "CurrentUser (default) or LocalMachine"),
        new("cert-file", "client certificate from a .pfx or .pem file"),
        .. ServerIdentity.Options,
    ];

    public IClusterAuth Create(AuthContext context)
    {
        var options = context.Options;
        var thumbprint = options.Get("cert-thumbprint");
        var file = options.Get("cert-file");

        var certificate = (thumbprint, file) switch
        {
            (not null, null) => CertificateLoader.FromStore(thumbprint, ParseStore(options.Get("cert-store"))),
            (null, not null) => CertificateLoader.FromFile(file),
            _ => throw new AuthConfigurationException("Pass exactly one of --cert-thumbprint or --cert-file."),
        };

        return new CertificateAuth(certificate, ServerIdentity.FromOptions(options));
    }

    private static StoreLocation ParseStore(string? value) =>
        value is null ? StoreLocation.CurrentUser
        : Enum.TryParse<StoreLocation>(value, ignoreCase: true, out var location) ? location
        : throw new AuthConfigurationException($"Unknown certificate store '{value}'.");
}
