using BarkCloud.Proto.Files;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;

using Grpc.Core;

namespace BarkCloud.Web.Endpoints;

public static class TextPreviewEndpoints
{
    public static void MapTextPreviewEndpoints(this WebApplication app)
    {
        app.MapGet("/api/files/text", async (HttpContext http, AuthGateway auth, FilesApi.FilesApiClient files, TextPreviewProxy proxy, string id) =>
            await Handle(http, async () =>
            {
                var user = await auth.AuthenticateAsync(http);
                if (user is null) return Error("Не авторизован", 401);
                if (!Guid.TryParse(id, out _)) return Error("Некорректный id файла", 400);
                var request = new GetTempDownloadUrlRequest(); request.FileIds.Add(id);
                var response = await files.GetTempDownloadUrlAsync(request, BrowserContext.UserToken(user.AccessToken), cancellationToken: http.RequestAborted);
                var url = response.FileUrls.FirstOrDefault(x => x.FileId == id)?.Url;
                return string.IsNullOrEmpty(url) ? Error("Файл не найден", 404) : await proxy.FetchAsync(http, url);
            }));

        app.MapGet("/api/shared/text", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, TextPreviewProxy proxy, string fileId) =>
            await Handle(http, async () =>
            {
                var user = await auth.AuthenticateAsync(http);
                if (user is null) return Error("Не авторизован", 401);
                if (!Guid.TryParse(fileId, out _)) return Error("Некорректный id файла", 400);
                var response = await cloud.GetSharedFileDownloadUrlAsync(new GetSharedFileDownloadUrlRequest { FileId = fileId },
                    BrowserContext.UserToken(user.AccessToken), cancellationToken: http.RequestAborted);
                return await proxy.FetchAsync(http, response.DownloadUrl);
            }));

        app.MapGet("/api/public/shares/{token}/text", async (HttpContext http, FilesServerApi.FilesServerApiClient files, TextPreviewProxy proxy, string token) =>
            await Handle(http, async () =>
            {
                var response = await files.ResolveShareAsync(new ResolveShareRequest { Token = token }, cancellationToken: http.RequestAborted);
                return response.Found ? await proxy.FetchAsync(http, response.DownloadUrl) : Error("Ссылка не найдена или была отозвана", 404);
            }));

        app.MapGet("/api/public/folder-shares/{token}/text", async (HttpContext http, FilesServerApi.FilesServerApiClient files,
            TextPreviewProxy proxy, string token, string fileId, string? dir) =>
            await Handle(http, async () =>
            {
                if (!Guid.TryParse(fileId, out _)) return Error("Некорректный id файла", 400);
                var response = await files.ResolveFolderShareAsync(new ResolveFolderShareRequest { Token = token, Dir = dir ?? "" }, cancellationToken: http.RequestAborted);
                var file = response.Found ? response.Files.FirstOrDefault(x => x.FileId == fileId) : null;
                return file is null ? Error("Файл не найден или доступ отозван", 404) : await proxy.FetchAsync(http, file.DownloadUrl);
            }));
    }

    private static async Task<IResult> Handle(HttpContext http, Func<Task<IResult>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated) { return Error("Не авторизован", 401); }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied
            || ex.Trailers.GetValue("x-error-code") == "D4F1E2A8-9C5B-4A77-83BB-5E6F7A8B9C01") { return Error("Нет доступа к файлу", 403); }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.NotFound or StatusCode.FailedPrecondition) { return Error("Файл не найден или ещё не готов", 404); }
        catch (RpcException) { return Error("Сервис файлов недоступен", 502); }
        catch (HttpRequestException) { return Error("Не удалось загрузить содержимое файла", 502); }
        catch (IOException) { return Error("Не удалось загрузить содержимое файла", 502); }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested) { return Error("Не удалось загрузить файл: время ожидания истекло", 504); }
    }

    private static IResult Error(string message, int status) => Results.Json(new { error = message }, statusCode: status);
}
