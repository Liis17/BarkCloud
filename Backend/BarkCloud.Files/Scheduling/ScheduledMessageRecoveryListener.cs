using MassTransit.QuartzIntegration;

using Quartz;
using Quartz.Listener;

namespace BarkCloud.Files.Scheduling;

public sealed class ScheduledMessageRecoveryListener(
    ILogger<ScheduledMessageRecoveryListener> logger) : JobListenerSupport
{
    public static readonly TimeSpan RecoveryDelay = TimeSpan.FromSeconds(30);
    public override string Name => "files-scheduled-message-recovery";

    public override async Task JobWasExecuted(IJobExecutionContext context,
        JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        if (context.JobDetail.JobType != typeof(ScheduledMessageJob)
            || jobException is null || jobException.RefireImmediately)
            return;

        // Copy the entire map, including transport properties and MT-Redelivery-Count.
        // Schedule a new trigger before Quartz completes/removes the failed one.
        var trigger = TriggerBuilder.Create()
            .ForJob(context.JobDetail.Key)
            .WithIdentity(Guid.NewGuid().ToString("N"), "files-transport-recovery")
            .UsingJobData(new JobDataMap((IDictionary<string, object>)context.MergedJobDataMap))
            .StartAt(DateTimeOffset.UtcNow.Add(RecoveryDelay))
            .WithSchedule(SimpleScheduleBuilder.Create().WithMisfireHandlingInstructionFireNow())
            .Build();

        await context.Scheduler.ScheduleJob(trigger, cancellationToken);
        logger.LogWarning(jobException,
            "Scheduled message send failed; persisted recovery trigger {TriggerKey} for {Destination}",
            trigger.Key, context.MergedJobDataMap.GetString("Destination"));
    }
}
