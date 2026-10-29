using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WhiteProxy.Core;

namespace WhiteProxy.Service;

internal static class WebControlCertificate
{
    internal static X509Certificate2 LoadOrCreate(string root)
    {
        var path = Path.Combine(root, "web-control.pfx");
        if (File.Exists(path))
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.MachineKeySet);
            if (existing.HasPrivateKey && existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(7)) return existing;
            existing.Dispose();
        }
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=WhiteProxy Web Control", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost"); names.AddDnsName(Dns.GetHostName());
        foreach (var address in ListenAddresses.CurrentIpv4()) names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(3));
        var temporary = path + ".tmp";
        try { File.WriteAllBytes(temporary, certificate.Export(X509ContentType.Pfx)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        // WindowsのSchannelは永続キーコンテナーを使う。EphemeralKeySetではTLSハンドシェイクが失敗する。
        return X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.MachineKeySet);
    }
}
