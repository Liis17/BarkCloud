using BarkCloud.Proto.Files;
using BarkCloud.Proto.Identity;

using Grpc.Core.Interceptors;
using Grpc.Net.Client;

namespace BarkCloud.Drive.Engine;

// Микросервисы на РАЗНЫХ портах (как в iOS GrpcManager): Identity :7020, Files :7025.
// Поэтому два канала с общим интерсептором (device + токен). CloudApi/FilesApi делят
// канал Files. HttpClient — для скачивания байтов с Files-вебки.
internal sealed class BarkCloudConnection : IDisposable
{
    private readonly GrpcChannel _identityChannel;
    private readonly GrpcChannel _filesChannel;

    public IdentityApi.IdentityApiClient Identity { get; }
    public CloudApi.CloudApiClient Cloud { get; }
    public FilesApi.FilesApiClient Files { get; }
    public HttpClient Http { get; }

    public BarkCloudConnection(string identityAddress, string filesAddress, bool acceptAnyCert,
        DeviceIdentity device, Func<string?> tokenProvider)
    {
        var interceptor = new MetadataInterceptor(device, tokenProvider);

        _identityChannel = CreateChannel(identityAddress, acceptAnyCert);
        Identity = new IdentityApi.IdentityApiClient(_identityChannel.Intercept(interceptor));

        _filesChannel = CreateChannel(filesAddress, acceptAnyCert);
        var filesInvoker = _filesChannel.Intercept(interceptor);
        Cloud = new CloudApi.CloudApiClient(filesInvoker);
        Files = new FilesApi.FilesApiClient(filesInvoker);

        var downloadHandler = new SocketsHttpHandler();
        if (acceptAnyCert)
            downloadHandler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        Http = new HttpClient(downloadHandler);
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
    }
}
