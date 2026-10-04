using BarkCloud.Configuration.Catalog;
using BarkCloud.Shared.Identity;

namespace BarkCloud.Configuration.Tests.Infrastructure;

public class SettingsCatalogTests
{
    [Fact]
    public void Resolve_KnownSetting_ReturnsStorageMetadata()
    {
        var entry = SettingsCatalog.Resolve(ServiceId.Files, "TempFiles", "ExpiresAt");

        entry.StorageKey.Should().Be("TempFiles:ExpiresAt");
        entry.RestartTargets.Should().Equal("files");
        entry.IsSensitive.Should().BeFalse();
    }

    [Fact]
    public void Resolve_UnknownSetting_Throws()
    {
        var act = () => SettingsCatalog.Resolve(ServiceId.Files, "Unknown", "Value");

        act.Should().Throw<UnknownSettingException>();
    }

    [Fact]
    public void Resolve_GlobalKeyInServiceScope_ReturnsServiceOverrideMetadata()
    {
        var entry = SettingsCatalog.Resolve(ServiceId.Files, "JwtSettings", "Issuer");

        entry.StorageKey.Should().Be("JwtSettings:Issuer");
        entry.ServiceId.Should().Be(ServiceId.Files);
        entry.RestartTargets.Should().Equal("files");
    }

    [Fact]
    public void All_DoesNotExposeLegacyS3RowsOrReservedNamesAsScalarSettings()
    {
        SettingsCatalog.All.Should().NotContain(x => x.Section.StartsWith("S3Buckets:", StringComparison.Ordinal));
        SettingsCatalog.All.Should().NotContain(x => x.Section == "ReservedNames");
        SettingsCatalog.All.Should().Contain(x => x.ServiceId == ServiceId.Unknown
                                                  && x.Section == "Features"
                                                  && x.Key == "RegistrationEnabled");
        SettingsCatalog.All.Should().ContainSingle(x => x.ServiceId == ServiceId.Unknown
                                                        && x.Section == "Features"
                                                        && x.Key == "EmailEnabled"
                                                        && x.IsComputed
                                                        && x.IsEnvironmentManaged);
    }

    [Theory]
    [InlineData(" TRUE ", "true")]
    [InlineData("false", "false")]
    public void ValidateAndNormalize_Boolean_NormalizesValidValue(string input, string expected)
    {
        var entry = SettingsCatalog.Resolve(ServiceId.Unknown, "Features", "RegistrationEnabled");

        SettingsValueValidator.ValidateAndNormalize(entry, input).Should().Be(expected);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    public void ValidateAndNormalize_Boolean_RejectsInvalidValue(string input)
    {
        var entry = SettingsCatalog.Resolve(ServiceId.Unknown, "Features", "RegistrationEnabled");

        var act = () => SettingsValueValidator.ValidateAndNormalize(entry, input);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ValidateAndNormalize_Integer_UsesInvariantFormat()
    {
        var entry = SettingsCatalog.Resolve(ServiceId.Files, "TempFiles", "ExpiresAt");

        SettingsValueValidator.ValidateAndNormalize(entry, " 0042 ").Should().Be("42");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    public void ValidateAndNormalize_Port_RejectsValuesOutsideTcpRange(string input)
    {
        var entry = SettingsCatalog.Resolve(ServiceId.Notification, "Email", "Port");

        var act = () => SettingsValueValidator.ValidateAndNormalize(entry, input);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void ValidateAndNormalize_Duration_RejectsNonPositiveValues(string input)
    {
        var entry = SettingsCatalog.Resolve(ServiceId.Files, "TempFiles", "ExpiresAt");

        var act = () => SettingsValueValidator.ValidateAndNormalize(entry, input);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("short-secret")]
    [InlineData("жжжжжжжжжжжжжжж")] // 15 символов = 30 байт UTF-8
    public void ValidateAndNormalize_JwtSecret_RejectsKeyShorterThanRecommendedBytes(string input)
    {
        var entry = SettingsCatalog.Resolve(ServiceId.Unknown, "JwtSettings", "SecretKey");

        var act = () => SettingsValueValidator.ValidateAndNormalize(entry, input);

        act.Should().Throw<InvalidOperationException>().WithMessage("*JwtSettings:SecretKey*");
    }

    [Theory]
    [InlineData("supersecretkey_at_least_32_chars_long!!")]
    [InlineData("жжжжжжжжжжжжжжжж")] // 16 символов = 32 байта UTF-8
    public void ValidateAndNormalize_JwtSecret_AcceptsKeyOfRecommendedBytesIncludingUnicode(string input)
    {
        var entry = SettingsCatalog.Resolve(ServiceId.Unknown, "JwtSettings", "SecretKey");

        SettingsValueValidator.ValidateAndNormalize(entry, $" {input} ").Should().Be(input);
    }

    [Fact]
    public void ValidateAndNormalize_JwtSecret_EmptyMeansNotSet()
    {
        var entry = SettingsCatalog.Resolve(ServiceId.Unknown, "JwtSettings", "SecretKey");

        SettingsValueValidator.ValidateAndNormalize(entry, "  ").Should().BeEmpty();
    }

    [Fact]
    public void ValidateAndNormalize_OtherPassword_StillAcceptsShortValue()
    {
        var entry = SettingsCatalog.Resolve(ServiceId.Unknown, "RabbitMQ", "Password");

        SettingsValueValidator.ValidateAndNormalize(entry, "pw").Should().Be("pw");
    }

    [Fact]
    public void EmailSettings_ReportEveryStartupConsumerThatNeedsRestart()
    {
        var entry = SettingsCatalog.Resolve(ServiceId.Notification, "Email", "Host");

        entry.RestartTargets.Should().BeEquivalentTo("notification", "identity", "web");
    }

    [Fact]
    public void EmailAllowInsecure_IsBooleanAndRestartsOnlyNotification()
    {
        var entry = SettingsCatalog.Resolve(ServiceId.Notification, "Email", "AllowInsecure");

        entry.ValueKind.Should().Be(SettingValueKind.Boolean);
        entry.IsSensitive.Should().BeFalse();
        entry.IsEnvironmentManaged.Should().BeFalse();
        entry.RestartTargets.Should().BeEquivalentTo("notification");
        SettingsValueValidator.ValidateAndNormalize(entry, " TRUE ").Should().Be("true");
        var act = () => SettingsValueValidator.ValidateAndNormalize(entry, "yes");
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("ftp://example.com")]
    public void ValidateAndNormalize_Url_RejectsNonHttpUrl(string input)
    {
        var entry = SettingsCatalog.Resolve(ServiceId.Web, "FilesService", "Host");

        var act = () => SettingsValueValidator.ValidateAndNormalize(entry, input);

        act.Should().Throw<InvalidOperationException>();
    }
}
