using System.Security.Cryptography;
using System.Text;

namespace WindowsCommander.McpServer.Mcp;

internal sealed class InstanceGuard : IDisposable
{
    private readonly Mutex mutex;
    private bool ownsMutex;

    private InstanceGuard(Mutex mutex, bool ownsMutex)
    {
        this.mutex = mutex;
        this.ownsMutex = ownsMutex;
    }

    public static InstanceGuard Acquire()
    {
        var configured = Environment.GetEnvironmentVariable("WINDOWS_COMMANDER_INSTANCE_KEY");
        var instanceKey = string.IsNullOrWhiteSpace(configured) ? "default" : configured.Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceKey)))[..16];
        var mutex = new Mutex(initiallyOwned: false, $"Local\\WindowsCommander.McpServer.{hash}");

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            throw new InvalidOperationException($"Windows Commander instance '{instanceKey}' is already running.");
        }

        return new InstanceGuard(mutex, ownsMutex: true);
    }

    public void Dispose()
    {
        if (ownsMutex)
        {
            ownsMutex = false;
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }

        mutex.Dispose();
    }
}
