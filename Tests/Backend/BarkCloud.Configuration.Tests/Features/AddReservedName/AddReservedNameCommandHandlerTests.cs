using BarkCloud.Configuration.Features.AddReservedName;
using BarkCloud.Configuration.Infrastructure;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Configuration.Tests.Features.AddReservedName;

public class AddReservedNameCommandHandlerTests
{
    private readonly Mock<IConfigurationStorage> _storage = new();

    private AddReservedNameCommandHandler CreateSut() =>
        new(_storage.Object, NullLogger<AddReservedNameCommandHandler>.Instance);

    [Fact]
    public async Task Handle_Success_ReturnsSuccessAndCallsStorage()
    {
        var response = await CreateSut().Handle(new AddReservedNameCommand { Name = "admin" }, default);

        response.Success.Should().BeTrue();
        _storage.Verify(s => s.AddReservedNameAsync("admin"), Times.Once);
    }

    [Fact]
    public async Task Handle_StorageThrows_ReturnsFailureWithMessage()
    {
        _storage.Setup(s => s.AddReservedNameAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("уже зарезервировано"));

        var response = await CreateSut().Handle(new AddReservedNameCommand { Name = "admin" }, default);

        response.Success.Should().BeFalse();
        response.Message.Should().Be("уже зарезервировано");
    }
}
