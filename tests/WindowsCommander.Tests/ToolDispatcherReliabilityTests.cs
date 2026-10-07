using System.Text.Json;
using WindowsCommander.McpServer.Mcp;
using WindowsCommander.Safety.Audit;
using WindowsCommander.Safety.Policy;
using WindowsCommander.Windows.Services;

namespace WindowsCommander.Tests;

public class ToolDispatcherReliabilityTests
{
    [Fact]
    public async Task CallToolAsync_TimesOutSlowTool_AndAcceptsNextRequest()
    {
        var dispatcher = CreateDispatcher(
            toolCallTimeout: TimeSpan.FromMilliseconds(500),
            maxResponseBytes: 1024 * 1024);

        using var slowArguments = JsonDocument.Parse(
            """{"command":"Start-Sleep -Seconds 30","timeout_ms":30000}""");

        var timedOut = await dispatcher.CallToolAsync(
            "execute_powershell",
            slowArguments.RootElement,
            CancellationToken.None);

        var timedOutJson = JsonSerializer.Serialize(timedOut);
        Assert.Contains(""isError":true", timedOutJson, StringComparison.Ordinal);
        Assert.Contains("deadline", timedOutJson, StringComparison.OrdinalIgnoreCase);

        var next = await dispatcher.CallToolAsync(
            "get_system_info",
            arguments: null,
            CancellationToken.None);

        var nextJson = JsonSerializer.Serialize(next);
        Assert.DoesNotContain(""isError":true", nextJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallToolAsync_RejectsOversizedResult_AndKeepsServerUsable()
    {
        var dispatcher = CreateDispatcher(
            toolCallTimeout: TimeSpan.FromSeconds(5),
            maxResponseBytes: 512);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "windows-commander-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var filePath = Path.Combine(tempDirectory, "large.txt");
        File.WriteAllText(filePath, new string('x', 4096));

        try
        {
            using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                path = filePath,
                encoding = "utf-8",
                as_base64 = false
            }));

            var oversized = await dispatcher.CallToolAsync(
                "read_file",
                arguments.RootElement,
                CancellationToken.None);

            var oversizedJson = JsonSerializer.Serialize(oversized);
            Assert.Contains(""isError":true", oversizedJson, StringComparison.Ordinal);
            Assert.Contains("response budget", oversizedJson, StringComparison.OrdinalIgnoreCase);

            var next = await dispatcher.CallToolAsync(
                "get_cursor_position",
                arguments: null,
                CancellationToken.None);

            var nextJson = JsonSerializer.Serialize(next);
            Assert.DoesNotContain(""isError":true", nextJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static ToolDispatcher CreateDispatcher(TimeSpan toolCallTimeout, int maxResponseBytes)
    {
        return new ToolDispatcher(
            new ProcessService(),
            new WindowService(),
            new ScreenService(),
            new SystemInfoService(),
            new ExecutionService(),
            new EnvironmentService(),
            new ClipboardService(),
            new FileSystemService(),
            new ShellService(),
            new WindowsServiceDiscoveryService(),
            new RegistryService(),
            new ApplicationService(),
            new InputService(),
            new VisionService(),
            new UiAutomationService(),
            new ControlIndicatorService(),
            new InMemoryAuditLog(),
            new RiskPolicyService(),
            requireConfirmation: false,
            toolCallTimeout: toolCallTimeout,
            maxResponseBytes: maxResponseBytes);
    }
}
