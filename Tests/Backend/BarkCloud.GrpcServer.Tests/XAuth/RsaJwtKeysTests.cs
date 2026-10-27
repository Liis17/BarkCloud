using BarkCloud.Shared.Identity;

using System.Security.Cryptography;

namespace BarkCloud.GrpcServer.Tests.XAuth;

public class RsaJwtKeysTests
{
    private static string WritePem(string name, string pem)
    {
        var path = Path.Combine(Path.GetTempPath(), $"barkcloud-rsajwtkeys-tests-{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, pem);
        return path;
    }

    [Fact]
    public void ComputeKid_IsStablePrefixOfSha256()
    {
        using var rsa = RSA.Create(3072);

        var kid = RsaJwtKeys.ComputeKid(rsa);

        kid.Should().Be(RsaJwtKeys.ComputeKid(rsa));
        kid.Length.Should().Be(16);
        kid.Should().MatchRegex("^[0-9a-f]{16}$");
    }

    [Fact]
    public void LoadSigningKey_RoundTripsAndSetsKid()
    {
        using var rsa = RSA.Create(3072);
        var path = WritePem("private.pem", rsa.ExportPkcs8PrivateKeyPem());

        var key = RsaJwtKeys.LoadSigningKey(path);

        key.KeyId.Should().Be(RsaJwtKeys.ComputeKid(rsa));
        key.Rsa.KeySize.Should().Be(3072);
    }

    [Fact]
    public void LoadSigningKey_RejectsKeyBelow3072Bits()
    {
        using var rsa = RSA.Create(2048);
        var path = WritePem("private-weak.pem", rsa.ExportPkcs8PrivateKeyPem());

        var act = () => RsaJwtKeys.LoadSigningKey(path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*3072*");
    }

    [Fact]
    public void LoadSigningKey_RejectsPublicKeyPem()
    {
        using var rsa = RSA.Create(3072);
        var path = WritePem("public-as-private.pem", rsa.ExportSubjectPublicKeyInfoPem());

        var act = () => RsaJwtKeys.LoadSigningKey(path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*PRIVATE KEY*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LoadSigningKey_EmptyValue_FailsWithConfigHint(string? value)
    {
        var act = () => RsaJwtKeys.LoadSigningKey(value);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{RsaJwtKeys.PrivateKeyConfigKey}*{RsaJwtKeys.PrivateKeyEnvName}*");
    }

    [Fact]
    public void LoadPublicKeyMap_LoadsMultipleKeysForRotation()
    {
        using var first = RSA.Create(3072);
        using var second = RSA.Create(3072);
        var firstPath = WritePem("public-1.pem", first.ExportSubjectPublicKeyInfoPem());
        var secondPath = WritePem("public-2.pem", second.ExportSubjectPublicKeyInfoPem());

        var map = RsaJwtKeys.LoadPublicKeyMap($"{firstPath};  {secondPath}");

        map.Keys.Should().BeEquivalentTo(
        [
            RsaJwtKeys.ComputeKid(first),
            RsaJwtKeys.ComputeKid(second)
        ]);
    }

    [Fact]
    public void LoadPublicKeyMap_RejectsDuplicateKeyFiles()
    {
        using var rsa = RSA.Create(3072);
        var path = WritePem("public-dup.pem", rsa.ExportSubjectPublicKeyInfoPem());

        var act = () => RsaJwtKeys.LoadPublicKeyMap($"{path};{path}");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Дубликат*");
    }

    [Fact]
    public void LoadPublicKeyMap_RejectsKeyBelow2048Bits()
    {
        using var rsa = RSA.Create(1024);
        var path = WritePem("public-weak.pem", rsa.ExportSubjectPublicKeyInfoPem());

        var act = () => RsaJwtKeys.LoadPublicKeyMap(path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*2048*");
    }

    [Fact]
    public void LoadPublicKeyMap_UnreadableFile_FailsWithConfigHint()
    {
        var act = () => RsaJwtKeys.LoadPublicKeyMap("/nonexistent/jwt-public.pem");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{RsaJwtKeys.PublicKeyConfigKey}*{RsaJwtKeys.PublicKeyEnvName}*");
    }

    [Fact]
    public void LoadPublicKeyMap_PrivateKeyPem_Rejected()
    {
        using var rsa = RSA.Create(3072);
        var path = WritePem("private-as-public.pem", rsa.ExportPkcs8PrivateKeyPem());

        var act = () => RsaJwtKeys.LoadPublicKeyMap(path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*PUBLIC KEY*");
    }
}
