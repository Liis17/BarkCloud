using BarkCloud.GrpcServer;
using BarkCloud.Notification.Configurations;
using BarkCloud.Notification.Consumers;
using BarkCloud.Notification.Parsers;
using BarkCloud.Notification.Senders;
using BarkCloud.Shared.Identity;

using MassTransit;

using Serilog;

namespace BarkCloud.Notification;

public class Program
{
    public static void Main(string[] args)
    {
        var allowInsecure = EmailConfiguration.ParseAllowInsecure(
            Environment.GetEnvironmentVariable("SMTP_ALLOW_INSECURE"));
        var builder = WebApplication.CreateBuilder(args);

        builder.LoadConfiguration(ServiceId.Notification);
        builder.AddBarkCloudSerilog("BarkCloud.Notification");
        builder.SetRunningAddress(builder.Configuration);
        builder.Services.AddBarkCloudMetrics("BarkCloud.Notification");

        builder.Services.AddSettings<EmailConfiguration>(builder.Configuration, "Email");
        builder.Services.PostConfigure<EmailConfiguration>(settings => settings.AllowInsecure = allowInsecure);
        builder.Services.AddMassTransit(x =>
        {
            x.AddConsumer<EmailQueueConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(builder.Configuration["RabbitMQ:Host"], "/", h =>
                {
                    h.Username(builder.Configuration["RabbitMQ:Username"]);
                    h.Password(builder.Configuration["RabbitMQ:Password"]);
                });

                cfg.ReceiveEndpoint("notifications-email-handler", e =>
                {
                    e.ConfigureConsumer<EmailQueueConsumer>(context);
                });
            });
        });

        builder.Services.AddTransient<EmailSender>();
        builder.Services.AddTransient<HtmlEmailTemplateParser>();

        var app = builder.Build();
        if (allowInsecure)
            app.Logger.LogWarning("SMTP_ALLOW_INSECURE=true: проверка сертификата SMTP отключена; разрешена отправка без TLS");
        app.UseRouting();

        app.Lifetime.ApplicationStopped.Register(Log.CloseAndFlush);
        app.Run();
    }
}