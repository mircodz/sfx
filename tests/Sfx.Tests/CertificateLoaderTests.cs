using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Sfx.Core.Auth;

namespace Sfx.Tests;

public sealed class CertificateLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sfx-certs-").FullName;
    private readonly X509Certificate2 _certificate;

    public CertificateLoaderTests()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=sfx-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    public void Dispose()
    {
        _certificate.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Loads_a_pfx_with_its_private_key()
    {
        var path = Write("client.pfx", _certificate.Export(X509ContentType.Pkcs12));

        using var loaded = CertificateLoader.FromFile(path);

        Assert.Equal(_certificate.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void Loads_a_cer_without_a_private_key()
    {
        var path = Write("client.cer", _certificate.Export(X509ContentType.Cert));

        using var loaded = CertificateLoader.FromFile(path);

        Assert.Equal(_certificate.Thumbprint, loaded.Thumbprint);
        Assert.False(loaded.HasPrivateKey);
    }

    [Fact]
    public void Loads_a_pem_with_its_private_key()
    {
        var pem = _certificate.ExportCertificatePem() + "\n" + _certificate.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem();
        var path = Write("client.pem", System.Text.Encoding.ASCII.GetBytes(pem));

        using var loaded = CertificateLoader.FromFile(path);

        Assert.Equal(_certificate.Thumbprint, loaded.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void Rejects_a_missing_file()
    {
        Assert.Throws<AuthConfigurationException>(() => CertificateLoader.FromFile(Path.Combine(_dir, "missing.pfx")));
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
