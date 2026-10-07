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
    private long nextActivityId;
    private int active;
    private int queued;

    public RescueConsole()
    {
        // Raw per-operation rows are intentionally off by default. The dedicated
        // dashboard consumes the JSONL activity stream and renders the readable UI.
        consoleEnabled = string.Equals(
            Environment.GetEnvironmentVariable("WINDOWS_COMMANDER_ACTIVITY_CONSOLE"), "1", StringComparison.OrdinalIgnoreCase);

        // Raw ANSI sequences render as garbage in several Windows/tunnel hosts.
        // Enable them only when explicitly requested; the dedicated dashboard
        // uses native PowerShell console colors instead.
        colorEnabled = consoleEnabled
            && !Console.IsErrorRedirected
            && string.Equals(
                Environment.GetEnvironmentVariable("WINDOWS_COMMANDER_ANSI"), "1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
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

    public ActivityScope Queued(string lane, string operation, string? detail = null)
    {
        var activityId = Interlocked.Increment(ref nextActivityId);
        Interlocked.Increment(ref queued);
        Write(activityId, "BLUE", "QUEUED", lane, operation, detail, 0);
        return new ActivityScope(this, activityId, lane, operation, detail);
    }

    public object Snapshot() => new
    {
        active = Volatile.Read(ref active),
        queued = Volatile.Read(ref queued)
    };

    private void Write(
        long activityId,
        string color,
        string state,
        string lane,
        string operation,
        string? detail,
        long elapsedMs)
    {
        var prefix = colorEnabled ? Ansi(color) : string.Empty;
        var reset = colorEnabled ? "\u001b[0m" : string.Empty;
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" | {detail}";

        if (consoleEnabled)
        {
            lock (gate)
            {
                Console.Error.WriteLine(
                    $"{prefix}[WC] #{activityId,-5} {state,-9} {lane,-8} {elapsedMs,7} ms | {operation}{suffix}{reset}");
            }
        }

        _ = events.Writer.TryWrite(new ActivityEvent(
            DateTimeOffset.UtcNow,
            activityId,
            state,
            lane,
            operation,
            detail,
            elapsedMs,
            Volatile.Read(ref active),
            Volatile.Read(ref queued)));
    }

    private void Begin(ActivityScope scope)
    {
        Interlocked.Decrement(ref queued);
        Interlocked.Increment(ref active);
        Write(
            scope.ActivityId,
            "CYAN",
            "RUNNING",
            scope.Lane,
            scope.Operation,
            scope.RequestDetail,
            scope.Stopwatch.ElapsedMilliseconds);
    }

    private void End(ActivityScope scope)
    {
        Interlocked.Decrement(ref active);

        var detail = scope.Error is not null
            ? Combine(scope.RequestDetail, $"ERROR: {scope.Error.Message}")
            : Combine(scope.RequestDetail, scope.ResultDetail);

        Write(
            scope.ActivityId,
            scope.Error is null ? "GREEN" : "RED",
            scope.Error is null ? "SUCCESS" : "FAILED",
            scope.Lane,
            scope.Operation,
            detail,
            scope.Stopwatch.ElapsedMilliseconds);
    }

    private static string? Combine(string? request, string? result)
    {
        if (string.IsNullOrWhiteSpace(request)) return result;
        if (string.IsNullOrWhiteSpace(result)) return request;
        return $"{request} => {result}";
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
        "GREEN" => "\u001b[32m",
        "CYAN" => "\u001b[36m",
        "BLUE" => "\u001b[34m",
        "YELLOW" => "\u001b[33m",
        "MAGENTA" => "\u001b[35m",
        "RED" => "\u001b[31m",
        "GRAY" => "\u001b[90m",
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
        long ActivityId,
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
        private bool started;
        private bool disposed;

        public ActivityScope(
            RescueConsole owner,
            long activityId,
            string lane,
            string operation,
            string? requestDetail)
        {
            this.owner = owner;
            ActivityId = activityId;
            Lane = lane;
            Operation = operation;
            RequestDetail = requestDetail;
        }

        internal long ActivityId { get; }
        internal string Lane { get; }
        internal string Operation { get; }
        internal string? RequestDetail { get; }
        internal string? ResultDetail { get; private set; }
        internal Exception? Error { get; private set; }
        internal Stopwatch Stopwatch { get; } = Stopwatch.StartNew();

        public void MarkStarted()
        {
            if (started) return;
            started = true;
            owner.Begin(this);
        }

        public void MarkResult(string? detail) => ResultDetail = detail;

        public void MarkError(Exception exception) => Error = exception;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Stopwatch.Stop();

            if (!started)
            {
                Interlocked.Decrement(ref owner.queued);
                owner.Write(
                    ActivityId,
                    Error is null ? "GRAY" : "RED",
                    Error is null ? "CANCELLED" : "FAILED",
                    Lane,
                    Operation,
                    Error?.Message ?? RequestDetail,
                    Stopwatch.ElapsedMilliseconds);
                return;
            }

            owner.End(this);
        }
    }
}
