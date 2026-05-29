using BarkCloud.Configuration.Features.UpdateConfiguration;
using BarkCloud.Configuration.Infrastructure;
using BarkCloud.Shared.Identity;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Configuration.Tests.Features.UpdateConfiguration;

public class UpdateConfigurationCommandHandlerTests
{
    private readonly Mock<IConfigurationStorage> _storage = new();

    private UpdateConfigurationCommandHandler CreateSut() =>
        new(_storage.Object, NullLogger<UpdateConfigurationCommandHandler>.Instance);

    private static UpdateConfigurationCommand ValidCommand(int serviceId) => new()
    {
        Section = "Smtp",
        Key = "Host",
        Value = "mail.example.com",
        ServiceId = serviceId,
        EditedBy = "admin",
        EditedFrom = "panel"
    };

    [Fact]
    public async Task Handle_UnknownServiceId_RejectsWithoutCallingStorage()
    {
        var response = await CreateSut().Handle(ValidCommand(99), default);

        response.Success.Should().BeFalse();
        _storage.Verify(
            s => s.UpdateConfigurationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<ServiceId>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_ValidServiceId_CallsStorageAndSucceeds()
    {
        var response = await CreateSut().Handle(ValidCommand((int)ServiceId.Files), default);

        response.Success.Should().BeTrue();
        _storage.Verify(
            s => s.UpdateConfigurationAsync("Smtp", "Host", "mail.example.com", ServiceId.Files, "admin", "panel"),
            Times.Once);
    }

    [Fact]
    public async Task Handle_StorageThrows_ReturnsFailure()
    {
        _storage.Setup(s => s.UpdateConfigurationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<ServiceId>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("db down"));

        var response = await CreateSut().Handle(ValidCommand((int)ServiceId.Files), default);

        response.Success.Should().BeFalse();
        response.Message.Should().Contain("db down");
    }
}
