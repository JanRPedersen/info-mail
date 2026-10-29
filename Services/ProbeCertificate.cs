using System.Security.Cryptography.X509Certificates;

namespace InfoMail.Services;

public static class ProbeCertificate
{
    public static X509Certificate2 Load(string? thumbprint)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("The local certificate probe requires Windows.");
        }

        if (string.IsNullOrWhiteSpace(thumbprint) || thumbprint.Length != 40 || !thumbprint.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("GraphProbe:CertificateThumbprint must contain 40 hexadecimal characters.");
        }

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        if (matches.Count != 1)
        {
            throw new InvalidOperationException("Exactly one matching certificate must exist in CurrentUser\\My.");
        }

        var certificate = matches[0];
        if (!certificate.HasPrivateKey || DateTime.Now < certificate.NotBefore || DateTime.Now >= certificate.NotAfter)
        {
            certificate.Dispose();
            throw new InvalidOperationException("The certificate must be current and have a private key.");
        }

        return certificate;
    }
}
