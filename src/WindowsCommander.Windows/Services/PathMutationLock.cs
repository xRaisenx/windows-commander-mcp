namespace WindowsCommander.Windows.Services;

internal sealed class PathMutationLock
{
    public static PathMutationLock Shared { get; } = new();

    private readonly object gate = new();
    private readonly HashSet<string> activePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TaskCompletionSource<bool>> waiters = new();

    private PathMutationLock()
    {
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(IEnumerable<string?> paths, CancellationToken cancellationToken)
    {
        var requested = paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Canonicalize(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requested.Length == 0)
        {
            return NoopReleaser.Instance;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<bool>? waiter = null;

            lock (gate)
            {
                if (!HasConflict(requested))
                {
                    foreach (var path in requested)
                    {
                        activePaths.Add(path);
                    }

                    return new Releaser(this, requested);
                }

                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add(waiter);
            }

            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                waiter);

            await waiter.Task.ConfigureAwait(false);
        }
    }

    private bool HasConflict(IReadOnlyList<string> requested)
    {
        foreach (var candidate in requested)
        {
            foreach (var active in activePaths)
            {
                if (Conflicts(candidate, active))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Conflicts(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsChildOf(left, right) || IsChildOf(right, left);
    }

    private static bool IsChildOf(string candidate, string parent)
    {
        if (candidate.Length <= parent.Length || !candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var separator = candidate[parent.Length];
        return separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar;
    }

    private static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void Release(IReadOnlyList<string> paths)
    {
        TaskCompletionSource<bool>[] toWake;
        lock (gate)
        {
            foreach (var path in paths)
            {
                activePaths.Remove(path);
            }

            toWake = waiters.ToArray();
            waiters.Clear();
        }

        foreach (var waiter in toWake)
        {
            waiter.TrySetResult(true);
        }
    }

    private sealed class Releaser : IAsyncDisposable
    {
        private PathMutationLock? owner;
        private readonly IReadOnlyList<string> paths;

        public Releaser(PathMutationLock owner, IReadOnlyList<string> paths)
        {
            this.owner = owner;
            this.paths = paths;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref owner, null)?.Release(paths);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopReleaser : IAsyncDisposable
    {
        public static NoopReleaser Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
