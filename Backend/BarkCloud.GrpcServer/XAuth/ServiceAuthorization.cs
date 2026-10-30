using BarkCloud.Shared.Identity;

using Microsoft.AspNetCore.Authorization;

namespace BarkCloud.GrpcServer.XAuth;

/// <summary>Имена политик авторизации XAuth для межсервисных RPC.</summary>
public static class XAuthPolicies
{
    /// <summary>Префикс политик «сервисный токен с конкретным разрешением».</summary>
    public const string ServicePermissionPrefix = "service-permission:";

    public static string ServicePermission(string permission) => ServicePermissionPrefix + permission;

    // Константы для [Authorize(Policy = …)]: аргумент атрибута требует константное выражение.
    public const string RevocationReadPolicy = ServicePermissionPrefix + ServicePermissions.RevocationRead;
    public const string UsersServerReadPolicy = ServicePermissionPrefix + ServicePermissions.UsersServerRead;
    public const string UsersServerWritePolicy = ServicePermissionPrefix + ServicePermissions.UsersServerWrite;
    public const string FilesServerReadPolicy = ServicePermissionPrefix + ServicePermissions.FilesServerRead;
    public const string FilesServerWritePolicy = ServicePermissionPrefix + ServicePermissions.FilesServerWrite;
    public const string FilesServerStorageAdminPolicy = ServicePermissionPrefix + ServicePermissions.FilesServerStorageAdmin;
}

/// <summary>
/// Требование к сервисному токену: выпущен Identity для этого сервиса (x-target-service),
/// личность вызывающего распознана (x-service-id) и в токене есть перечень разрешений.
/// При <see cref="Permission"/> == null — любое валидное разрешение не требуется
/// (политика <c>Service</c>); иначе клейм x-permissions должен содержать указанное право.
/// </summary>
public sealed record ServiceTokenRequirement(ServiceId Self, string? Permission = null) : IAuthorizationRequirement;

public sealed class ServiceTokenAuthorizationHandler : AuthorizationHandler<ServiceTokenRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ServiceTokenRequirement requirement)
    {
        var user = context.User;

        // Значения клеймов числовые (см. JwtService.GenerateServiceToken).
        if (user.FindFirst(IdentityClaims.TokenType)?.Value != TokenType.Service.ToString()
            || user.FindFirst(IdentityClaims.TargetService)?.Value != ((int)requirement.Self).ToString())
        {
            return Task.CompletedTask;
        }

        var caller = user.FindFirst(IdentityClaims.ServiceId)?.Value;
        if (!Enum.TryParse<ServiceId>(caller, out var callerId) || callerId == ServiceId.Unknown)
            return Task.CompletedTask;

        var permissions = user.FindFirst(IdentityClaims.Permissions)?.Value;

        // Клейм разрешений обязан присутствовать: его ставит только Identity при выпуске.
        if (permissions is null)
            return Task.CompletedTask;

        if (requirement.Permission is null
            || permissions.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
