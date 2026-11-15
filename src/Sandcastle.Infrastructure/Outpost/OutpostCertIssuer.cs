using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Sandcastle.Infrastructure.Outpost;

/// <summary>
/// SPEC-153 RF-003: issues a per-outpost self-signed client certificate
/// at register time. The SHA-256 fingerprint is persisted on the outpost
/// row — the connect endpoint validates the presented cert against it
/// (revocation = fingerprint row cleared on deregister).
/// </summary>
public static class OutpostCertIssuer
{
    /// <summary>One issued client cert (PEM pair + fingerprint).</summary>
    public sealed record IssuedCert(
        string CertPem, string KeyPem, string Fingerprint);

    /// <summary>Issues a self-signed RSA-3072 client cert for an outpost.</summary>
    public static IssuedCert Issue(Guid outpostId, int validityDays)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            $"CN=sandcastle-outpost-{outpostId:N}",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.2")], // clientAuth
                false));

        var now = DateTimeOffset.UtcNow;
        using var cert = request.CreateSelfSigned(
            now.AddDays(-1), now.AddDays(Math.Max(1, validityDays)));
        var fingerprint = Convert.ToHexStringLower(
            SHA256.HashData(cert.RawData));
        return new IssuedCert(
            cert.ExportCertificatePem(),
            rsa.ExportPkcs8PrivateKeyPem(),
            fingerprint);
    }

    /// <summary>SHA-256 fingerprint (hex) of a presented client cert.</summary>
    public static string FingerprintOf(X509Certificate2 cert) =>
        Convert.ToHexStringLower(SHA256.HashData(cert.RawData));
}
