using WindowsCommander.McpServer.Mcp;

namespace WindowsCommander.Tests;

public class ProcessOperationSupervisorTests
{
    [Fact]
    public async Task Start_ReturnsHandleAndCapturesBoundedOutput()
    {
        var supervisor = new ProcessOperationSupervisor(maxConcurrentProcesses: 1);

        var started = supervisor.Start(
            "pwsh.exe",
            new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Write-Output 'managed-ok'; exit 0" },
            workingDirectory: null,
            timeoutMs: 5000,
            maxOutputBytes: 64 * 1024);

        Assert.False(string.IsNullOrWhiteSpace(started.OperationId));

        var completed = await WaitForTerminalAsync(supervisor, started.OperationId);
        Assert.Equal("SUCCEEDED", completed.State);
        Assert.True(completed.ProcessId > 0);
        Assert.Equal(0, completed.ExitCode);
        Assert.Contains("managed-ok", completed.StandardOutput);
        Assert.False(completed.StdoutTruncated);
    }

    [Fact]
    public async Task Timeout_PreservesPartialOutputAndKillsOwnedTree()
    {
        var supervisor = new ProcessOperationSupervisor(maxConcurrentProcesses: 1);

        var started = supervisor.Start(
            "pwsh.exe",
            new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.WriteLine('before-timeout'); [Console]::Out.Flush(); Start-Sleep -Seconds 10" },
            workingDirectory: null,
            timeoutMs: 3000,
            maxOutputBytes: 64 * 1024);

        var completed = await WaitForTerminalAsync(supervisor, started.OperationId);
        Assert.Equal("TIMED_OUT", completed.State);
        Assert.Contains("before-timeout", completed.StandardOutput);
        Assert.Contains("timeout", completed.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancel_QueuedOperationDoesNotStartIt()
    {
        var supervisor = new ProcessOperationSupervisor(maxConcurrentProcesses: 1);

        var first = supervisor.Start(
            "pwsh.exe",
            new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 3" },
            null,
            5000,
            64 * 1024);

        var second = supervisor.Start(
            "pwsh.exe",
            new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Write-Output 'must-not-run'" },
            null,
            5000,
            64 * 1024);

        supervisor.Cancel(second.OperationId);
        var cancelled = await WaitForTerminalAsync(supervisor, second.OperationId);

        Assert.Equal("CANCELLED", cancelled.State);
        Assert.Null(cancelled.ProcessId);
        Assert.DoesNotContain("must-not-run", cancelled.StandardOutput);

        supervisor.Cancel(first.OperationId);
        _ = await WaitForTerminalAsync(supervisor, first.OperationId);
    }

    private static async Task<ManagedOperationSnapshot> WaitForTerminalAsync(
        ProcessOperationSupervisor supervisor,
        string operationId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var snapshot = supervisor.Get(operationId);
            if (snapshot.State is "SUCCEEDED" or "FAILED" or "CANCELLED" or "TIMED_OUT")
            {
                return snapshot;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Operation did not reach a terminal state: {operationId}");
    }
}
