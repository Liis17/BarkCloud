using System.Text;

using Grpc.Core;
using Grpc.Core.Interceptors;

namespace BarkCloud.Drive.Engine;

// Кладёт в метадату device-заголовки (Base64(UTF8), как ждёт сервер) на каждый вызов,
// плюс x-auth-token (сырой) если токен уже есть. Токен берётся динамически —
// поэтому авторефреш не требует пересоздания канала/клиентов.
internal sealed class MetadataInterceptor(DeviceIdentity device, Func<string?> tokenProvider) : Interceptor
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        => continuation(request, WithHeaders(context));

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
        => continuation(request, WithHeaders(context));

    private ClientInterceptorContext<TRequest, TResponse> WithHeaders<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var h = context.Options.Headers ?? new Metadata();

        h.Add("x-device-name", B64(device.DeviceName));
        h.Add("x-os-name", B64(device.OsName));
        h.Add("x-app-name", B64(device.AppName));
        h.Add("x-app-version", B64(device.AppVersion));
        h.Add("x-device-id", B64(device.DeviceId));

        var token = tokenProvider();
        if (!string.IsNullOrEmpty(token))
            h.Add("x-auth-token", token); // токен — сырой, не base64

        return new ClientInterceptorContext<TRequest, TResponse>(
            context.Method, context.Host, context.Options.WithHeaders(h));
    }

    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
