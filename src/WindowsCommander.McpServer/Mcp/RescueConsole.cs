using System.Diagnostics;

namespace WindowsCommander.McpServer.Mcp;

internal sealed class RescueConsole
{
    private readonly object gate = new();
    private readonly bool colorEnabled;
    private int active;
    private int queued;

    public RescueConsole()
    {
        colorEnabled = !Console.IsErrorRedirected && !string.Equals(
            Environment.GetEnvironmentVariable("NO_COLOR"), "1", StringComparison.OrdinalIgnoreCase);
    }

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
        var reset = colorEnabled ? "\u001b[0m" : string.Empty;
        var detail = string.IsNullOrWhiteSpace(target) ? string.Empty : $" | {target}";
        lock (gate)
        {
            Console.Error.WriteLine($"{prefix}[WC] {state,-16} {lane,-8} {elapsedMs,7} ms | {operation}{detail}{reset}");
        }
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
        Write(error is null ? "GREEN" : "RED", error is null ? "SUCCESS" : "FAILED", lane, operation,
            error is null ? target : error.Message, stopwatch.ElapsedMilliseconds);
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
                owner.Write(error is null ? "GRAY" : "RED", error is null ? "CANCELLED" : "FAILED", lane, operation, error?.Message ?? target, stopwatch.ElapsedMilliseconds);
                return;
            }

            owner.End(lane, operation, target, stopwatch, error);
        }
    }
}
