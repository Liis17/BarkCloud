using System.Security.Cryptography;
using BarkCloud.Builder;

namespace BarkCloud.Builder.Tests;

public sealed class JwtKeyFileWriterTests
{
    [Fact]
    public void WriteJwtKeys_RejectsPublicPemAtPrivatePathWithoutOverwritingIt()
    {
        WithTempDirectory(dir =>
        {
            var keysDir = Path.Combine(dir, "secrets", "jwt");
            Directory.CreateDirectory(keysDir);
            var privatePath = Path.Combine(keysDir, "jwt-private.pem");
            using var rsa = RSA.Create(3072);
            var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
            File.WriteAllText(privatePath, publicPem);

            Assert.Throws<InvalidOperationException>(() => JwtKeyFileWriter.WriteJwtKeys(dir));

            Assert.Equal(publicPem, File.ReadAllText(privatePath));
            Assert.False(File.Exists(Path.Combine(keysDir, "jwt-public.pem")));
        });
    }

    [Fact]
    public void WriteJwtKeys_RejectsSmallPrivateKeysWithoutOverwritingEitherFile()
    {
        WithTempDirectory(dir =>
        {
            var keysDir = Path.Combine(dir, "secrets", "jwt");
            Directory.CreateDirectory(keysDir);
            var privatePath = Path.Combine(keysDir, "jwt-private.pem");
            var publicPath = Path.Combine(keysDir, "jwt-public.pem");
            using var rsa = RSA.Create(2048);
            var privatePem = rsa.ExportPkcs8PrivateKeyPem();
            var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
            File.WriteAllText(privatePath, privatePem);
            File.WriteAllText(publicPath, publicPem);

            Assert.Throws<InvalidOperationException>(() => JwtKeyFileWriter.WriteJwtKeys(dir));

            Assert.Equal(privatePem, File.ReadAllText(privatePath));
            Assert.Equal(publicPem, File.ReadAllText(publicPath));
        });
    }

    [Fact]
    public void WriteJwtKeys_DerivesMissingPublicKeyFromExistingPrivateKey()
    {
        WithTempDirectory(dir =>
        {
            JwtKeyFileWriter.WriteJwtKeys(dir);
            var privatePath = Path.Combine(dir, "secrets", "jwt", "jwt-private.pem");
            var publicPath = Path.Combine(dir, "secrets", "jwt", "jwt-public.pem");
            var privateKeyBefore = File.ReadAllBytes(privatePath);
            File.Delete(publicPath);

            JwtKeyFileWriter.WriteJwtKeys(dir);

            Assert.Equal(privateKeyBefore, File.ReadAllBytes(privatePath));
            using var privateRsa = RSA.Create();
            using var publicRsa = RSA.Create();
            privateRsa.ImportFromPem(File.ReadAllText(privatePath));
            publicRsa.ImportFromPem(File.ReadAllText(publicPath));
            Assert.Equal(privateRsa.ExportSubjectPublicKeyInfo(), publicRsa.ExportSubjectPublicKeyInfo());
        });
    }

    [Fact]
    public void WriteJwtKeys_RejectsPublicKeyWithoutPrivateKeyWithoutOverwritingIt()
    {
        WithTempDirectory(dir =>
        {
            var keysDir = Path.Combine(dir, "secrets", "jwt");
            Directory.CreateDirectory(keysDir);
            var publicPath = Path.Combine(keysDir, "jwt-public.pem");
            using var rsa = RSA.Create(3072);
            var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
            File.WriteAllText(publicPath, publicKey);

            Assert.Throws<InvalidOperationException>(() => JwtKeyFileWriter.WriteJwtKeys(dir));

            Assert.Equal(publicKey, File.ReadAllText(publicPath));
            Assert.False(File.Exists(Path.Combine(keysDir, "jwt-private.pem")));
        });
    }

    [Fact]
    public void WriteJwtKeys_RejectsMismatchedExistingKeysWithoutOverwritingThem()
    {
        WithTempDirectory(dir =>
        {
            JwtKeyFileWriter.WriteJwtKeys(dir);
            var privatePath = Path.Combine(dir, "secrets", "jwt", "jwt-private.pem");
            var publicPath = Path.Combine(dir, "secrets", "jwt", "jwt-public.pem");
            using var otherRsa = RSA.Create(3072);
            var mismatchedPublicKey = otherRsa.ExportSubjectPublicKeyInfoPem();
            File.WriteAllText(publicPath, mismatchedPublicKey);
            var privateKey = File.ReadAllText(privatePath);

            Assert.Throws<InvalidOperationException>(() => JwtKeyFileWriter.WriteJwtKeys(dir));

            Assert.Equal(privateKey, File.ReadAllText(privatePath));
            Assert.Equal(mismatchedPublicKey, File.ReadAllText(publicPath));
        });
    }

    private static void WithTempDirectory(Action<string> action)
    {
        var dir = Path.Combine(Path.GetTempPath(), "barkcloud-jwt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            action(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
