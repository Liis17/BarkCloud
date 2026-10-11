using System.Net;
using System.Reflection;
using System.Text;
using BarkCloud.Proto.Files;
using BarkCloud.Web;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;
using BarkCloud.Web.Rendering;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:18941");
builder.Logging.ClearProviders();
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
    ["FilesService:Http1Base"] = "http://stub-files.invalid",
    ["JwtSettings:SecretKey"] = "audit-only-signing-key-with-at-least-32-bytes",
    ["App:AdminPassword"] = "audit-only-password"
});
builder.Services.AddTransient<AuthGateway>();
builder.Services.AddTransient<PageService>();
builder.Services.AddTransient<FeatureConfigurationGateway>();
builder.Services.AddTransient<RegistrationGateway>();
builder.Services.AddTransient<PasswordResetGateway>();
builder.Services.AddTransient<FilesServerApi.FilesServerApiClient>(_ => throw new InvalidOperationException("No real gRPC calls allowed"));
var upstream = new Upstream();
builder.Services.AddSingleton<IHttpClientFactory>(new Factory(upstream));
await using var app = builder.Build();
app.MapWebEndpoints();
app.MapGet("/audit-marker", () => Results.Text("same-origin-read-succeeded"));
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:18941") };
var id = "00000000-0000-0000-0000-000000000001";
var svgPath = "/api/files/image?url=" + Uri.EscapeDataString("http://irrelevant.example/download/" + id);
using (var svg = await client.GetAsync(svgPath))
    Console.WriteLine($"SVG_PROXY status={(int)svg.StatusCode}; type={svg.Content.Headers.ContentType}; disposition={svg.Content.Headers.ContentDisposition}; csp={svg.Headers.Contains("Content-Security-Policy")}; script={(await svg.Content.ReadAsStringAsync()).Contains("<script>")}");

var validator = typeof(WebEndpoints).GetMethod("TryValidateIconProxyUrl", BindingFlags.NonPublic | BindingFlags.Static)!;
var context = new DefaultHttpContext();
context.Request.Host = new HostString("169.254.169.254");
var validationArgs = new object?[] { context, builder.Configuration, "http://169.254.169.254/download/probe", null };
Console.WriteLine($"ICON_HOST accepted={validator.Invoke(null, validationArgs)}; target={validationArgs[3]}");

upstream.Large = true;
using (var large = await client.GetAsync("/api/head/icon?url=" + Uri.EscapeDataString("http://localhost/download/" + id)))
    Console.WriteLine($"ICON_LIMIT status={(int)large.StatusCode}; serializedBytes={upstream.SerializedBytes}");
upstream.Large = false;

var gate = new AdminGate(builder.Configuration);
var unlock = new DefaultHttpContext();
gate.Unlock(unlock, "audit-only-password");
var cookie = unlock.Response.Headers.SetCookie.ToString().Split(';')[0];
var afterLogout = new DefaultHttpContext();
afterLogout.Request.Headers.Cookie = cookie;
Console.WriteLine($"ADMIN_UNBOUND withoutAnyUserSession={gate.IsUnlocked(afterLogout)}");
afterLogout.Request.Headers.Cookie = "bark_admin=9223372036854775807.invalid";
try { Console.WriteLine($"ADMIN_MALFORMED result={gate.IsUnlocked(afterLogout)}"); }
catch (Exception error) { Console.WriteLine($"ADMIN_MALFORMED throws={error.GetType().Name}"); }

var form = new DefaultHttpContext();
var boundary = "audit-boundary";
using var multipart = new MultipartFormDataContent(boundary);
multipart.Add(new ByteArrayContent(new byte[8 * 1024 * 1024]), "ignored-file", "ignored.bin");
form.Request.ContentType = "multipart/form-data; boundary=" + boundary;
form.Request.Body = new MemoryStream(await multipart.ReadAsByteArrayAsync());
form.Features.Set<IFormFeature>(new FormFeature(form.Request, new FormOptions { MultipartBodyLengthLimit = long.MaxValue }));
Console.WriteLine($"FORM_BUFFER acceptedIgnoredFileBytes={(await form.Request.ReadFormAsync()).Files.Single().Length}");
Console.WriteLine($"BROWSER_URL={client.BaseAddress!.ToString().TrimEnd('/')}{svgPath}");
if (args.Contains("--serve")) await app.WaitForShutdownAsync();

sealed class Factory(Upstream upstream) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(upstream, disposeHandler: false);
}
sealed class Upstream : HttpMessageHandler
{
    public bool Large { get; set; }
    public long SerializedBytes { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpContent content = Large ? new CountingContent(this) : new StringContent("<svg xmlns='http://www.w3.org/2000/svg'><script>fetch('/audit-marker').then(r=>r.text()).then(t=>{document.documentElement.setAttribute('data-audit',t);document.title='audit-script-executed';})</script></svg>", Encoding.UTF8, "image/svg+xml");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
sealed class CountingContent : HttpContent
{
    private readonly Upstream _owner;
    public CountingContent(Upstream owner) { _owner = owner; Headers.ContentType = new("image/png"); }
    protected override bool TryComputeLength(out long length) { length = 3 * 1024 * 1024; return true; }
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var chunk = new byte[64 * 1024];
        for (var i = 0; i < 48; i++) { await stream.WriteAsync(chunk); _owner.SerializedBytes += chunk.Length; }
    }
}
