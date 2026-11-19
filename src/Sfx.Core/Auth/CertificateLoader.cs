using System.Security.Cryptography.X509Certificates;

namespace Sfx.Core.Auth;

public static class CertificateLoader
{
    public static X509Certificate2 FromStore(string thumbprint, StoreLocation location, string storeName = "My")
    {
        using var store = new X509Store(storeName, location);
        store.Open(OpenFlags.ReadOnly);
        // validOnly: false because cluster client certs are often self-signed.
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, NormalizeThumbprint(thumbprint), validOnly: false);
        return matches.Count > 0
            ? matches[0]
            : throw new AuthConfigurationException($"Certificate {thumbprint} not found in {location}/{storeName}.");
    }

    public static X509Certificate2 FromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new AuthConfigurationException($"Certificate file '{path}' not found.");
        }

        if (!Path.GetExtension(path).Equals(".pem", StringComparison.OrdinalIgnoreCase))
        {
            // A .pfx carries the private key; anything else (.cer, .crt) is just a certificate.
            return X509Certificate2.GetCertContentType(path) == X509ContentType.Pkcs12
                ? X509CertificateLoader.LoadPkcs12FromFile(path, password: null)
                : X509CertificateLoader.LoadCertificateFromFile(path);
        }

        var pem = X509Certificate2.CreateFromPemFile(path);
        if (!OperatingSystem.IsWindows())
        {
            return pem;
        }

        // SChannel on Windows can't use the ephemeral key a PEM load produces; a PKCS#12 round-trip persists it.
        using (pem)
        {
            return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null);
        }
    }

    public static string NormalizeThumbprint(string thumbprint) =>
        string.Concat(thumbprint.Where(Uri.IsHexDigit)).ToUpperInvariant();
}
