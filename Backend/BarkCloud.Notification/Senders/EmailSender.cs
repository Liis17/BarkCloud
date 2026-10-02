using BarkCloud.Notification.Configurations;
using BarkCloud.Notification.Helpers;
using BarkCloud.Notification.Parsers;
using BarkCloud.Shared.Queue.Notifications;

using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;

using MimeKit;

namespace BarkCloud.Notification.Senders;

public class EmailSender
{
    private readonly EmailConfiguration _emailConfiguration;
    private readonly HtmlEmailTemplateParser _templateParser;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(
        EmailConfiguration emailConfiguration,
        HtmlEmailTemplateParser templateParser,
        ILogger<EmailSender> logger)
    {
        _emailConfiguration = emailConfiguration;
        _templateParser = templateParser;
        _logger = logger;
    }

    public virtual async Task SendEmail(EmailNotification notification)
    {
        _logger.LogInformation(
            "Начало отправки email на {Email} с темой '{Subject}'",
            EmailMasker.Mask(notification.Address),
            notification.Title
        );

        try
        {
            using var smtpClient = CreateSmtpClient();
            if (_emailConfiguration.AllowInsecure)
                smtpClient.ServerCertificateValidationCallback = (_, _, _, _) => true;

            _logger.LogDebug(
                "Парсинг HTML шаблона для типа уведомления {NotificationType}",
                notification.Type
            );

            var body = await _templateParser.Parse(notification.Type, notification.Payload);

            using var mailMessage = new MimeMessage();
            mailMessage.From.Add(MailboxAddress.Parse(_emailConfiguration.SenderEmail));
            mailMessage.Subject = notification.Title;
            mailMessage.Body = new TextPart("html") { Text = body };
            mailMessage.To.Add(MailboxAddress.Parse(notification.Address));

            _logger.LogDebug(
                "Отправка email через SMTP сервер {Host}:{Port}",
                _emailConfiguration.Host,
                _emailConfiguration.Port
            );

            var socketOptions = _emailConfiguration.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : _emailConfiguration.AllowInsecure
                    ? SecureSocketOptions.StartTlsWhenAvailable
                    : SecureSocketOptions.StartTls;
            await smtpClient.ConnectAsync(_emailConfiguration.Host, _emailConfiguration.Port, socketOptions);
            if (smtpClient.Capabilities.HasFlag(SmtpCapabilities.Authentication))
                await smtpClient.AuthenticateAsync(_emailConfiguration.SenderEmail, _emailConfiguration.SenderPassword);
            await smtpClient.SendAsync(mailMessage);
            await smtpClient.DisconnectAsync(true);

            _logger.LogInformation(
                "Email успешно отправлен на {Email}",
                EmailMasker.Mask(notification.Address)
            );
        }
        catch (SmtpCommandException ex)
        {
            _logger.LogError(
                ex,
                "SMTP ошибка при отправке email на {Email}. StatusCode: {StatusCode}",
                EmailMasker.Mask(notification.Address),
                ex.StatusCode
            );
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Неожиданная ошибка при отправке email на {Email}",
                EmailMasker.Mask(notification.Address)
            );
            throw;
        }
    }

    protected virtual SmtpClient CreateSmtpClient() => new();
}