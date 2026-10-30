using System.Net;
using System.Net.Http.Json;

namespace BarkCloud.Files.Infrastructure;

internal sealed class VideoTranscoderClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _token;

    public VideoTranscoderClient(HttpClient httpClient, string baseAddress, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Для обработки видео не задан TRANSCODER_TOKEN.");

        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri(baseAddress.TrimEnd('/') + "/", UriKind.Absolute);
        _token = token;
    }

    public async Task<TranscoderJob?> GetCurrentJobAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "jobs");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadOptionalJobAsync(response, cancellationToken);
    }

    public async Task<TranscoderJob?> GetJobAsync(string id, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"jobs/{Uri.EscapeDataString(id)}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadOptionalJobAsync(response, cancellationToken);
    }

    public async Task<TranscoderJob> StartJobAsync(
        string input,
        string output,
        int height,
        long generation,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "jobs");
        request.Content = JsonContent.Create(new StartTranscoderJob(input, output, height, generation));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TranscoderJob>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Transcoder вернул пустой ответ на запуск задания.");
    }

    public async Task CancelJobAsync(string id, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"jobs/{Uri.EscapeDataString(id)}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;

        response.EnsureSuccessStatusCode();
        var job = await response.Content.ReadFromJsonAsync<TranscoderJob>(cancellationToken: cancellationToken);
        if (job is null || job.Status == "running")
            throw new InvalidOperationException("Transcoder не подтвердил остановку задания.");
    }

    public async Task CancelPriorGenerationJobsAsync(long generation, CancellationToken cancellationToken)
    {
        var job = await GetCurrentJobAsync(cancellationToken);
        if (job?.Status != "running")
            return;

        if (job.Generation > generation)
            throw new InvalidOperationException("Transcoder выполняет задание более нового поколения Files.");

        await CancelJobAsync(job.Id, cancellationToken);
    }

    public async Task CancelJobForGenerationAsync(long generation, CancellationToken cancellationToken)
    {
        var job = await GetCurrentJobAsync(cancellationToken);
        if (job?.Status == "running" && job.Generation == generation)
            await CancelJobAsync(job.Id, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-Transcoder-Token", _token);
        return request;
    }

    private static async Task<TranscoderJob?> ReadOptionalJobAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TranscoderJob>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Transcoder вернул пустое состояние задания.");
    }

    public void Dispose() => _httpClient.Dispose();
}

internal sealed record StartTranscoderJob(string Input, string Output, int Height, long Generation);

internal sealed record TranscoderJob(string Id, long Generation, string Status, int Progress, string? Error);
