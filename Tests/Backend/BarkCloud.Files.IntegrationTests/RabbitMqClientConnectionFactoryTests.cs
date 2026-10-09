using RabbitMQ.Client;

namespace BarkCloud.Files.IntegrationTests;

public sealed class RabbitMqClientConnectionFactoryTests
{
    [Fact]
    public void RunnerAddress_UsesRootVirtualHost()
    {
        var factory = RabbitMqClientFactory.FromMassTransitAddress(
            new Uri("rabbitmq://integration:integration@127.0.0.1:56718/"));

        factory.HostName.Should().Be("127.0.0.1");
        factory.Port.Should().Be(56718);
        factory.UserName.Should().Be("integration");
        factory.Password.Should().Be("integration");
        factory.VirtualHost.Should().Be("/");
    }

    [Fact]
    public void AddressWithEscapedCredentialsAndNamedVirtualHost_UsesDecodedValues()
    {
        var factory = RabbitMqClientFactory.FromMassTransitAddress(
            new Uri("rabbitmq://user%40name:p%3Ass@broker.example:5679/team%2Fmedia"));

        factory.HostName.Should().Be("broker.example");
        factory.Port.Should().Be(5679);
        factory.UserName.Should().Be("user@name");
        factory.Password.Should().Be("p:ss");
        factory.VirtualHost.Should().Be("team/media");
    }
}
