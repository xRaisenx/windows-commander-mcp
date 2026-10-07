using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WindowsCommander.McpServer;
using WindowsCommander.McpServer.Mcp;
using WindowsCommander.Safety.Audit;
using WindowsCommander.Safety.Policy;
using WindowsCommander.Windows.Services;

using var instanceGuard = InstanceGuard.Acquire();

var requireConfirmation = !IsUnattended();
var requestTimeoutMs = GetBoundedEnvironmentInt(
    "WINDOWS_COMMANDER_REQUEST_TIMEOUT_MS",
    defaultValue: 90_000,
    minimum: 5_000,
    maximum: 110_000);
var maxConcurrency = GetBoundedEnvironmentInt(
    "WINDOWS_COMMANDER_MAX_CONCURRENCY",
    defaultValue: 6,
    minimum: 1,
    maximum: 10);
var maxRequestBytes = GetBoundedEnvironmentInt(
    "WINDOWS_COMMANDER_MAX_REQUEST_BYTES",
    defaultValue: 1024 * 1024,
    minimum: 64 * 1024,
    maximum: 8 * 1024 * 1024);

var rescueConsole = new RescueConsole();
var processOperations = new ProcessOperationSupervisor(Math.Min(2, maxConcurrency));
var serverUptime = Stopwatch.StartNew();
Func<object> runtimeStatusProvider = () => CreateRuntimeStatus(
    rescueConsole,
    serverUptime,
    maxConcurrency,
    requestTimeoutMs,
    maxRequestBytes,
    requireConfirmation);

var dispatcher = new ToolDispatcher(
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
    new PersistentAuditLog(),
    new RiskPolicyService(),
    requireConfirmation,
    runtimeStatusProvider,
    processOperations);
using var globalConcurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);
using var desktopLane = new SemaphoreSlim(1, 1);
using var processLane = new SemaphoreSlim(Math.Min(2, maxConcurrency), Math.Min(2, maxConcurrency));

var responses = Channel.CreateBounded<JsonRpcResponse>(new BoundedChannelOptions(Math.Max(16, maxConcurrency * 4))
{
    FullMode = BoundedChannelFullMode.Wait,
    SingleReader = true,
    SingleWriter = false
});

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var input = new StreamReader(Console.OpenStandardInput(), utf8);
await using var output = new StreamWriter(Console.OpenStandardOutput(), utf8)
{
    AutoFlush = false,
    NewLine = "\n"
};

var writerTask = WriteResponsesAsync(responses.Reader, output);
var inFlight = new List<Task>();

while (await input.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    if (Encoding.UTF8.GetByteCount(line) > maxRequestBytes)
    {
        await responses.Writer.WriteAsync(JsonRpcResponse.Failure(null, -32600, $"Request exceeds maximum size of {maxRequestBytes} bytes."));
        continue;
    }

    // Keep rescue/status visibility available even when all normal execution
    // slots are occupied by slow operations.
    var fastStatus = TryCreateRuntimeStatusResponse(line, runtimeStatusProvider);
    if (fastStatus is not null)
    {
        await responses.Writer.WriteAsync(fastStatus);
        continue;
    }

    await globalConcurrency.WaitAsync();
    inFlight.RemoveAll(static task => task.IsCompleted);

    inFlight.Add(Task.Run(async () =>
    {
        try
        {
            var response = await HandleLineAsync(
                line,
                dispatcher,
                desktopLane,
                processLane,
                requestTimeoutMs,
                rescueConsole);

            if (response is not null)
            {
                await responses.Writer.WriteAsync(response);
            }
        }
        finally
        {
            globalConcurrency.Release();
        }
    }));
}

await Task.WhenAll(inFlight);
responses.Writer.TryComplete();
await writerTask;

