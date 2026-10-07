using System.Collections.Concurrent;

namespace WindowsCommander.Windows.Services;

internal sealed class StaExecutor
{
    public static StaExecutor Shared { get; } = new();

    private readonly BlockingCollection<IWorkItem> queue = new();
    private readonly Thread thread;
    private volatile bool poisoned;

    private StaExecutor()
    {
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "WindowsCommander.STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public T Invoke<T>(Func<T> action, int timeoutMs = 5000)
    {
        if (poisoned)
        {
            throw new InvalidOperationException("Desktop STA lane is poisoned after a previous non-cancellable timeout; restart Windows Commander to recover it.");
        }

        var item = new WorkItem<T>(action);
        queue.Add(item);
        if (!item.Wait(Math.Clamp(timeoutMs, 100, 30_000)))
        {
            poisoned = true;
            throw new TimeoutException($"Desktop STA operation exceeded {timeoutMs} ms; lane marked poisoned.");
        }

        return item.GetResult();
    }

    private void Run()
    {
        foreach (var item in queue.GetConsumingEnumerable())
        {
            item.Execute();
        }
    }

    private interface IWorkItem { void Execute(); }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<T> action;
        private readonly ManualResetEventSlim completed = new(false);
        private T? result;
        private Exception? error;

        public WorkItem(Func<T> action) => this.action = action;

        public void Execute()
        {
            try { result = action(); }
            catch (Exception exception) { error = exception; }
            finally { completed.Set(); }
        }

        public bool Wait(int timeoutMs) => completed.Wait(timeoutMs);

        public T GetResult()
        {
            if (error is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }

            return result!;
        }
    }
}
