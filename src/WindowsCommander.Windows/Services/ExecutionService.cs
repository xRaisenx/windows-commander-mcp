using System.Diagnostics;
using System.IO;
using System.Text;
using WindowsCommander.Core.Models;
using WindowsCommander.Core.Services;

namespace WindowsCommander.Windows.Services;

public sealed class ExecutionService : IExecutionService
{
    private static readonly TimeSpan OutputDrainGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OutputDrainCancelGrace = TimeSpan.FromMilliseconds(250);
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

        // Read both pipes continuously while the direct child runs. A detached
        // descendant may inherit those handles and keep them open after the
        // direct child exits, so pipe EOF must never become an unbounded part
        // of request completion.
        using var drainSource = new CancellationTokenSource();
        var stdoutBuffer = new StringBuilder();
        var stderrBuffer = new StringBuilder();
        var stdoutTask = DrainAsync(process.StandardOutput, stdoutBuffer, drainSource.Token);
        var stderrTask = DrainAsync(process.StandardError, stderrBuffer, drainSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
            var (stdout, stderr) = await CompleteOutputCaptureAsync(
                stdoutTask,
                stderrTask,
                stdoutBuffer,
                stderrBuffer,
                drainSource);
            stopwatch.Stop();

            return new CapturedExecution(
                processId,
                new CommandExecutionResult(stdout, stderr, process.ExitCode, stopwatch.Elapsed, TimedOut: false));
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            TryKill(process);
            await WaitForKilledProcessAsync(process);
            var (stdout, stderr) = await CompleteOutputCaptureAsync(
                stdoutTask,
                stderrTask,
                stdoutBuffer,
                stderrBuffer,
                drainSource);
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
            _ = await CompleteOutputCaptureAsync(
                stdoutTask,
                stderrTask,
                stdoutBuffer,
                stderrBuffer,
                drainSource);
            stopwatch.Stop();
            throw;
        }
    }

    private static async Task DrainAsync(
        StreamReader reader,
        StringBuilder destination,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    return;
                }

                lock (destination)
                {
                    destination.Append(buffer, 0, read);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task<(string Stdout, string Stderr)> CompleteOutputCaptureAsync(
        Task stdoutTask,
        Task stderrTask,
        StringBuilder stdoutBuffer,
        StringBuilder stderrBuffer,
        CancellationTokenSource drainSource)
    {
        var drains = Task.WhenAll(stdoutTask, stderrTask);

        try
        {
            await drains.WaitAsync(OutputDrainGrace);
        }
        catch (TimeoutException)
        {
            drainSource.Cancel();

            try
            {
                await drains.WaitAsync(OutputDrainCancelGrace);
            }
            catch (TimeoutException)
            {
                // A detached descendant can keep inherited stdout/stderr handles
                // open even after cancellation. The direct process result is
                // already terminal, so return the captured prefix instead of
                // keeping the MCP request active indefinitely.
            }
        }

        return (Snapshot(stdoutBuffer), Snapshot(stderrBuffer));
    }

    private static string Snapshot(StringBuilder buffer)
    {
        lock (buffer)
        {
            return buffer.ToString();
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

        _ = ChildEnvironmentSanitizer.ApplyTo(startInfo);

        if (environment is not null)
        {
            // Explicit per-call environment values are deliberate and may
            // re-introduce credentials after the inherited environment was
            // scrubbed.
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
