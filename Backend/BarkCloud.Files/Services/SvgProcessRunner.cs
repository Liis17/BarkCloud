using System.Diagnostics;
using System.Text;

namespace BarkCloud.Files.Services;

public sealed record SvgProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    byte[] InputBytes,
    int MaximumOutputBytes,
    TimeSpan Timeout);

public interface ISvgProcessRunner
{
    Task<byte[]> RunAsync(SvgProcessRequest request, CancellationToken cancellationToken);
}

/// <summary>Запускает процесс без shell, с потоками stdin/stdout и завершением всего дерева.</summary>
public sealed class SvgProcessRunner : ISvgProcessRunner
{
    private const int MaximumCapturedErrorCharacters = 4096;

    public async Task<byte[]> RunAsync(SvgProcessRequest request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Clear();
        foreach (var (key, value) in request.Environment)
            startInfo.Environment[key] = value;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить SVG-рендерер.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        using var killRegistration = timeout.Token.Register(() => TryKill(process));

        var inputTask = WriteInputAsync(process, request.InputBytes, timeout.Token);
        var outputTask = ReadOutputAsync(process, request.MaximumOutputBytes, timeout.Token);
        var errorTask = DrainErrorAsync(process.StandardError, timeout.Token);
        var exitTask = process.WaitForExitAsync(timeout.Token);

        try
        {
            var output = await outputTask;
            var error = await errorTask;
            await Task.WhenAll(inputTask, exitTask);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"SVG-рендерер завершился с кодом {process.ExitCode}: {error}");

            return output;
        }
        catch
        {
            TryKill(process);
            await ReapAsync(process);
            await ObserveFailureAsync(inputTask, outputTask, errorTask, exitTask);

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw;
        }
    }

    private static async Task WriteInputAsync(Process process, byte[] input, CancellationToken cancellationToken)
    {
        await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        process.StandardInput.Close();
    }

    private static async Task<byte[]> ReadOutputAsync(Process process, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[8192];
        while (true)
        {
            var remaining = maximumBytes - (int)output.Length;
            var read = await process.StandardOutput.BaseStream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), cancellationToken);
            if (read == 0)
                return output.ToArray();
            if (read > remaining)
            {
                TryKill(process);
                throw new InvalidDataException("SVG-рендерер превысил лимит размера PNG.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task<string> DrainErrorAsync(StreamReader error, CancellationToken cancellationToken)
    {
        var captured = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var read = await error.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                return captured.ToString();

            if (captured.Length < MaximumCapturedErrorCharacters)
                captured.Append(buffer, 0, Math.Min(read, MaximumCapturedErrorCharacters - captured.Length));
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task ReapAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task ObserveFailureAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
        }
    }
}
