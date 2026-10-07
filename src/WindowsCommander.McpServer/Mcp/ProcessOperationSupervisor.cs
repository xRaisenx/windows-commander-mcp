using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace WindowsCommander.McpServer.Mcp;

public sealed class ProcessOperationSupervisor
{
    private const int DefaultMaxOutputBytes = 512 * 1024;
    private const int MaximumMaxOutputBytes = 4 * 1024 * 1024;
    private const int MaximumRetainedOperations = 256;
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, ManagedOperation> operations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim executionSlots;

    public ProcessOperationSupervisor(int maxConcurrentProcesses = 2)
    {
        var limit = Math.Clamp(maxConcurrentProcesses, 1, 8);
        executionSlots = new SemaphoreSlim(limit, limit);
    }

    public ManagedOperationSnapshot Start(
        string executablePath,
        IReadOnlyList<string>? arguments,
        string? workingDirectory,
        int? timeoutMs,
        int? maxOutputBytes)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Executable path must not be empty.", nameof(executablePath));
        }

        CleanupExpired();

        var operation = new ManagedOperation(
            Guid.NewGuid().ToString("N"),
            executablePath,
            arguments ?? Array.Empty<string>(),
            workingDirectory,
            Math.Clamp(timeoutMs ?? 300_000, 1_000, 86_400_000),
            Math.Clamp(maxOutputBytes ?? DefaultMaxOutputBytes, 64 * 1024, MaximumMaxOutputBytes));

        if (!operations.TryAdd(operation.Id, operation))
        {
            throw new InvalidOperationException("Failed to allocate process operation id.");
        }

        _ = RunAsync(operation);
        return operation.Snapshot();
    }

    public ManagedOperationSnapshot Get(string operationId)
    {
        if (!operations.TryGetValue(operationId, out var operation))
        {
            throw new ArgumentException($"Unknown process operation: {operationId}", nameof(operationId));
        }

        return operation.Snapshot();
    }

    public ManagedOperationSnapshot Cancel(string operationId)
    {
        if (!operations.TryGetValue(operationId, out var operation))
        {
            throw new ArgumentException($"Unknown process operation: {operationId}", nameof(operationId));
        }

        operation.Cancel();
        return operation.Snapshot();
    }

    private async Task RunAsync(ManagedOperation operation)
    {
        var acquired = false;
        try
        {
            await executionSlots.WaitAsync(operation.CancellationToken);
            acquired = true;
            operation.MarkStarting();

            var startInfo = new ProcessStartInfo(operation.ExecutablePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var argument in operation.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (!string.IsNullOrWhiteSpace(operation.WorkingDirectory))
            {
                startInfo.WorkingDirectory = operation.WorkingDirectory;
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start process: {operation.ExecutablePath}");

            operation.MarkRunning(process);

            var stdoutTask = PumpAsync(process.StandardOutput, operation.Stdout);
            var stderrTask = PumpAsync(process.StandardError, operation.Stderr);

            using var timeout = new CancellationTokenSource(operation.TimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                operation.CancellationToken,
                timeout.Token);

            try
            {
                await process.WaitForExitAsync(linked.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
                operation.MarkCompleted(process.ExitCode);
            }
            catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                await WaitForExitBoundedAsync(process);
                await DrainBoundedAsync(stdoutTask, stderrTask);
                operation.MarkCancelled();
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                TryKill(process);
                await WaitForExitBoundedAsync(process);
                await DrainBoundedAsync(stdoutTask, stderrTask);
                operation.MarkTimedOut();
            }
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
            operation.MarkCancelled();
        }
        catch (Exception exception)
        {
            operation.MarkFailed(exception);
        }
        finally
        {
            if (acquired)
            {
                executionSlots.Release();
            }
        }
    }

    private static async Task PumpAsync(StreamReader reader, BoundedTextBuffer destination)
    {
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length));
            if (read == 0)
            {
                return;
            }

            destination.Append(buffer.AsSpan(0, read));
        }
    }

    private static async Task DrainBoundedAsync(params Task[] tasks)
    {
        try
        {
            using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Task.WhenAll(tasks).WaitAsync(settle.Token);
        }
        catch
        {
            // Output pumps are evidence helpers; teardown must remain bounded.
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
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task WaitForExitBoundedAsync(Process process)
    {
        try
        {
            using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(settle.Token);
        }
        catch
        {
        }
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in operations)
        {
            if (!pair.Value.IsTerminal)
            {
                continue;
            }

            var completedAt = pair.Value.CompletedAt;
            if (completedAt is not null && now - completedAt.Value > Retention)
            {
                operations.TryRemove(pair.Key, out _);
            }
        }

        if (operations.Count <= MaximumRetainedOperations)
        {
            return;
        }

        foreach (var candidate in operations.Values
                     .Where(operation => operation.IsTerminal)
                     .OrderBy(operation => operation.CompletedAt)
                     .Take(operations.Count - MaximumRetainedOperations))
        {
            operations.TryRemove(candidate.Id, out _);
        }
    }

    private sealed class ManagedOperation
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource cancellation = new();
        private readonly Stopwatch elapsed = new();
        private string state = "QUEUED";
        private int? processId;
        private int? exitCode;
        private string? error;
        private DateTimeOffset? startedAt;
        private DateTimeOffset? completedAt;

        public ManagedOperation(
            string id,
            string executablePath,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            int timeoutMs,
            int maxOutputBytes)
        {
            Id = id;
            ExecutablePath = executablePath;
            Arguments = arguments;
            WorkingDirectory = workingDirectory;
            TimeoutMs = timeoutMs;
            Stdout = new BoundedTextBuffer(maxOutputBytes / 2);
            Stderr = new BoundedTextBuffer(maxOutputBytes / 2);
            CreatedAt = DateTimeOffset.UtcNow;
        }

        public string Id { get; }
        public string ExecutablePath { get; }
        public IReadOnlyList<string> Arguments { get; }
        public string? WorkingDirectory { get; }
        public int TimeoutMs { get; }
        public DateTimeOffset CreatedAt { get; }
        public BoundedTextBuffer Stdout { get; }
        public BoundedTextBuffer Stderr { get; }
        public CancellationToken CancellationToken => cancellation.Token;

        public bool IsTerminal
        {
            get
            {
                lock (gate)
                {
                    return state is "SUCCEEDED" or "FAILED" or "CANCELLED" or "TIMED_OUT";
                }
            }
        }

        public DateTimeOffset? CompletedAt
        {
            get
            {
                lock (gate)
                {
                    return completedAt;
                }
            }
        }

        public void MarkStarting()
        {
            lock (gate)
            {
                if (state == "CANCELLED")
                {
                    return;
                }

                state = "STARTING";
            }
        }

        public void MarkRunning(Process process)
        {
            lock (gate)
            {
                processId = process.Id;
                startedAt = DateTimeOffset.UtcNow;
                elapsed.Start();
                state = "RUNNING";
            }
        }

        public void MarkCompleted(int code)
        {
            lock (gate)
            {
                elapsed.Stop();
                exitCode = code;
                completedAt = DateTimeOffset.UtcNow;
                state = code == 0 ? "SUCCEEDED" : "FAILED";
                if (code != 0)
                {
                    error = $"Process exited with code {code}.";
                }
            }
        }

        public void MarkCancelled()
        {
            lock (gate)
            {
                elapsed.Stop();
                completedAt ??= DateTimeOffset.UtcNow;
                state = "CANCELLED";
            }
        }

        public void MarkTimedOut()
        {
            lock (gate)
            {
                elapsed.Stop();
                completedAt = DateTimeOffset.UtcNow;
                state = "TIMED_OUT";
                error = $"Process exceeded timeout of {TimeoutMs} ms.";
            }
        }

        public void MarkFailed(Exception exception)
        {
            lock (gate)
            {
                elapsed.Stop();
                completedAt = DateTimeOffset.UtcNow;
                state = "FAILED";
                error = exception.Message;
            }
        }

        public void Cancel()
        {
            cancellation.Cancel();
            lock (gate)
            {
                if (state is "QUEUED" or "STARTING")
                {
                    state = "CANCELLED";
                    completedAt ??= DateTimeOffset.UtcNow;
                }
            }
        }

        public ManagedOperationSnapshot Snapshot()
        {
            lock (gate)
            {
                return new ManagedOperationSnapshot(
                    Id,
                    ExecutablePath,
                    state,
                    processId,
                    CreatedAt,
                    startedAt,
                    completedAt,
                    elapsed.ElapsedMilliseconds,
                    TimeoutMs,
                    exitCode,
                    Stdout.ToString(),
                    Stderr.ToString(),
                    Stdout.Truncated,
                    Stderr.Truncated,
                    error);
            }
        }
    }

    internal sealed class BoundedTextBuffer
    {
        private readonly object gate = new();
        private readonly StringBuilder buffer = new();
        private readonly int maxChars;
        private bool truncated;

        public BoundedTextBuffer(int maxBytes)
        {
            // UTF-16 StringBuilder storage can be larger than UTF-8. A strict
            // character cap keeps memory bounded independent of output content.
            maxChars = Math.Max(1024, maxBytes / 2);
        }

        public bool Truncated
        {
            get
            {
                lock (gate)
                {
                    return truncated;
                }
            }
        }

        public void Append(ReadOnlySpan<char> value)
        {
            lock (gate)
            {
                if (buffer.Length >= maxChars)
                {
                    truncated = true;
                    return;
                }

                var remaining = maxChars - buffer.Length;
                if (value.Length > remaining)
                {
                    buffer.Append(value[..remaining]);
                    truncated = true;
                    return;
                }

                buffer.Append(value);
            }
        }

        public override string ToString()
        {
            lock (gate)
            {
                return buffer.ToString();
            }
        }
    }
}

public sealed record ManagedOperationSnapshot(
    string OperationId,
    string ExecutablePath,
    string State,
    int? ProcessId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long ElapsedMs,
    int TimeoutMs,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool StdoutTruncated,
    bool StderrTruncated,
    string? Error);
