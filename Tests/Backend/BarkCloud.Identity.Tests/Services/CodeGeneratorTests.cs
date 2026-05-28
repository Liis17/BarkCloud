using BarkCloud.Identity.Services;

namespace BarkCloud.Identity.Tests.Services;

public class CodeGeneratorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(32)]
    public void GenerateDigitalCode_ProducesExactRequestedLength(int length)
    {
        var code = CodeGenerator.GenerateDigitalCode(length);

        code.Length.Should().Be(length);
    }

    [Fact]
    public void GenerateDigitalCode_ContainsOnlyDigits()
    {
        var code = CodeGenerator.GenerateDigitalCode(64);

        code.Should().MatchRegex("^[0-9]+$");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void GenerateDigitalCode_NonPositiveLength_Throws(int length)
    {
        var act = () => CodeGenerator.GenerateDigitalCode(length);

        act.Should().Throw<ArgumentException>().WithParameterName("length");
    }
}
