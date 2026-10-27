using System.IO;
using System.Security.Cryptography;

namespace BarkCloud.Builder;

internal static class JwtKeyFileWriter
{
    internal static void WriteJwtKeys(string dir)
    {
        var keysDir = Path.Combine(dir, "secrets", "jwt");
        Directory.CreateDirectory(keysDir);

        var privateKey = Path.Combine(keysDir, "jwt-private.pem");
        var publicKey = Path.Combine(keysDir, "jwt-public.pem");
        var hasPrivateKey = File.Exists(privateKey);
        var hasPublicKey = File.Exists(publicKey);

        if (!hasPrivateKey && hasPublicKey)
            throw new InvalidOperationException(
                $"{publicKey} exists without {privateKey}; restore the matching private key instead of generating a new pair.");

        if (!hasPrivateKey)
        {
            using var generated = RSA.Create(3072);
            File.WriteAllText(privateKey, generated.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(publicKey, generated.ExportSubjectPublicKeyInfoPem());
            return;
        }

        using var privateRsa = RSA.Create();
        privateRsa.ImportFromPem(File.ReadAllText(privateKey));
        RSAParameters privateParameters;
        try
        {
            privateParameters = privateRsa.ExportParameters(includePrivateParameters: true);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"{privateKey} does not contain an RSA private key; neither key file was overwritten.", ex);
        }
        if (privateParameters.D is null || privateRsa.KeySize < 3072)
            throw new InvalidOperationException(
                $"{privateKey} must contain a private RSA key of at least 3072 bits; neither key file was overwritten.");

        var privatePublicKey = privateRsa.ExportSubjectPublicKeyInfo();

        if (!hasPublicKey)
        {
            File.WriteAllText(publicKey, privateRsa.ExportSubjectPublicKeyInfoPem());
            return;
        }

        using var publicRsa = RSA.Create();
        publicRsa.ImportFromPem(File.ReadAllText(publicKey));
        if (!privatePublicKey.AsSpan().SequenceEqual(publicRsa.ExportSubjectPublicKeyInfo()))
            throw new InvalidOperationException(
                $"{privateKey} and {publicKey} do not form a matching JWT key pair; neither file was overwritten.");
    }
}
