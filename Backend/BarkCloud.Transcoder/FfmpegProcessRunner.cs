using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace BarkCloud.Transcoder;

public sealed class FfmpegProcessRunner(int threads) : ITranscoderProcessRunner
{
    public async Task RunAsync(
        string inputPath,
        string outputPath,
        int height,
        Action<int> reportProgress,
        CancellationToken cancellationToken)
    {
        var duration = await ProbeDurationAsync(inputPath, cancellationToken);
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in FfmpegCommand.BuildArguments(inputPath, outputPath, height, threads))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start())
        {
            throw new InvalidOperationException("Не удалось запустить ffmpeg.");
        }

        var stderrTask = ReadTailAsync(process.StandardError);
        var progressTask = ReadProgressAsync(process.StandardOutput, duration, reportProgress, cancellationToken);
        try
        {
            await WaitForExitAsync(process, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await Task.WhenAll(progressTask, stderrTask);
            }
            catch
            {
            }

            throw;
        }

        await Task.WhenAll(progressTask, stderrTask);

        if (process.ExitCode != 0)
        {
            var error = stderrTask.Result.Trim();
            if (error.Length > 1000)
            {
                error = error[^1000..];
            }

            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"ffmpeg завершился с кодом {process.ExitCode}."
                : error);
        }
    }

    private static async Task<double> ProbeDurationAsync(string inputPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-v", "error", "-show_entries", "format=duration", "-of",
                     "default=noprint_wrappers=1:nokey=1", inputPath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start())
        {
            throw new InvalidOperationException("Не удалось запустить ffprobe.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = ReadTailAsync(process.StandardError);
        try
        {
            await WaitForExitAsync(process, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch
            {
            }

            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(stderrTask.Result.Trim());
        }

        return double.TryParse(stdoutTask.Result.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            ? duration
            : 0;
    }

    private static async Task ReadProgressAsync(
        StreamReader stdout,
        double duration,
        Action<int> reportProgress,
        CancellationToken cancellationToken)
    {
        while (await stdout.ReadLineAsync(cancellationToken) is { } line)
        {
            if (duration <= 0)
            {
                continue;
            }

            const string microsecondPrefix = "out_time_us=";
            const string millisecondPrefix = "out_time_ms=";
            var prefixLength = line.StartsWith(microsecondPrefix, StringComparison.Ordinal)
                ? microsecondPrefix.Length
                : line.StartsWith(millisecondPrefix, StringComparison.Ordinal)
                    ? millisecondPrefix.Length
                    : 0;
            if (prefixLength > 0
                && long.TryParse(line.AsSpan(prefixLength), CultureInfo.InvariantCulture, out var microseconds))
            {
                reportProgress(Math.Clamp((int)(microseconds / (duration * 10_000)), 0, 99));
            }
        }
    }

    private static async Task<string> ReadTailAsync(StreamReader reader)
    {
        const int tailSize = 4096;
        var tail = new char[tailSize];
        var buffer = new char[1024];
        var totalRead = 0L;
        var tailPosition = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                tail[tailPosition] = buffer[index];
                tailPosition = (tailPosition + 1) % tailSize;
            }

            totalRead += read;
        }

        var resultLength = (int)Math.Min(totalRead, tailSize);
        var resultStart = (tailPosition - resultLength + tailSize) % tailSize;
        var result = new StringBuilder(resultLength);
        for (var index = 0; index < resultLength; index++)
        {
            result.Append(tail[(resultStart + index) % tailSize]);
        }

        return result.ToString();
    }

    private static async Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync();
            throw;
        }
    }
}
