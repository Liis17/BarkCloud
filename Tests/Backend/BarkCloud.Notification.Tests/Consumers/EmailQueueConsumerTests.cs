using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Notification.Configurations;
using BarkCloud.Notification.Consumers;
using BarkCloud.Notification.Parsers;
using BarkCloud.Notification.Senders;
using BarkCloud.Shared.Queue.Notifications;

using MassTransit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Notification.Tests.Consumers;

public class EmailQueueConsumerTests
{
    private readonly Mock<EmailSender> _sender;
    private readonly MetricsCollector _metrics = new();

    public EmailQueueConsumerTests()
    {
        _sender = new Mock<EmailSender>(
            new EmailConfiguration { Host = "h", Port = 25, SenderEmail = "s@e", SenderPassword = "p" },
            new HtmlEmailTemplateParser(),
            NullLogger<EmailSender>.Instance);
    }

    private EmailQueueConsumer CreateSut() => new(
        _sender.Object, NullLogger<EmailQueueConsumer>.Instance, _metrics);

    private static Mock<ConsumeContext<EmailNotification>> Context(EmailNotification msg)
    {
        var ctx = new Mock<ConsumeContext<EmailNotification>>();
        ctx.SetupGet(c => c.Message).Returns(msg);
        return ctx;
    }

    [Fact]
    public async Task Consume_SendsEmailAndIncrementsSentMetric()
    {
        _sender.Setup(s => s.SendEmail(It.IsAny<EmailNotification>())).Returns(Task.CompletedTask);
        var msg = new EmailNotification { Address = "u@e", Type = NotificationType.SuccessfulLogin, Title = "T" };

        await CreateSut().Consume(Context(msg).Object);

        _sender.Verify(s => s.SendEmail(msg), Times.Once);
        var snap = _metrics.SnapshotAndReset();
        snap["emails_sent"].Should().Be(1);
        snap["rabbitmq_events_consumed"].Should().Be(1);
    }

    [Fact]
    public async Task Consume_SenderThrows_IncrementsFailedMetricAndRethrows()
    {
        _sender.Setup(s => s.SendEmail(It.IsAny<EmailNotification>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"));
        var msg = new EmailNotification { Address = "u@e", Type = NotificationType.SuccessfulLogin, Title = "T" };

        var act = () => CreateSut().Consume(Context(msg).Object);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _metrics.SnapshotAndReset()["emails_failed"].Should().Be(1);
    }
}
