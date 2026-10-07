using System.Diagnostics;
using WindowsCommander.Windows.Services;

namespace WindowsCommander.Tests;

public class ExecutionServiceTests
{
    [Fact]
    public async Task ExecutePowerShellAsync_ExternalCancellation_ReturnsPromptly()
    {
        var service = new ExecutionService();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecutePowerShellAsync(
            "Start-Sleep -Seconds 30",
            workingDirectory: null,
            timeoutMs: 30000,
            environment: null,
            cancellationSource.Token));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Cancellation took {stopwatch.Elapsed}.");
    }
}
