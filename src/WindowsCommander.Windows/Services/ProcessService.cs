using System.ComponentModel;
using System.Diagnostics;
using WindowsCommander.Core.Models;
using WindowsCommander.Core.Services;

namespace WindowsCommander.Windows.Services;

public sealed class ProcessService : IProcessService
{
    public IReadOnlyList<ProcessSummary> ListProcesses(string? filterName, bool sortByMemory)
    {
        var summaries = new List<ProcessSummary>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!MatchesFilter(process, filterName))
                    {
                        continue;
                    }

                    summaries.Add(ToSummary(process));
                }
                catch (InvalidOperationException)
                {
                    // Processes can disappear between enumeration and inspection.
                }
                catch (Win32Exception)
                {
                    // Access to some protected processes is denied; skip them
                    // without failing the entire listing.
                }
            }
        }

        return sortByMemory
            ? summaries.OrderByDescending(process => process.MemoryUsageMB).ToArray()
            : summaries.OrderBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public ProcessDetails GetProcessDetails(int pid)
    {
        using var process = Process.GetProcessById(pid);
        var windows = WindowService.EnumerateWindows(visibleOnly: false)
            .Where(window => window.OwningPID == pid)
            .Select(window => window.HWND)
            .ToArray();

        return new ProcessDetails(
            process.Id,
            process.ProcessName,
            GetExecutablePath(process),
            process.Responding ? "Responding" : "Hung",
            windows);
    }

    public ProcessActionResult ManageProcess(int pid, string action)
    {
        if (pid == Environment.ProcessId)
        {
            throw new InvalidOperationException("Windows Commander refuses to terminate its own MCP process.");
        }

        using var process = Process.GetProcessById(pid);
        var normalizedAction = action.ToLowerInvariant();
        var completed = normalizedAction switch
        {
            "terminate" or "kill" => KillAndVerify(process, entireProcessTree: false),
            "kill_tree" => KillAndVerify(process, entireProcessTree: true),
            "close_main_window" => CloseMainWindowAndVerify(process),
            _ => throw new ArgumentException($"Unsupported process action: {action}")
        };

        return new ProcessActionResult(pid, action, completed);
    }

    private static bool KillAndVerify(Process process, bool entireProcessTree)
    {
        process.Kill(entireProcessTree);
        return WaitForExit(process, TimeSpan.FromSeconds(5));
    }

    private static bool CloseMainWindowAndVerify(Process process)
    {
        if (!process.CloseMainWindow())
        {
            throw new InvalidOperationException($"Process does not have a closable main window: {process.Id}");
        }

        if (WaitForExit(process, TimeSpan.FromSeconds(5)))
        {
            return true;
        }

        try
        {
            process.Refresh();
            return process.MainWindowHandle == IntPtr.Zero;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool WaitForExit(Process process, TimeSpan timeout)
    {
        try
        {
            if (process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                return true;
            }

            process.Refresh();
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool MatchesFilter(Process process, string? filterName)
    {
        return string.IsNullOrWhiteSpace(filterName)
            || process.ProcessName.Contains(filterName, StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessSummary ToSummary(Process process)
    {
        return new ProcessSummary(
            process.Id,
            process.ProcessName,
            Math.Round(process.WorkingSet64 / 1024d / 1024d, 2),
            GetMainWindowTitle(process));
    }

    private static string GetMainWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string? GetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
