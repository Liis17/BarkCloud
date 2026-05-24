using BarkCloud.Proto.Files;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Web;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;
using BarkCloud.Web.Rendering;

// gRPC к микросервисам идёт по docker-сети без TLS (h2c) — разрешаем HTTP/2 поверх http://
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

var identityAddress = builder.Configuration["Backend:Identity"] ?? "http://cloud-identity:7020";
var usersAddress = builder.Configuration["Backend:Users"] ?? "http://cloud-users:7021";
var filesAddress = builder.Configuration["Backend:Files"] ?? "http://cloud-files:7025";

builder.Services.AddGrpcClient<IdentityApi.IdentityApiClient>(o => o.Address = new Uri(identityAddress));
builder.Services.AddGrpcClient<UsersApi.UsersApiClient>(o => o.Address = new Uri(usersAddress));
builder.Services.AddGrpcClient<FilesApi.FilesApiClient>(o => o.Address = new Uri(filesAddress));
builder.Services.AddGrpcClient<CloudApi.CloudApiClient>(o => o.Address = new Uri(filesAddress));

builder.Services.AddSingleton<TemplateRenderer>();
builder.Services.AddSingleton<PageService>();
builder.Services.AddScoped<AuthGateway>();
builder.Services.AddScoped<PageDataBuilder>();

var app = builder.Build();

app.MapWebEndpoints();

app.Run();
