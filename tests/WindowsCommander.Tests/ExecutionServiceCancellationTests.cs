using WindowsCommander.Windows.Services;

namespace WindowsCommander.Tests;

public class ExecutionServiceCancellationTests
{
    [Fact]
    public async Task ExecutePowerShellAsync_ExternalCancellation_KillsChildProcessTree()
    {
        var service = new ExecutionService();
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "windows-commander-tests", Guid.NewGuid().ToString("N"));
        var markerPath = System.IO.Path.Combine(directory, "should-not-exist.txt");
        Directory.CreateDirectory(directory);

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var command = $"Start-Sleep -Seconds 2; Set-Content -LiteralPath '{markerPath.Replace("'", "''")}' -Value 'unexpected'";

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ExecutePowerShellAsync(
                    command,
                    directory,
                    timeoutMs: null,
                    environment: null,
                    cancellation.Token));

            await Task.Delay(TimeSpan.FromSeconds(2.25));
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
