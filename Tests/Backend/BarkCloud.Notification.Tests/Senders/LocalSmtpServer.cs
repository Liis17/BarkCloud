using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BarkCloud.Notification.Tests.Senders;

internal sealed class LocalSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
    private readonly X509Certificate2? _certificate;
    private readonly bool _implicitTls;
    private readonly bool _failTlsHandshake;
    private readonly bool _advertiseAuthentication;
    private readonly Task _serverTask;

    public LocalSmtpServer(X509Certificate2? certificate, bool implicitTls = false,
        bool failTlsHandshake = false, bool advertiseAuthentication = true)
    {
        _certificate = certificate;
        _implicitTls = implicitTls;
        _failTlsHandshake = failTlsHandshake;
        _advertiseAuthentication = advertiseAuthentication;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serverTask = RunAsync();
    }

    public int Port { get; }
    public ConcurrentQueue<string> Commands { get; } = new();
    public string? Message { get; private set; }
    public bool UsedTls { get; private set; }
    public int ConnectionCount { get; private set; }

    public static X509Certificate2 CreateAuthority()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=SMTP test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(30));
    }

    public static X509Certificate2 CreateCertificate(string host = "localhost",
        X509Certificate2? authority = null, bool expired = false)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var notBefore = DateTimeOffset.UtcNow.AddDays(-2);
        var notAfter = DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1);
        if (authority == null)
            return request.CreateSelfSigned(notBefore, notAfter);

        using var signed = request.Create(authority, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
        return signed.CopyWithPrivateKey(key);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                ConnectionCount++;
                try
                {
                    if (_implicitTls)
                    {
                        using var tls = new SslStream(client.GetStream());
                        await AuthenticateTlsAsync(tls);
                        await ServeAsync(tls, sendGreeting: true);
                    }
                    else
                    {
                        await ServeAsync(client.GetStream(), sendGreeting: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or AuthenticationException)
                {
                    // A rejected TLS handshake or a disconnected client ends this connection.
                }
            }
        }
        catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or SocketException)
        {
            // The listener is stopped when the test disposes the server.
        }
    }

    private async Task AuthenticateTlsAsync(SslStream tls)
    {
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _certificate,
            EnabledSslProtocols = SslProtocols.None
        }, _stop.Token);
        UsedTls = true;
    }

    private async Task ServeAsync(Stream stream, bool sendGreeting)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n"
        };

        if (sendGreeting)
            await writer.WriteLineAsync("220 localhost test SMTP");

        while (await reader.ReadLineAsync(_stop.Token) is { } line)
        {
            Commands.Enqueue(line);
            var verb = line.Split(' ', 2)[0].ToUpperInvariant();
            switch (verb)
            {
                case "EHLO":
                    await writer.WriteLineAsync("250-localhost");
                    if (_certificate != null && !UsedTls)
                        await writer.WriteLineAsync("250-STARTTLS");
                    if (_advertiseAuthentication)
                        await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
                    await writer.WriteLineAsync("250 8BITMIME");
                    break;
                case "HELO":
                case "MAIL":
                case "RCPT":
                case "RSET":
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "STARTTLS":
                    await writer.WriteLineAsync("220 Ready for TLS");
                    if (_failTlsHandshake)
                        return;
                    using (var tls = new SslStream(stream, leaveInnerStreamOpen: true))
                    {
                        await AuthenticateTlsAsync(tls);
                        await ServeAsync(tls, sendGreeting: false);
                    }
                    return;
                case "AUTH":
                    if (line.Equals("AUTH LOGIN", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                        await reader.ReadLineAsync(_stop.Token);
                        await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                        await reader.ReadLineAsync(_stop.Token);
                    }
                    await writer.WriteLineAsync("235 Authenticated");
                    break;
                case "DATA":
                    await writer.WriteLineAsync("354 End with a dot");
                    var message = new StringBuilder();
                    while (await reader.ReadLineAsync(_stop.Token) is { } bodyLine && bodyLine != ".")
                        message.AppendLine(bodyLine.StartsWith("..") ? bodyLine[1..] : bodyLine);
                    Message = message.ToString();
                    await writer.WriteLineAsync("250 Accepted");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 Bye");
                    return;
                default:
                    await writer.WriteLineAsync("502 Unsupported command");
                    break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _serverTask;
        _stop.Dispose();
    }
}
