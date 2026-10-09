using BarkCloud.Files.Scheduling;

using MassTransit.QuartzIntegration;

using Microsoft.Extensions.Logging.Abstractions;

using Quartz;

namespace BarkCloud.Files.Tests.Scheduling;

public class ScheduledMessageRecoveryListenerTests
{
    [Fact]
    public async Task FinalSendFailure_PersistsPayloadAndHeadersForTransportRetry()
    {
        var data = new JobDataMap
        {
            ["Destination"] = "rabbitmq://localhost/process-uploaded-file",
            ["Body"] = "payload",
            ["HeadersAsJson"] = "[{\"Key\":\"MT-Redelivery-Count\",\"Value\":4}]",
            ["TransportProperties"] = "transport"
        };
        var scheduler = new Mock<IScheduler>();
        ITrigger? saved = null;
        scheduler.Setup(x => x.ScheduleJob(It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .Callback<ITrigger, CancellationToken>((trigger, _) => saved = trigger)
            .ReturnsAsync(DateTimeOffset.UtcNow);
        var context = CreateContext(scheduler, data);
        var before = DateTimeOffset.UtcNow;
        var listener = new ScheduledMessageRecoveryListener(NullLogger<ScheduledMessageRecoveryListener>.Instance);

        await listener.JobWasExecuted(context.Object, new JobExecutionException(new IOException("send failed"), false));

        saved.Should().NotBeNull();
        saved!.JobKey.Should().Be(context.Object.JobDetail.Key);
        saved.StartTimeUtc.Should().BeOnOrAfter(before.AddSeconds(30));
        saved.StartTimeUtc.Should().BeBefore(DateTimeOffset.UtcNow.AddSeconds(31));
        saved.MisfireInstruction.Should().Be(MisfireInstruction.SimpleTrigger.FireNow);
        saved.JobDataMap.Should().BeEquivalentTo(data);
        data["Body"] = "changed";
        saved.JobDataMap["Body"].Should().Be("payload");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuccessOrImmediateRefire_DoesNotScheduleAnotherTrigger(bool refire)
    {
        var scheduler = new Mock<IScheduler>();
        var context = CreateContext(scheduler, new JobDataMap());
        var listener = new ScheduledMessageRecoveryListener(NullLogger<ScheduledMessageRecoveryListener>.Instance);

        await listener.JobWasExecuted(context.Object, refire ? new JobExecutionException(new IOException("retry"), true) : null);

        scheduler.Verify(x => x.ScheduleJob(It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<IJobExecutionContext> CreateContext(Mock<IScheduler> scheduler, JobDataMap data)
    {
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(x => x.Scheduler).Returns(scheduler.Object);
        context.SetupGet(x => x.MergedJobDataMap).Returns(data);
        context.SetupGet(x => x.JobDetail).Returns(JobBuilder.Create<ScheduledMessageJob>().WithIdentity("send").StoreDurably().Build());
        return context;
    }
}
