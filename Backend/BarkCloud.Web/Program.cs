using BarkCloud.GrpcServer;
using BarkCloud.Proto.Files;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Auth;
using BarkCloud.Shared.Identity;
using BarkCloud.Web;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Endpoints;
using BarkCloud.Web.Infrastructure;
using BarkCloud.Web.Rendering;

using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;

// gRPC к микросервисам идёт по docker-сети без TLS (h2c) — разрешаем HTTP/2 поверх http://
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

// JwtSettings и адреса микросервисов берутся из Configuration-сервиса (как у остальных сервисов).
// На холодном старте Configuration может быть ещё не готов — ждём его с повторами,
// чтобы не уходить в краш-луп по docker restart.
for (var attempt = 1; ; attempt++)
{
    try
    {
        builder.LoadConfiguration(ServiceId.Web);
        break;
    }
    catch (Exception ex) when (attempt < 30)
    {
        Console.Error.WriteLine($"[startup] Configuration-сервис недоступен (попытка {attempt}): {ex.Message}. Повтор через 2с.");
        Thread.Sleep(2000);
    }
}

// Адреса сервисов в docker-сети. Первичный источник — Configuration
// (IdentityService:Host / UsersService:Host / FilesService:Host).
// Внутренний порт сервиса = его RunSettings:Port в Configuration-БД; оператор задаёт
// его через .env (контейнер web получает .env через env_file), поэтому fallback строим
// из USERS_PORT/FILES_PORT, а не из захардкоженных значений. Identity слушает 7000
// (его IDENTITY_PORT из .env — это только host-маппинг nginx). См. nginx/cloud.barkfluff.conf.
static string EnvPort(string name, int fallback)
    => int.TryParse(Environment.GetEnvironmentVariable(name), out var p) && p > 0 ? p.ToString() : fallback.ToString();

var identityAddress = builder.Configuration["IdentityService:Host"] ?? "http://cloud-identity:7000";
var usersAddress = builder.Configuration["UsersService:Host"] ?? $"http://cloud-users:{EnvPort("USERS_PORT", 7001)}";
var filesAddress = builder.Configuration["FilesService:Host"] ?? $"http://cloud-files:{EnvPort("FILES_PORT", 7005)}";

// Внутренний HTTP1-эндпоинт Files (тот же хост, что и gRPC, но порт Http1Port) для прокси-загрузки
// байтов внутри docker-сети — минуя nginx/TLS и зависимость от ExternalEndpoint:Host.
builder.Configuration["FilesService:Http1Base"] = $"http://{new Uri(filesAddress).Host}:{EnvPort("FILES_HTTP1PORT", 7026)}";

builder.Services.AddGrpcClient<IdentityApi.IdentityApiClient>(o => o.Address = new Uri(identityAddress));
builder.Services.AddGrpcClient<UsersApi.UsersApiClient>(o => o.Address = new Uri(usersAddress));
builder.Services.AddGrpcClient<FilesApi.FilesApiClient>(o => o.Address = new Uri(filesAddress));
builder.Services.AddGrpcClient<CloudApi.CloudApiClient>(o => o.Address = new Uri(filesAddress));
builder.Services.AddGrpcClient<AlbumApi.AlbumApiClient>(o => o.Address = new Uri(filesAddress));

// HttpClient для прокси-загрузки байтов в Files (на внутренний HTTP1-эндпоинт).
builder.Services.AddHttpClient("files-upload");

// Загрузка файлов до 512 МБ: снимаем дефолтные лимиты тела запроса и multipart-формы.
const long maxUpload = 536_870_912; // 512 МБ
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxUpload);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUpload);

// Серверные (inter-service) API. Авторизуются сервисным токеном, который Web
// подписывает общим JWT-секретом (как Configuration): проверка занятости юзернейма/почты
// при регистрации (Users) и загрузка аватара (Files).
var serviceToken = ServiceToken.Generate(builder.Configuration);

builder.Services.AddGrpcClient<UsersServerApi.UsersServerApiClient>(o => o.Address = new Uri(usersAddress))
    .AddInterceptor(() => new JwtClientInterceptor(serviceToken));
builder.Services.AddGrpcClient<FilesServerApi.FilesServerApiClient>(o => o.Address = new Uri(filesAddress))
    .AddInterceptor(() => new JwtClientInterceptor(serviceToken));

builder.Services.AddSingleton<TemplateRenderer>();
builder.Services.AddSingleton<PageService>();
builder.Services.AddSingleton<AdminGate>();
builder.Services.AddSingleton<DockerService>();
builder.Services.AddScoped<AuthGateway>();
builder.Services.AddScoped<RegistrationGateway>();
builder.Services.AddScoped<PasswordResetGateway>();
builder.Services.AddScoped<PageDataBuilder>();

var app = builder.Build();

// За nginx TLS терминируется снаружи, поэтому Kestrel видит scheme=http.
// Доверяем X-Forwarded-Proto/Host от reverse-proxy, чтобы Request.Scheme был корректным
// (используется при сборке публичного URL ссылок /s/{token}).
var fhOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
};
// nginx в docker-сети — не loopback; доверяем proxy из любой сети (веб не открыт наружу напрямую).
fhOptions.KnownNetworks.Clear();
fhOptions.KnownProxies.Clear();
app.UseForwardedHeaders(fhOptions);

// Бандл React-SPA из wwwroot (собирается Vite из ClientApp). Отдаём статику до эндпоинтов.
app.UseStaticFiles();

app.MapWebEndpoints();
app.MapCloudApiEndpoints();
app.MapSystemEndpoints();
app.MapSettingsEndpoints();

// SPA-fallback: любой неизвестный путь (кроме /api/*) отдаёт index.html SPA.
// Неавторизованный заход/refresh на маршрут приложения → редирект на серверный /login
// (AuthGateway также обновляет access-токен по refresh-cookie).
app.MapFallback(async (HttpContext http, AuthGateway auth) =>
{
    if (http.Request.Path.StartsWithSegments("/api"))
        return Results.NotFound();

    if (await auth.AuthenticateAsync(http) is null)
        return Results.Redirect("/login");

    var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    return index.Exists && index.PhysicalPath is not null
        ? Results.File(index.PhysicalPath, "text/html; charset=utf-8")
        : Results.NotFound();
});

app.Run();
