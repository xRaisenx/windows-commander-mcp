using System.Diagnostics;
using WindowsCommander.Core.Models;
using WindowsCommander.Core.Services;

namespace WindowsCommander.Windows.Services;

public sealed class ExecutionService : IExecutionService
{
    public async Task<CommandExecutionResult> ExecutePowerShellAsync(
        string command,
        string? workingDirectory,
        int? timeoutMs,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("PowerShell command must not be empty.", nameof(command));
        }

        var capture = await ExecuteAndCaptureAsync(
            "pwsh.exe",
            new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command },
            workingDirectory,
            timeoutMs,
            environment,
            cancellationToken);

        return capture.Result;
    }

    public async Task<ProcessStartResult> ExecuteProcessAsync(
        string executablePath,
        IReadOnlyList<string>? arguments,
        string? workingDirectory,
        int? timeoutMs,
        bool waitForExit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Executable path must not be empty.", nameof(executablePath));
        }

        if (!waitForExit)
        {
            using var process = StartProcess(
                executablePath,
                arguments ?? Array.Empty<string>(),
                workingDirectory,
                null,
                redirectOutput: false);

            return new ProcessStartResult(process.Id, null, null, null, null, TimedOut: false);
        }

        var capture = await ExecuteAndCaptureAsync(
            executablePath,
            arguments ?? Array.Empty<string>(),
            workingDirectory,
            timeoutMs,
            null,
            cancellationToken);

        return new ProcessStartResult(
            capture.ProcessId,
            capture.Result.StandardOutput,
            capture.Result.StandardError,
            capture.Result.ExitCode,
            capture.Result.ElapsedTime,
            capture.Result.TimedOut);
    }

    private static async Task<CapturedExecution> ExecuteAndCaptureAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        int? timeoutMs,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeoutSource = timeoutMs is > 0
            ? new CancellationTokenSource(timeoutMs.Value)
            : new CancellationTokenSource();
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var process = StartProcess(executablePath, arguments, workingDirectory, environment, redirectOutput: true);
        var processId = process.Id;

        // Do not cancel the stream drains when the operation times out. Killing
        // the owned process closes stdout/stderr, allowing us to preserve every
        // byte the child produced before termination instead of returning empty
        // evidence on the most important failure path.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            stopwatch.Stop();

            return new CapturedExecution(
                processId,
                new CommandExecutionResult(stdout, stderr, process.ExitCode, stopwatch.Elapsed, TimedOut: false));
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            TryKill(process);
            await WaitForKilledProcessAsync(process);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            stopwatch.Stop();

            var timeoutMessage = string.IsNullOrWhiteSpace(stderr)
                ? "Process timed out."
                : $"{stderr.TrimEnd()}{Environment.NewLine}Process timed out.";

            return new CapturedExecution(
                processId,
                new CommandExecutionResult(stdout, timeoutMessage, null, stopwatch.Elapsed, TimedOut: true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await WaitForKilledProcessAsync(process);
            _ = await stdoutTask;
            _ = await stderrTask;
            stopwatch.Stop();
            throw;
        }
    }

    private static Process StartProcess(
        string executablePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        bool redirectOutput)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start process: {executablePath}");
    }

    private static async Task WaitForKilledProcessAsync(Process process)
    {
        try
        {
            using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(settle.Token);
        }
        catch (OperationCanceledException)
        {
            // The caller already has a timeout/cancellation outcome. Do not turn
            // a stubborn process teardown into an unbounded secondary wait.
        }
        catch (InvalidOperationException)
        {
            // The process may have exited between the kill and this wait.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Failed to kill process: {ex.Message}");
        }
    }

    private sealed record CapturedExecution(int ProcessId, CommandExecutionResult Result);
}
