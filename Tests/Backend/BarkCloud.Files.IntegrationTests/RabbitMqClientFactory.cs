using RabbitMQ.Client;

namespace BarkCloud.Files.IntegrationTests;

internal static class RabbitMqClientFactory
{
    public static ConnectionFactory FromMassTransitAddress(Uri address)
    {
        var amqpAddress = new UriBuilder(address) { Scheme = "amqp" }.Uri;
        return new ConnectionFactory { Uri = amqpAddress };
    }
}
