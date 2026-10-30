using System.Security.Cryptography;
using System.Text;
using BarkCloud.Transcoder;

var builder = WebApplication.CreateBuilder(args);
var token = builder.Configuration["TRANSCODER_TOKEN"];
if (string.IsNullOrWhiteSpace(token))
{
    throw new InvalidOperationException("Не задан обязательный TRANSCODER_TOKEN.");
}

var threadsValue = builder.Configuration["TRANSCODER_THREADS"];
var threads = string.IsNullOrWhiteSpace(threadsValue)
    ? 2
    : int.TryParse(threadsValue, out var parsedThreads) && parsedThreads is >= 1 and <= 64
        ? parsedThreads
        : throw new InvalidOperationException("TRANSCODER_THREADS должен быть числом от 1 до 64.");

builder.Services.AddSingleton(new TranscodePaths("/mnt/transcode"));
builder.Services.AddSingleton<ITranscoderProcessRunner>(new FfmpegProcessRunner(threads));
builder.Services.AddSingleton<TranscodeJobManager>();

var app = builder.Build();
var expectedToken = Encoding.UTF8.GetBytes(token);
app.Use(async (context, next) =>
{
    var providedToken = context.Request.Headers["X-Transcoder-Token"].ToString();
    var providedBytes = Encoding.UTF8.GetBytes(providedToken);
    if (providedBytes.Length != expectedToken.Length
        || !CryptographicOperations.FixedTimeEquals(providedBytes, expectedToken))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next();
});

app.MapPost("/jobs", (StartTranscodeRequest request, TranscodeJobManager manager) =>
{
    try
    {
        var result = manager.Start(request);
        return result.Conflict
            ? Results.Conflict()
            : Results.Accepted($"/jobs/{result.Job!.Id}", result.Job);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (FileNotFoundException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

app.MapGet("/jobs/{id}", (string id, TranscodeJobManager manager) =>
    manager.Get(id) is { } job ? Results.Ok(job) : Results.NotFound());

app.MapGet("/jobs", (TranscodeJobManager manager) =>
    manager.GetCurrent() is { } job ? Results.Ok(job) : Results.NotFound());

app.MapDelete("/jobs/{id}", async (string id, TranscodeJobManager manager) =>
    await manager.CancelAsync(id) is { } job ? Results.Ok(job) : Results.NotFound());

app.Run();

public partial class Program;
