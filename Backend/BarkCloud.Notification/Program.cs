using BarkCloud.GrpcServer;
using BarkCloud.Notification.Configurations;
using BarkCloud.Notification.Consumers;
using BarkCloud.Notification.Parsers;
using BarkCloud.Notification.Senders;
using BarkCloud.Shared.Identity;

using MassTransit;

namespace BarkCloud.Notification;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        await builder.LoadConfigurationAsync(ServiceId.Notification);
        await using var loggingLifetime = await builder.AddBarkCloudSerilogAsync("BarkCloud.Notification");
        builder.SetRunningAddress(builder.Configuration);
        builder.Services.AddBarkCloudMetrics("BarkCloud.Notification");

        builder.Services.AddSettings<EmailConfiguration>(builder.Configuration, "Email");
        builder.Services.AddMassTransit(x =>
        {
            x.AddConsumer<EmailQueueConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(builder.Configuration["RabbitMQ:Host"], builder.Configuration["RabbitMQ:VirtualHost"], h =>
                {
                    h.Username(builder.Configuration["RabbitMQ:Username"]);
                    h.Password(builder.Configuration["RabbitMQ:Password"]);
                });
                cfg.OverrideDefaultBusEndpointQueueName("bark-notification-bus-" + Guid.NewGuid().ToString("N"));

                cfg.ReceiveEndpoint("notifications-email-handler", e =>
                {
                    e.ConfigureConsumer<EmailQueueConsumer>(context);
                });
            });
        });

        builder.Services.AddTransient<EmailSender>();
        builder.Services.AddTransient<HtmlEmailTemplateParser>();

        var app = builder.Build();
        if (app.Services.GetRequiredService<EmailConfiguration>().AllowInsecure)
            app.Logger.LogWarning("Email:AllowInsecure=true: проверка сертификата SMTP отключена; разрешена отправка без TLS");
        app.UseRouting();

        await app.RunAsync();
    }
}
