using BarkCloud.Web.Infrastructure;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Rendering;

namespace BarkCloud.Web;

public static class StorageMigrationEndpoints
{
    public static void MapStorageMigrationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/migration/sources", (HttpContext http, AuthGateway auth, AdminGate admin, StorageMigrationService service) =>
            Admin(http, auth, admin, async _ => Results.Ok(await service.GetSourcesAsync(http.RequestAborted))));
        api.MapPost("/migration/check", (HttpContext http, AuthGateway auth, AdminGate admin, StorageMigrationService service, CheckBody body) =>
            Admin(http, auth, admin, async user => Results.Ok(await service.CheckAsync(body.SourceId, body.Destination,
                $"user:{user.UserId}", http.RequestAborted))));
        api.MapPost("/migration/start", (HttpContext http, AuthGateway auth, AdminGate admin, StorageMigrationService service, StartBody body) =>
            Admin(http, auth, admin, user => Task.FromResult(Results.Ok(service.Start(body.ValidationId, $"user:{user.UserId}")))));
        api.MapGet("/migration/jobs", (HttpContext http, AuthGateway auth, AdminGate admin, StorageMigrationService service) =>
            Admin(http, auth, admin, _ => Task.FromResult(Results.Ok(service.GetJobs()))));
        api.MapGet("/migration/jobs/{id}", (HttpContext http, AuthGateway auth, AdminGate admin, StorageMigrationService service, string id) =>
            Admin(http, auth, admin, _ => Task.FromResult(Results.Ok(service.GetJob(id)))));
        api.MapPost("/migration/jobs/{id}/retry", (HttpContext http, AuthGateway auth, AdminGate admin, StorageMigrationService service, string id) =>
            Admin(http, auth, admin, _ => Task.FromResult(Results.Ok(service.Retry(id)))));
        api.MapPost("/migration/jobs/{id}/cancel", (HttpContext http, AuthGateway auth, AdminGate admin, StorageMigrationService service, string id) =>
            Admin(http, auth, admin, async _ => Results.Ok(await service.CancelAsync(id, http.RequestAborted))));
        api.MapPost("/server/storage/migration/apply", (HttpContext http, AuthGateway auth, AdminGate admin, StorageMigrationService service, ApplyBody body) =>
            Admin(http, auth, admin, async _ => Results.Ok(await service.ApplyAsync(body.JobId, http.RequestAborted))));
        api.MapGet("/migration/cutovers", (HttpContext http, AuthGateway auth, AdminGate admin, StorageMigrationService service) =>
            Admin(http, auth, admin, async _ => Results.Ok(await service.GetCutoversAsync(http.RequestAborted))));
        api.MapPost("/migration/cutovers/{id}/cancel", (HttpContext http, AuthGateway auth, AdminGate admin, StorageMigrationService service, string id) =>
            Admin(http, auth, admin, async _ =>
            {
                await service.CancelCutoverAsync(id, http.RequestAborted);
                return Results.Ok(new { message = "Запрошена безопасная отмена переключения." });
            }));
        api.MapPost("/migration/cutovers/{id}/recover", (HttpContext http, AuthGateway auth, AdminGate admin, StorageMigrationService service, string id) =>
            Admin(http, auth, admin, async user => Results.Ok(await service.RecoverCutoverAsync(id, $"user:{user.UserId}", http.RequestAborted))));
    }

    private static Task<IResult> Admin(HttpContext http, AuthGateway auth, AdminGate admin, Func<WebUser, Task<IResult>> action) =>
        SettingsEndpoints.DoAdmin(http, auth, admin, async user =>
        {
            try { return await action(user); }
            catch (Exception e) { return Results.BadRequest(new { message = StorageMigrationService.SafeError(e) }); }
        });

    public sealed record CheckBody(string SourceId, MigrationConnection Destination);
    public sealed record StartBody(string ValidationId);
    public sealed record ApplyBody(string JobId);
}
