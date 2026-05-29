using BarkCloud.Configuration.Domain;
using BarkCloud.Configuration.Features.GetConfiguration;
using BarkCloud.Configuration.Infrastructure;
using BarkCloud.Shared.Identity;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Configuration.Tests.Features.GetConfiguration;

public class GetConfigurationCommandHandlerTests
{
    private readonly Mock<IConfigurationStorage> _storage = new();

    private GetConfigurationCommandHandler CreateSut() =>
        new(_storage.Object, NullLogger<GetConfigurationCommandHandler>.Instance);

    private static ConfigurationItem Item(string section, string key, string value, ServiceId serviceId) => new()
    {
        Section = section,
        Key = key,
        Value = value,
        ServiceId = serviceId,
        EditedAt = DateTime.UtcNow,
        EditedBy = "admin",
        EditedFrom = "panel"
    };

    [Fact]
    public async Task Handle_PrefersServiceSpecificOverUnknownForSameSectionKey()
    {
        _storage.Setup(s => s.GetConfiguration(ServiceId.Files))
            .ReturnsAsync(new List<ConfigurationItem>
            {
                Item("Smtp", "Host", "global", ServiceId.Unknown),
                Item("Smtp", "Host", "files-specific", ServiceId.Files),
                Item("Smtp", "Port", "587", ServiceId.Unknown),
            });

        var response = await CreateSut().Handle(new GetConfigurationCommand { ServiceId = ServiceId.Files }, default);

        response.Configurations.Should().HaveCount(2);
        response.Configurations.Single(c => c.Key == "Host").Value.Should().Be("files-specific");
        response.Configurations.Single(c => c.Key == "Port").Value.Should().Be("587");
    }

    [Fact]
    public async Task Handle_NoConfigurations_ReturnsEmpty()
    {
        _storage.Setup(s => s.GetConfiguration(It.IsAny<ServiceId>()))
            .ReturnsAsync(new List<ConfigurationItem>());

        var response = await CreateSut().Handle(new GetConfigurationCommand { ServiceId = ServiceId.Files }, default);

        response.Configurations.Should().BeEmpty();
    }
}
