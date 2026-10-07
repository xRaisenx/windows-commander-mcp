using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;

namespace WindowsCommander.McpServer.Mcp;

internal sealed class RescueConsole : IAsyncDisposable
{
    private const long RotateBytes = 8L * 1024L * 1024L;

    private readonly object gate = new();
    private readonly bool consoleEnabled;
    private readonly bool colorEnabled;
    private readonly Channel<ActivityEvent> events;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task writerTask;
    private int active;
    private int queued;

    public RescueConsole()
    {
        consoleEnabled = !Console.IsErrorRedirected || string.Equals(
            Environment.GetEnvironmentVariable("WINDOWS_COMMANDER_ACTIVITY_CONSOLE"), "1", StringComparison.OrdinalIgnoreCase);
        colorEnabled = consoleEnabled && !Console.IsErrorRedirected && !string.Equals(
            Environment.GetEnvironmentVariable("NO_COLOR"), "1", StringComparison.OrdinalIgnoreCase);

        EventLogPath = ResolveEventLogPath();
        events = Channel.CreateBounded<ActivityEvent>(new BoundedChannelOptions(2048)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        writerTask = Task.Run(WriteEventsAsync);
    }

    public string EventLogPath { get; }

    public ActivityScope Queued(string lane, string operation, string? target = null)
    {
        Interlocked.Increment(ref queued);
        Write("BLUE", "QUEUED", lane, operation, target, 0);
        return new ActivityScope(this, lane, operation, target);
    }

    public object Snapshot() => new
    {
        active = Volatile.Read(ref active),
        queued = Volatile.Read(ref queued)
    };

    private void Write(string color, string state, string lane, string operation, string? target, long elapsedMs)
    {
        var prefix = colorEnabled ? Ansi(color) : string.Empty;
        var reset = colorEnabled ? "[0m" : string.Empty;
        var detail = string.IsNullOrWhiteSpace(target) ? string.Empty : $" | {target}";
        if (consoleEnabled)
        {
            lock (gate)
            {
                Console.Error.WriteLine($"{prefix}[WC] {state,-16} {lane,-8} {elapsedMs,7} ms | {operation}{detail}{reset}");
            }
        }

        _ = events.Writer.TryWrite(new ActivityEvent(
            DateTimeOffset.UtcNow,
            state,
            lane,
            operation,
            target,
            elapsedMs,
            Volatile.Read(ref active),
            Volatile.Read(ref queued)));
    }

    private void Begin(string lane, string operation, string? target, Stopwatch stopwatch)
    {
        Interlocked.Decrement(ref queued);
        Interlocked.Increment(ref active);
        Write("CYAN", "RUNNING", lane, operation, target, stopwatch.ElapsedMilliseconds);
    }

    private void End(string lane, string operation, string? target, Stopwatch stopwatch, Exception? error)
    {
        Interlocked.Decrement(ref active);
        Write(
            error is null ? "GREEN" : "RED",
            error is null ? "SUCCESS" : "FAILED",
            lane,
            operation,
            error is null ? target : error.Message,
            stopwatch.ElapsedMilliseconds);
    }

    private async Task WriteEventsAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(EventLogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            RotateIfNeeded();

            await using var stream = new FileStream(
                EventLogPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 32 * 1024,
                options: FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream)
            {
                AutoFlush = true
            };

            await foreach (var activityEvent in events.Reader.ReadAllAsync(lifetime.Token))
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(activityEvent));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[WC] activity observer stream disabled: {exception.Message}");
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(EventLogPath) || new FileInfo(EventLogPath).Length <= RotateBytes)
            {
                return;
            }

            var previous = EventLogPath + ".1";
            File.Move(EventLogPath, previous, overwrite: true);
        }
        catch
        {
            // Activity rendering is observational and never blocks the MCP.
        }
    }

    private static string ResolveEventLogPath()
    {
        var configured = Environment.GetEnvironmentVariable("WINDOWS_COMMANDER_ACTIVITY_LOG");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsCommander",
            "activity.jsonl");
    }

    private static string Ansi(string color) => color switch
    {
        "GREEN" => "[32m",
        "CYAN" => "[36m",
        "BLUE" => "[34m",
        "YELLOW" => "[33m",
        "MAGENTA" => "[35m",
        "RED" => "[31m",
        "GRAY" => "[90m",
        _ => string.Empty
    };

    public async ValueTask DisposeAsync()
    {
        events.Writer.TryComplete();
        try
        {
            await writerTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            lifetime.Cancel();
            try { await writerTask; } catch { }
        }

        lifetime.Dispose();
    }

    private sealed record ActivityEvent(
        DateTimeOffset Timestamp,
        string State,
        string Lane,
        string Operation,
        string? Detail,
        long ElapsedMs,
        int Active,
        int Queued);

    internal sealed class ActivityScope : IDisposable
    {
        private readonly RescueConsole owner;
        private readonly string lane;
        private readonly string operation;
        private readonly string? target;
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private Exception? error;
        private bool started;
        private bool disposed;

        public ActivityScope(RescueConsole owner, string lane, string operation, string? target)
        {
            this.owner = owner;
            this.lane = lane;
            this.operation = operation;
            this.target = target;
        }

        public void MarkStarted()
        {
            if (started) return;
            started = true;
            owner.Begin(lane, operation, target, stopwatch);
        }

        public void MarkError(Exception exception) => error = exception;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            stopwatch.Stop();
            if (!started)
            {
                Interlocked.Decrement(ref owner.queued);
                owner.Write(
                    error is null ? "GRAY" : "RED",
                    error is null ? "CANCELLED" : "FAILED",
                    lane,
                    operation,
                    error?.Message ?? target,
                    stopwatch.ElapsedMilliseconds);
                return;
            }

            owner.End(lane, operation, target, stopwatch, error);
        }
    }
}