static JsonRpcResponse? TryCreateRuntimeStatusResponse(string line, Func<object> runtimeStatusProvider)
{
    try
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("method", out var method)
            || !string.Equals(method.GetString(), "tools/call", StringComparison.Ordinal)
            || !root.TryGetProperty("params", out var parameters)
            || !parameters.TryGetProperty("name", out var name)
            || !string.Equals(name.GetString(), "get_runtime_status", StringComparison.Ordinal))
        {
            return null;
        }

        if (!root.TryGetProperty("id", out var id))
        {
            return null;
        }

        return JsonRpcResponse.Success(id.Clone(), runtimeStatusProvider());
    }
    catch (JsonException)
    {
        // Let the normal parser return the protocol error.
        return null;
    }
}

static object CreateRuntimeStatus(
    RescueConsole rescueConsole,
    Stopwatch serverUptime,
    int maxConcurrency,
    int requestTimeoutMs,
    int maxRequestBytes,
    bool requireConfirmation)
{
    int? sessionId = null;
    try
    {
        using var process = Process.GetCurrentProcess();
        sessionId = process.SessionId;
    }
    catch
    {
        // Session identity can be unavailable in constrained hosts.
    }

    return new
    {
        healthy = true,
        server = ServerInfo.Name,
        version = ServerInfo.Version,
        informational_version = ServerInfo.InformationalVersion,
        source_revision = ServerInfo.SourceRevision,
        executable_sha256 = ServerInfo.ExecutableSha256,
        process_id = Environment.ProcessId,
        process_started_at = ServerInfo.ProcessStartedAt,
        uptime_ms = serverUptime.ElapsedMilliseconds,
        process_session_id = sessionId,
        user_interactive = Environment.UserInteractive,
        instance_key = Environment.GetEnvironmentVariable("WINDOWS_COMMANDER_INSTANCE_KEY") ?? "default",
        confirmation_policy = requireConfirmation ? "local-confirmation-for-high-risk" : "unattended",
        max_concurrency = maxConcurrency,
        desktop_lane_limit = 1,
        process_lane_limit = Math.Min(2, maxConcurrency),
        request_timeout_ms = requestTimeoutMs,
        max_request_bytes = maxRequestBytes,
        stdout_protocol_only = true,
        inherited_child_secrets_allowed = ChildEnvironmentSanitizer.AllowsInheritedSecrets,
        scheduler = rescueConsole.Snapshot()
    };
}

static async Task WriteResponsesAsync(ChannelReader<JsonRpcResponse> reader, StreamWriter output)
{
    await foreach (var response in reader.ReadAllAsync())
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions.Default));
        await output.FlushAsync();
    }
}

