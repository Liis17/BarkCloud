using BarkCloud.Proto.Files;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;

using Grpc.Core.Interceptors;
using Grpc.Net.Client;

namespace BarkCloud.Drive.Engine;

// Микросервисы на РАЗНЫХ портах (как в iOS GrpcManager): Identity :7020, Files :7025, Users :7021.
// Поэтому отдельные каналы с общим интерсептором (device + токен). CloudApi/FilesApi делят
// канал Files. HttpClient — для скачивания байтов с Files-вебки.
internal sealed class BarkCloudConnection : IDisposable
{
    private readonly GrpcChannel _identityChannel;
    private readonly GrpcChannel _filesChannel;
    private readonly GrpcChannel _usersChannel;

    public IdentityApi.IdentityApiClient Identity { get; }
    public CloudApi.CloudApiClient Cloud { get; }
    public FilesApi.FilesApiClient Files { get; }
    public UsersApi.UsersApiClient Users { get; }
    public HttpClient Http { get; }

    public BarkCloudConnection(string identityAddress, string filesAddress, string usersAddress,
        bool acceptAnyCert, DeviceIdentity device, Func<string?> tokenProvider)
    {
        var interceptor = new MetadataInterceptor(device, tokenProvider);

        _identityChannel = CreateChannel(identityAddress, acceptAnyCert);
        Identity = new IdentityApi.IdentityApiClient(_identityChannel.Intercept(interceptor));

        _filesChannel = CreateChannel(filesAddress, acceptAnyCert);
        var filesInvoker = _filesChannel.Intercept(interceptor);
        Cloud = new CloudApi.CloudApiClient(filesInvoker);
        Files = new FilesApi.FilesApiClient(filesInvoker);

        _usersChannel = CreateChannel(usersAddress, acceptAnyCert);
        Users = new UsersApi.UsersApiClient(_usersChannel.Intercept(interceptor));

        var transferHandler = new SocketsHttpHandler();
        if (acceptAnyCert)
            transferHandler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        // Без таймаута: крупные upload/download не должны обрываться по умолчанию (100 c).
        Http = new HttpClient(transferHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static GrpcChannel CreateChannel(string address, bool acceptAnyCert)
    {
        var handler = new SocketsHttpHandler();
        if (acceptAnyCert)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        return GrpcChannel.ForAddress(address,
            new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
    }

    public void Dispose()
    {
        Http.Dispose();
        _identityChannel.Dispose();
        _filesChannel.Dispose();
        _usersChannel.Dispose();
    }
}
