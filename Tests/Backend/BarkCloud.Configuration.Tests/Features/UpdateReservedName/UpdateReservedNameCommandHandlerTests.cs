using BarkCloud.Configuration.Features.UpdateReservedName;
using BarkCloud.Configuration.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Configuration.Tests.Features.UpdateReservedName;

public class UpdateReservedNameCommandHandlerTests
{
    private readonly Mock<IConfigurationStorage> _storage = new();

    private UpdateReservedNameCommandHandler CreateSut() =>
        new(_storage.Object, NullLogger<UpdateReservedNameCommandHandler>.Instance);

    [Fact]
    public async Task Handle_Success_ReturnsSuccessAndCallsStorage()
    {
        var response = await CreateSut().Handle(
            new UpdateReservedNameCommand { OldName = "admin", NewName = "root" }, default);

        response.Success.Should().BeTrue();
        _storage.Verify(s => s.UpdateReservedNameAsync("admin", "root"), Times.Once);
    }

    [Fact]
    public async Task Handle_StorageThrows_ReturnsFailureWithMessage()
    {
        _storage.Setup(s => s.UpdateReservedNameAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("не найдено"));

        var response = await CreateSut().Handle(
            new UpdateReservedNameCommand { OldName = "ghost", NewName = "root" }, default);

        response.Success.Should().BeFalse();
        response.Message.Should().Be("не найдено");
    }
}