static async Task<JsonRpcResponse?> HandleLineAsync(
    string line,
    ToolDispatcher dispatcher,
    SemaphoreSlim desktopLane,
    SemaphoreSlim processLane,
    int requestTimeoutMs,
    RescueConsole rescueConsole)
{
    JsonRpcRequest? request = null;
    try
    {
        request = JsonSerializer.Deserialize<JsonRpcRequest>(line, JsonOptions.Default)
            ?? throw new InvalidOperationException("Request body is empty.");

        if (!string.Equals(request.JsonRpc, "2.0", StringComparison.Ordinal))
        {
            return JsonRpcResponse.Failure(request.Id, -32600, "jsonrpc must be '2.0'.");
        }

        if (string.IsNullOrWhiteSpace(request.Method))
        {
            return JsonRpcResponse.Failure(request.Id, -32600, "Request method is required.");
        }

        // JSON-RPC notifications intentionally receive no response.
        if (request.Id is null)
        {
            return null;
        }

        if (request.Method == "initialize")
        {
            return JsonRpcResponse.Success(request.Id, new
            {
                protocolVersion = NegotiateProtocolVersion(request.Params),
                capabilities = new { tools = new { } },
                serverInfo = new { name = ServerInfo.Name, version = ServerInfo.Version }
            });
        }

        if (request.Method == "tools/list")
        {
            return JsonRpcResponse.Success(request.Id, dispatcher.ListTools());
        }

        if (request.Method != "tools/call")
        {
            return JsonRpcResponse.Failure(request.Id, -32601, $"Method not found: {request.Method}");
        }

        var toolName = GetRequiredString(request.Params, "name");
        var arguments = GetProperty(request.Params, "arguments");
        var lane = ClassifyLane(toolName);
        using var activity = rescueConsole.Queued(lane, toolName);

        SemaphoreSlim? laneSemaphore = lane switch
        {
            "DESKTOP" => desktopLane,
            "PROCESS" => processLane,
            _ => null
        };

        if (laneSemaphore is not null)
        {
            await laneSemaphore.WaitAsync();
        }

        try
        {
            activity.MarkStarted();
            var result = await CallToolWithDeadlineAsync(dispatcher, toolName, arguments, requestTimeoutMs);
            return JsonRpcResponse.Success(request.Id, result);
        }
        catch (Exception exception)
        {
            activity.MarkError(exception);
            throw;
        }
        finally
        {
            laneSemaphore?.Release();
        }
    }
    catch (JsonException exception)
    {
        return JsonRpcResponse.Failure(request?.Id, -32700, exception.Message);
    }
    catch (Exception exception)
    {
        var code = exception is ArgumentException or InvalidOperationException ? -32602 : -32603;
        return JsonRpcResponse.Failure(request?.Id, code, exception.Message);
    }
}

static async Task<object> CallToolWithDeadlineAsync(
    ToolDispatcher dispatcher,
    string toolName,
    JsonElement? arguments,
    int requestTimeoutMs)
{
    using var deadline = new CancellationTokenSource(requestTimeoutMs);
    return await dispatcher.CallToolAsync(toolName, arguments, deadline.Token);
}

static string ClassifyLane(string toolName)
{
    return toolName switch
    {
        "focus_window" or "move_resize_window" or "set_window_state"
            or "mouse_action" or "type_text" or "send_hotkey" or "keyboard_action"
            or "mouse_wheel" or "set_cursor_position" or "input_sequence"
            or "capture_screen" or "capture_screen_region" or "ocr_screen"
            or "detect_visual_elements" or "read_ui_tree" or "find_ui_element"
            or "invoke_ui_element" or "set_ui_value" or "get_ui_element_details"
            or "clipboard_access" => "DESKTOP",

        "execute_process" or "execute_powershell" or "manage_process"
            or "launch_app" or "start_process_operation" or "cancel_process_operation" => "PROCESS",

        "write_file" or "copy_move_delete_path" or "environment_variable"
            => "MUTATE",

        _ => "READ"
    };
}

static bool IsUnattended()
{
    var value = Environment.GetEnvironmentVariable("WINDOWS_COMMANDER_UNATTENDED");
    return value is "1" or "true" or "TRUE" or "True" or "yes";
}

static int GetBoundedEnvironmentInt(string name, int defaultValue, int minimum, int maximum)
{
    var raw = Environment.GetEnvironmentVariable(name);
    return int.TryParse(raw, out var parsed)
        ? Math.Clamp(parsed, minimum, maximum)
        : defaultValue;
}

static string NegotiateProtocolVersion(JsonElement? requestParams)
{
    const string supported = "2024-11-05";
    return supported;
}

static string GetRequiredString(JsonElement? element, string propertyName)
{
    var property = GetProperty(element, propertyName);

    return property is not null && property.Value.ValueKind == JsonValueKind.String
        ? property.Value.GetString() ?? string.Empty
        : throw new ArgumentException($"Missing or invalid string property: {propertyName}");
}

static JsonElement? GetProperty(JsonElement? element, string propertyName)
{
    return element is not null
        && element.Value.ValueKind == JsonValueKind.Object
        && element.Value.TryGetProperty(propertyName, out var property)
            ? property
            : null;
}
