using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace BarkCloud.Users.IntegrationTests;

// Изолирует сетевой сбой одного отправителя, не останавливая общий тестовый RabbitMQ.
internal sealed class RabbitConnectionProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<TcpClient> _connections = [];
    private readonly ConcurrentBag<Task> _forwarders = [];
    private readonly Task _accept;
    private readonly Uri _target;
    private volatile bool _enabled;

    public RabbitConnectionProxy(Uri target)
    {
        _target = target;
        _listener.Start();
        Address = new UriBuilder(target) { Host = "127.0.0.1", Port = ((IPEndPoint)_listener.LocalEndpoint).Port }.Uri;
        _accept = AcceptAsync();
    }

    public Uri Address { get; }

    public void Enable() => _enabled = true;

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (!_enabled)
                {
                    client.Dispose();
                    continue;
                }
                _forwarders.Add(ForwardAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ForwardAsync(TcpClient client)
    {
        using (client)
        using (var upstream = new TcpClient())
        {
            _connections.Add(client);
            _connections.Add(upstream);
            try
            {
                await upstream.ConnectAsync(_target.Host, _target.IsDefaultPort ? 5672 : _target.Port, _stop.Token);
                var send = client.GetStream().CopyToAsync(upstream.GetStream(), _stop.Token);
                var receive = upstream.GetStream().CopyToAsync(client.GetStream(), _stop.Token);
                await Task.WhenAny(send, receive);
                client.Dispose();
                upstream.Dispose();
                await Task.WhenAll(send, receive);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _accept;
        _listener.Stop();
        foreach (var connection in _connections)
            connection.Dispose();
        await Task.WhenAll(_forwarders);
        _stop.Dispose();
    }
}
