using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Features.CreateToken;
using BarkCloud.Identity.Features.SetPassword;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.IntegrationTests;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Identity;
using BarkCloud.Shared.Queue.Notifications;
using Grpc.Core;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using System.IdentityModel.Tokens.Jwt;

foreach (var size in new[] { 15, 16, 31, 32 })
{
    var secret = new string('x', size);
    bool startupAccepted;
    try { JwtSecret.GetKeyBytes(secret); startupAccepted = true; }
    catch (InvalidOperationException) { startupAccepted = false; }
    try
    {
        var jwt = new JwtService(new JwtSettings { SecretKey = secret, Issuer = "audit", Audience = "audit", ExpiryMinutes = 10 });
        var token = jwt.GenerateUserToken(42, "device", 1).Value;
        new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            IssuerSigningKey = new SymmetricSecurityKey(JwtSecret.GetKeyBytes(secret)),
            ValidateIssuer = true, ValidIssuer = "audit", ValidateAudience = true, ValidAudience = "audit", ValidateLifetime = true
        }, out _);
        Console.WriteLine($"F23 bytes={size}: startupAccepted={startupAccepted}; signAndValidate=success");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"F23 bytes={size}: startupAccepted={startupAccepted}; signing={ex.GetType().Name}; IDX10720={ex.Message.Contains("IDX10720")}");
    }
}

await using var db = await PostgresIdentityDatabase.CreateAsync();
await using var sessionContext = db.CreateContext();
var users = new Mock<UsersServerApi.UsersServerApiClient>();
var mediator = new Mock<IMediator>();
var location = new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance);
location.Setup(x => x.GetLocation(It.IsAny<string>())).ReturnsAsync((IpLocation?)null);
var sessionMetrics = new MetricsCollector();
var sessionJwtSettings = new JwtSettings
{
    SecretKey = "audit-session-signing-key-with-at-least-32-bytes",
    Issuer = "audit", Audience = "audit", ExpiryMinutes = 10
};
var sessionRefreshTokens = new RefreshTokensStorage(sessionContext, sessionJwtSettings);
var tokenHandler = new CreateTokenCommandHandler(sessionRefreshTokens, new JwtService(sessionJwtSettings), sessionMetrics,
    NullLogger<CreateTokenCommandHandler>.Instance);
mediator.Setup(x => x.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
    .Returns((CreateTokenCommand command, CancellationToken ct) => tokenHandler.Handle(command, ct));
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var rpcResponse = new TaskCompletionSource<RegisterDeviceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
DateTime? deadline = null;
CancellationToken rpcToken = default;
users.Setup(x => x.RegisterDeviceAsync(It.IsAny<RegisterDeviceRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
    .Callback<RegisterDeviceRequest, Metadata, DateTime?, CancellationToken>((_, _, d, ct) => { deadline = d; rpcToken = ct; entered.TrySetResult(); })
    .Returns(new AsyncUnaryCall<RegisterDeviceResponse>(rpcResponse.Task, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { }));
var sessionConfiguration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["Features:EmailEnabled"] = "false" })
    .Build();
var issuer = new SessionIssuer(users.Object, mediator.Object,
    new NotificationOutbox(sessionContext, sessionConfiguration, sessionMetrics, NullLogger<NotificationOutbox>.Instance),
    sessionRefreshTokens, sessionContext,
    new RequestContext { DeviceId = "audit-device", AppName = "audit", AppVersion = "1" }, location.Object,
    sessionMetrics, NullLogger<SessionIssuer>.Instance);
using var cancellation = new CancellationTokenSource();
var issuance = issuer.IssueAsync(42, cancellation.Token);
await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
cancellation.Cancel();
await Task.Delay(350);
Console.WriteLine($"F19 RegisterDevice: deadlinePresent={deadline.HasValue}; rpcTokenCanCancel={rpcToken.CanBeCanceled}; IssueAsyncCompleteAfterCallerCancel={issuance.IsCompleted}");
if (issuance.IsCompleted || deadline.HasValue || rpcToken.CanBeCanceled) throw new Exception("Expected cancellation/deadline repro changed");
rpcResponse.SetResult(new RegisterDeviceResponse());
await issuance.WaitAsync(TimeSpan.FromSeconds(2));
Console.WriteLine("F19 RegisterDevice: IssueAsync completed only after manual RPC release");

await using (var setup = db.CreateContext())
{
    await new PasswordsStorage(setup).UpdateUserPasswordHash(42, PasswordHasher.HashPassword("audit-old"));
    await setup.Database.ExecuteSqlRawAsync("""
        CREATE FUNCTION reject_pending_notification() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'audit outbox write failure'; END $$;
        CREATE TRIGGER reject_pending_notification BEFORE INSERT ON "PendingNotifications"
        FOR EACH ROW EXECUTE FUNCTION reject_pending_notification();
        """);
}
await using (var context = db.CreateContext())
{
    var metrics = new MetricsCollector();
    var outbox = new NotificationOutbox(context, new ConfigurationBuilder().Build(), metrics, NullLogger<NotificationOutbox>.Instance);
    var handler = new SetPasswordCommandHandler(UserContextFactory.Create(42), new PasswordsStorage(context), new AuthPropertiesStorage(context),
        new RefreshTokensStorage(context, new JwtSettings { ExpiryMinutes = 60 }), new PasswordChangedNotifier(outbox, new RequestContext()),
        metrics, NullLogger<SetPasswordCommandHandler>.Instance, context);
    var enqueueFailurePropagated = false;
    try
    {
        await handler.Handle(new SetPasswordCommand { OldPassword = "audit-old", NewPassword = "audit-new" }, default);
    }
    catch (DbUpdateException)
    {
        enqueueFailurePropagated = true;
    }
    await context.SaveChangesAsync();
    await using var reader = db.CreateContext();
    var oldPasswordPreserved = PasswordHasher.VerifyPassword("audit-old", await new PasswordsStorage(reader).GetUserPasswordHash(42));
    var count = await reader.PendingNotifications.CountAsync();
    var failures = metrics.SnapshotAndReset().GetValueOrDefault("notification_outbox_enqueue_failed");
    Console.WriteLine($"F19-A Outbox PostgreSQL: enqueueFailurePropagated={enqueueFailurePropagated}; oldPasswordPreserved={oldPasswordPreserved}; PendingNotifications={count}; enqueueFailed={failures}");
    if (!enqueueFailurePropagated || !oldPasswordPreserved || count != 0 || failures != 1)
        throw new Exception("Expected atomic rollback on outbox insert failure");
}
Console.WriteLine("F19-A atomic rollback verified; F19-B and F23 diagnostics remain independent observations");
