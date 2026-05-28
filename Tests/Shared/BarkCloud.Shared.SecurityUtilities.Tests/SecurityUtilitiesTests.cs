namespace BarkCloud.Shared.SecurityUtilities.Tests;

public class SecurityUtilitiesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EvaluatePasswordStrength_NullOrEmpty_ReturnsZero(string? password)
    {
        var score = SecurityUtilities.EvaluatePasswordStrength(password!);

        score.Should().Be(0);
    }

    [Theory]
    [InlineData("abc", 0)]
    [InlineData("abcdefgh", 10)]
    [InlineData("abcdefghabcd", 20)]
    [InlineData("abcdefghabcdefgh", 30)]
    public void EvaluatePasswordStrength_LengthOnly_GivesExpectedLengthScore(string password, int expectedLengthScore)
    {
        // Только нижний регистр без цифр/спецсимволов, плюс длина.
        // 8+ символов одинаковых: длина + регистр (lower) + uniqueness bonus.
        var score = SecurityUtilities.EvaluatePasswordStrength(password);

        // длина-балл всегда минимум: expectedLengthScore.
        score.Should().BeGreaterThanOrEqualTo(expectedLengthScore);
    }

    [Fact]
    public void EvaluatePasswordStrength_AddingUpperCaseGivesTenExtraPoints()
    {
        var lower = SecurityUtilities.EvaluatePasswordStrength("abcdefghabcd");
        var mixed = SecurityUtilities.EvaluatePasswordStrength("AbcdefghAbcd");

        (mixed - lower).Should().Be(10);
    }

    [Fact]
    public void EvaluatePasswordStrength_AddingMoreDigitsGivesTenExtraPoints()
    {
        var withOneDigit = SecurityUtilities.EvaluatePasswordStrength("Abcdefgh1Abc");
        var withThreeDigits = SecurityUtilities.EvaluatePasswordStrength("Abcdefgh123A");

        (withThreeDigits - withOneDigit).Should().Be(10);
    }

    [Fact]
    public void EvaluatePasswordStrength_AddingMoreSpecialsGivesTenExtraPoints()
    {
        var oneSpecial = SecurityUtilities.EvaluatePasswordStrength("Abcdefgh!Abc");
        var twoSpecials = SecurityUtilities.EvaluatePasswordStrength("Abcdef!@hAbc");

        (twoSpecials - oneSpecial).Should().Be(10);
    }

    [Fact]
    public void EvaluatePasswordStrength_LowUniquenessGivesNoBonus()
    {
        // ratio = 1/12 ≈ 0.083, не превышает 0.4 → 0 баллов за уникальность.
        var score = SecurityUtilities.EvaluatePasswordStrength("aaaaaaaaaaaa");
        // длина 12 = 20; lower only = 10; нет цифр/спецов → ровно 30.
        score.Should().Be(30);
    }

    [Fact]
    public void EvaluatePasswordStrength_ScoreNeverExceeds100()
    {
        var score = SecurityUtilities.EvaluatePasswordStrength("Abcdef123!@#XyzW9$%^&");

        score.Should().BeLessThanOrEqualTo(100);
    }

    [Theory]
    [InlineData(-50, "Пароль слишком простой", "#FF4C4C")]
    [InlineData(0, "Пароль слишком простой", "#FF4C4C")]
    [InlineData(19, "Пароль слишком простой", "#FF4C4C")]
    [InlineData(20, "Пароль всё ещё слишком лёгкий", "#FF8000")]
    [InlineData(39, "Пароль всё ещё слишком лёгкий", "#FF8000")]
    [InlineData(40, "Пароль средней сложности", "#FFD700")]
    [InlineData(59, "Пароль средней сложности", "#FFD700")]
    [InlineData(60, "Пароль достаточно надёжный", "#7FFF00")]
    [InlineData(79, "Пароль достаточно надёжный", "#7FFF00")]
    [InlineData(80, "Надёжный пароль", "#00CC66")]
    [InlineData(100, "Надёжный пароль", "#00CC66")]
    [InlineData(150, "Надёжный пароль", "#00CC66")]
    public void GetPasswordStrengthMessage_MapsScoreToCorrectBucket(int score, string expectedMessage, string expectedColor)
    {
        var (message, colorHex) = SecurityUtilities.GetPasswordStrengthMessage(score);

        message.Should().Be(expectedMessage);
        colorHex.Should().Be(expectedColor);
    }
}
