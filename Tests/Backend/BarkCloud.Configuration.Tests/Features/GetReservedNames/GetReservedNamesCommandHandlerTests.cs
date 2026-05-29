using BarkCloud.Configuration.Features.GetReservedNames;
using BarkCloud.Configuration.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Configuration.Tests.Features.GetReservedNames;

public class GetReservedNamesCommandHandlerTests
{
    private readonly Mock<IConfigurationStorage> _storage = new();

    private GetReservedNamesCommandHandler CreateSut() =>
        new(_storage.Object, NullLogger<GetReservedNamesCommandHandler>.Instance);

    [Fact]
    public async Task Handle_ReturnsNamesFromStorage()
    {
        _storage.Setup(s => s.GetReservedNamesAsync())
            .ReturnsAsync(new List<string> { "admin", "support", "help" });

        var response = await CreateSut().Handle(new GetReservedNamesCommand(), default);

        response.Names.Should().Equal("admin", "support", "help");
    }

    [Fact]
    public async Task Handle_NoNames_ReturnsEmpty()
    {
        _storage.Setup(s => s.GetReservedNamesAsync()).ReturnsAsync(new List<string>());

        var response = await CreateSut().Handle(new GetReservedNamesCommand(), default);

        response.Names.Should().BeEmpty();
    }
}
