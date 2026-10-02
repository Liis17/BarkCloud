using BarkCloud.Notification.Configurations;
using BarkCloud.Notification.Parsers;
using BarkCloud.Notification.Senders;
using BarkCloud.Shared.Queue.Notifications;

using Microsoft.Extensions.Logging.Abstractions;

using MailKit.Security;
using MailKit.Net.Smtp;

using MimeKit;

using System.Net.Security;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BarkCloud.Notification.Tests.Senders;

public class EmailSenderTests
{
    [Fact]
    public async Task SendEmail_UntrustedCertificate_RejectsBeforeSendingCredentialsOrMessage()
    {
        using var certificate = LocalSmtpServer.CreateCertificate();
        await using var server = new LocalSmtpServer(certificate);
        var sender = CreateSender(server.Port);
        var act = () => sender.SendEmail(CreateNotification());

        await act.Should().ThrowAsync<SslHandshakeException>();
        server.Commands.Should().NotContain(command => command.StartsWith("AUTH "));
        server.Message.Should().BeNull();
    }

    [Fact]
    public async Task SendEmail_InsecureMode_SendsOverTlsWithUntrustedCertificate()
    {
        using var certificate = LocalSmtpServer.CreateCertificate();
        await using var server = new LocalSmtpServer(certificate);

        await CreateSender(server.Port, allowInsecure: true).SendEmail(CreateNotification());

        server.UsedTls.Should().BeTrue();
        server.Commands.Should().Contain(command => command.StartsWith("AUTH "));
        server.Message.Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendEmail_TrustedCertificate_PreservesHtmlMessage(bool allowInsecure)
    {
        using var authority = LocalSmtpServer.CreateAuthority();
        using var certificate = LocalSmtpServer.CreateCertificate(authority: authority);
        await using var server = new LocalSmtpServer(certificate);
        var sender = new TestEmailSender(CreateConfiguration(server.Port, allowInsecure), server.Port, authority);

        await sender.SendEmail(CreateNotification());

        server.UsedTls.Should().BeTrue();
        using var message = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(server.Message!)));
        message.From.Mailboxes.Single().Address.Should().Be("sender@example.test");
        message.To.Mailboxes.Single().Address.Should().Be("recipient@example.test");
        message.Subject.Should().Be("Проверка SMTP");
        message.HtmlBody.Should().Contain("barker");
    }

    [Theory]
    [InlineData("wrong.example.test", false)]
    [InlineData("localhost", true)]
    public async Task SendEmail_InvalidTrustedCertificate_RejectsBeforeSendingCredentialsOrMessage(string host, bool expired)
    {
        using var authority = LocalSmtpServer.CreateAuthority();
        using var certificate = LocalSmtpServer.CreateCertificate(host, authority, expired);
        await using var server = new LocalSmtpServer(certificate);
        var sender = new TestEmailSender(CreateConfiguration(server.Port), server.Port, authority);
        var act = () => sender.SendEmail(CreateNotification());

        await act.Should().ThrowAsync<SslHandshakeException>();
        server.Commands.Should().NotContain(command => command.StartsWith("AUTH "));
        server.Message.Should().BeNull();
    }

    [Fact]
    public async Task SendEmail_NoStartTls_RejectsBeforeSendingCredentialsOrMessage()
    {
        await using var server = new LocalSmtpServer(null);
        var act = () => CreateSender(server.Port).SendEmail(CreateNotification());

        await act.Should().ThrowAsync<NotSupportedException>();
        server.Commands.Should().NotContain(command => command.StartsWith("AUTH "));
        server.Message.Should().BeNull();
    }

    [Fact]
    public async Task SendEmail_InsecureMode_SendsWithoutTlsWhenServerDoesNotSupportIt()
    {
        await using var server = new LocalSmtpServer(null);

        await CreateSender(server.Port, allowInsecure: true).SendEmail(CreateNotification());

        server.UsedTls.Should().BeFalse();
        server.Commands.Should().Contain(command => command.StartsWith("AUTH "));
        server.Message.Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendEmail_Port465_UsesImplicitTls(bool allowInsecure)
    {
        using var authority = LocalSmtpServer.CreateAuthority();
        using var certificate = LocalSmtpServer.CreateCertificate(authority: authority);
        await using var server = new LocalSmtpServer(certificate, implicitTls: true);
        var sender = new TestEmailSender(CreateConfiguration(465, allowInsecure), server.Port, authority);

        await sender.SendEmail(CreateNotification());

        server.UsedTls.Should().BeTrue();
        server.Commands.Should().NotContain("STARTTLS");
        server.Message.Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendEmail_BrokenTls_DoesNotRetryWithoutEncryption(bool allowInsecure)
    {
        using var certificate = LocalSmtpServer.CreateCertificate();
        await using var server = new LocalSmtpServer(certificate, failTlsHandshake: true);
        var act = () => CreateSender(server.Port, allowInsecure).SendEmail(CreateNotification());

        await act.Should().ThrowAsync<SslHandshakeException>();
        server.ConnectionCount.Should().Be(1);
        server.Commands.Should().NotContain(command => command.StartsWith("AUTH "));
        server.Message.Should().BeNull();
    }

    [Fact]
    public async Task SendEmail_ServerDoesNotAdvertiseAuth_SendsWithoutAuthentication()
    {
        using var authority = LocalSmtpServer.CreateAuthority();
        using var certificate = LocalSmtpServer.CreateCertificate(authority: authority);
        await using var server = new LocalSmtpServer(certificate, advertiseAuthentication: false);
        var sender = new TestEmailSender(CreateConfiguration(server.Port), server.Port, authority);

        await sender.SendEmail(CreateNotification());

        server.Commands.Should().NotContain(command => command.StartsWith("AUTH "));
        server.Message.Should().NotBeNull();
    }

    [Fact]
    public async Task SendEmail_InsecureMode_DoesNotChangeGlobalValidationOrFollowingSecureSend()
    {
#pragma warning disable SYSLIB0014
        var originalCallback = ServicePointManager.ServerCertificateValidationCallback;
#pragma warning restore SYSLIB0014
        using var certificate = LocalSmtpServer.CreateCertificate();
        await using (var insecureServer = new LocalSmtpServer(certificate))
            await CreateSender(insecureServer.Port, allowInsecure: true).SendEmail(CreateNotification());
        await using var secureServer = new LocalSmtpServer(certificate);
        var act = () => CreateSender(secureServer.Port).SendEmail(CreateNotification());

        await act.Should().ThrowAsync<SslHandshakeException>();
#pragma warning disable SYSLIB0014
        Assert.Same(originalCallback, ServicePointManager.ServerCertificateValidationCallback);
#pragma warning restore SYSLIB0014
        secureServer.Commands.Should().NotContain(command => command.StartsWith("AUTH "));
        secureServer.Message.Should().BeNull();
    }

    private static EmailSender CreateSender(int port, bool allowInsecure = false) => new(
        CreateConfiguration(port, allowInsecure), new HtmlEmailTemplateParser(), NullLogger<EmailSender>.Instance);

    private static EmailConfiguration CreateConfiguration(int port, bool allowInsecure = false) => new()
    {
        Host = "localhost",
        Port = port,
        SenderEmail = "sender@example.test",
        SenderPassword = "test-password",
        AllowInsecure = allowInsecure
    };

    private static EmailNotification CreateNotification() => new()
    {
        Address = "recipient@example.test",
        Title = "Проверка SMTP",
        Type = NotificationType.SuccessfulRegistration,
        Payload = new Dictionary<string, string> { ["username"] = "barker" }
    };

    private sealed class TestEmailSender(EmailConfiguration configuration, int port, X509Certificate2 authority)
        : EmailSender(configuration, new HtmlEmailTemplateParser(), NullLogger<EmailSender>.Instance)
    {
        protected override SmtpClient CreateSmtpClient() => new TestSmtpClient(port, authority);
    }

    // The fixture uses an isolated CA and an unprivileged port, including for the port-465 policy.
    private sealed class TestSmtpClient(int port, X509Certificate2 authority) : SmtpClient
    {
        public override Task ConnectAsync(string host, int configuredPort, SecureSocketOptions options,
            CancellationToken cancellationToken = default) => base.ConnectAsync(host, port, options, cancellationToken);

        protected override SslClientAuthenticationOptions GetSslClientAuthenticationOptions(string host,
            RemoteCertificateValidationCallback callback)
        {
            var options = base.GetSslClientAuthenticationOptions(host, callback);
            options.CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck
            };
            options.CertificateChainPolicy.CustomTrustStore.Add(authority);
            return options;
        }
    }
}
