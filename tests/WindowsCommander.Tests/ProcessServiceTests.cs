using WindowsCommander.Windows.Services;

namespace WindowsCommander.Tests;

public class ProcessServiceTests
{
    [Fact]
    public void ManageProcess_RefusesToTerminateWindowsCommanderProcess()
    {
        var service = new ProcessService();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            service.ManageProcess(Environment.ProcessId, "kill_tree"));

        Assert.Contains("refuses to terminate its own MCP process", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListProcesses_FilteredEnumerationStillReturnsStableResults()
    {
        var service = new ProcessService();
        using var current = System.Diagnostics.Process.GetCurrentProcess();

        var results = service.ListProcesses(current.ProcessName, sortByMemory: false);

        Assert.Contains(results, process => process.PID == Environment.ProcessId);
    }
}
