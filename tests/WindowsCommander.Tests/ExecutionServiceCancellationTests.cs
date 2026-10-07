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

    [Fact]
    public async Task ExecuteProcessAsync_WaitForExit_PreservesRealPid()
    {
        var service = new ExecutionService();
        var result = await service.ExecuteProcessAsync(
            "pwsh.exe",
            new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "exit 0" },
            workingDirectory: null,
            timeoutMs: 5000,
            waitForExit: true,
            CancellationToken.None);

        Assert.True(result.ProcessId > 0);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task ExecutePowerShellAsync_TimeoutPreservesPartialOutput()
    {
        var service = new ExecutionService();
        var result = await service.ExecutePowerShellAsync(
            "[Console]::Out.WriteLine('before-timeout'); [Console]::Out.Flush(); Start-Sleep -Seconds 10",
            workingDirectory: null,
            timeoutMs: 3000,
            environment: null,
            CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Contains("before-timeout", result.StandardOutput);
        Assert.Contains("timed out", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task ExecuteProcessAsync_ParentExitDoesNotWaitForDetachedChildHoldingPipes()
    {
        var service = new ExecutionService();
        var result = await service.ExecuteProcessAsync(
            "pwsh.exe",
            new[]
            {
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "Start-Process -FilePath pwsh.exe -ArgumentList '-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 6' -NoNewWindow; Write-Output 'parent-done'"
            },
            workingDirectory: null,
            timeoutMs: 10000,
            waitForExit: true,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("parent-done", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.ElapsedTime);
        Assert.True(
            result.ElapsedTime < TimeSpan.FromSeconds(3),
            $"Expected the direct parent result without waiting for the detached child; elapsed={result.ElapsedTime}.");
    }

}
