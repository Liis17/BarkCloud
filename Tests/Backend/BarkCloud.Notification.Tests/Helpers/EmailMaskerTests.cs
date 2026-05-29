using BarkCloud.Notification.Helpers;

namespace BarkCloud.Notification.Tests.Helpers;

public class EmailMaskerTests
{
    [Theory]
    [InlineData("user@example.com", "***@example.com")]
    [InlineData("a.b+c@mail.co.uk", "***@mail.co.uk")]
    public void Mask_NormalEmail_HidesLocalPart(string email, string expected)
        => EmailMasker.Mask(email).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("noatsign")]
    [InlineData("@leadingat.com")]
    public void Mask_InvalidOrEmpty_ReturnsStars(string email)
        => EmailMasker.Mask(email).Should().Be("***");
}
