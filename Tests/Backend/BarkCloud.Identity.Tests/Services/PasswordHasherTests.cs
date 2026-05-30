using BarkCloud.Identity.Services;

namespace BarkCloud.Identity.Tests.Services;

public class PasswordHasherTests
{
    [Fact]
    public void HashPassword_ReturnsBCryptHashWithVersionPrefix()
    {
        var hash = PasswordHasher.HashPassword("secret");

        hash.Should().StartWith("$2");
        hash.Length.Should().BeGreaterThan(50);
    }

    [Fact]
    public void HashPassword_SameInputProducesDifferentHashes()
    {
        var h1 = PasswordHasher.HashPassword("secret");
        var h2 = PasswordHasher.HashPassword("secret");

        h1.Should().NotBe(h2);
    }

    [Fact]
    public void VerifyPassword_ValidBCryptHash_ReturnsTrue()
    {
        var hash = PasswordHasher.HashPassword("MyPa$$w0rd");

        PasswordHasher.VerifyPassword("MyPa$$w0rd", hash).Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_WrongPassword_ReturnsFalse()
    {
        var hash = PasswordHasher.HashPassword("MyPa$$w0rd");

        PasswordHasher.VerifyPassword("wrong", hash).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void VerifyPassword_NullOrEmptyStoredHash_ReturnsFalse(string? storedHash)
    {
        PasswordHasher.VerifyPassword("any", storedHash).Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_LegacySha256Hash_ReturnsTrueForMatch()
    {
        // Legacy hash: Base64(SHA256(password)).
        var password = "old-password";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password));
        var legacyHash = Convert.ToBase64String(bytes);

        PasswordHasher.VerifyPassword(password, legacyHash).Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_LegacySha256Hash_ReturnsFalseForMismatch()
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("foo"));
        var legacyHash = Convert.ToBase64String(bytes);

        PasswordHasher.VerifyPassword("bar", legacyHash).Should().BeFalse();
    }

    [Fact]
    public void VerifyPassword_LongPassword_RoundTrips()
    {
        var password = new string('a', 100);
        var hash = PasswordHasher.HashPassword(password);

        PasswordHasher.VerifyPassword(password, hash).Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_MalformedNonBCryptHash_ReturnsFalseWithoutThrowing()
    {
        // Не-BCrypt строка идёт по legacy-ветке и сравнивается побайтово как есть —
        // мусор в storedHash должен давать false, а не исключение.
        PasswordHasher.VerifyPassword("any", "not-a-real-hash").Should().BeFalse();
    }
}
