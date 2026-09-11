using System.Diagnostics;

namespace tdtd_be.IntegrationTests;

internal sealed class ManagedChildProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;

    private ManagedChildProcess(Process process, Task stdoutPump, Task stderrPump)
    {
        _process = process;
        _stdoutPump = stdoutPump;
        _stderrPump = stderrPump;
    }

    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public static ManagedChildProcess Start(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        string stdoutPath,
        string stderrPath,
        Func<string, string>? stdoutLineSanitizer = null,
        Func<string, string>? stderrLineSanitizer = null)
    {
        Directory.CreateDirectory(workingDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var pair in environment)
        {
            if (pair.Value is null)
                startInfo.Environment.Remove(pair.Key);
            else
                startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {executable}.");

        var stdoutPump = PumpAsync(
            process.StandardOutput,
            stdoutPath,
            stdoutLineSanitizer);
        var stderrPump = PumpAsync(
            process.StandardError,
            stderrPath,
            stderrLineSanitizer);
        return new ManagedChildProcess(process, stdoutPump, stderrPump);
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process exited between the check and kill.
            }
        }

        if (!_process.HasExited)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            try
            {
                await _process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
        }

        await WaitForPumpsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(10));
        _process.Dispose();
    }

    private async Task WaitForPumpsAsync()
    {
        try
        {
            await Task.WhenAll(_stdoutPump, _stderrPump).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Logs are best-effort after the exact child process has stopped.
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        string path,
        Func<string, string>? lineSanitizer)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var writer = new StreamWriter(path, append: false) { AutoFlush = true };
        while (await reader.ReadLineAsync() is { } line)
        {
            var durableLine = lineSanitizer is null
                ? line
                : lineSanitizer(line) ?? throw new InvalidOperationException(
                    "A managed child-process log sanitizer returned null.");
            await writer.WriteLineAsync(durableLine);
        }
    }
}

internal static class LogTail
{
    public static string Read(string path, int maxLines = 80)
    {
        if (!File.Exists(path))
            return "<missing log>";
        maxLines = Math.Max(1, maxLines);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var lines = new Queue<string>(maxLines);
                while (reader.ReadLine() is { } line)
                {
                    if (lines.Count == maxLines)
                        lines.Dequeue();
                    lines.Enqueue(line);
                }

                return string.Join(Environment.NewLine, lines);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt));
            }
            catch (IOException error)
            {
                return $"<log unavailable: {error.GetType().Name}>";
            }
        }

        return "<log unavailable>";
    }
}
