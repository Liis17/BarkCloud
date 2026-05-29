using BarkCloud.Configuration.Features.DeleteReservedName;
using BarkCloud.Configuration.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Configuration.Tests.Features.DeleteReservedName;

public class DeleteReservedNameCommandHandlerTests
{
    private readonly Mock<IConfigurationStorage> _storage = new();

    private DeleteReservedNameCommandHandler CreateSut() =>
        new(_storage.Object, NullLogger<DeleteReservedNameCommandHandler>.Instance);

    [Fact]
    public async Task Handle_Success_ReturnsSuccessAndCallsStorage()
    {
        var response = await CreateSut().Handle(new DeleteReservedNameCommand { Name = "admin" }, default);

        response.Success.Should().BeTrue();
        _storage.Verify(s => s.DeleteReservedNameAsync("admin"), Times.Once);
    }

    [Fact]
    public async Task Handle_StorageThrows_ReturnsFailureWithMessage()
    {
        _storage.Setup(s => s.DeleteReservedNameAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("не найдено"));

        var response = await CreateSut().Handle(new DeleteReservedNameCommand { Name = "ghost" }, default);

        response.Success.Should().BeFalse();
        response.Message.Should().Be("не найдено");
    }
}
