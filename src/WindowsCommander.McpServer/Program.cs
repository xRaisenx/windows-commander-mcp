using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WindowsCommander.McpServer;
using WindowsCommander.McpServer.CodeIntel;
using WindowsCommander.McpServer.Mcp;
using WindowsCommander.Safety.Audit;
using WindowsCommander.Safety.Policy;
using WindowsCommander.Windows.Services;

_ = HostEnvironmentSanitizer.ScrubCurrentProcess();
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
var codeIntel = new CodeIntelManager();
var serenaRescue = new SerenaRescueService(processOperations);
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
    processOperations,
    codeIntel,
    serenaRescue);
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
    inFlight.Add(ProcessLineAndQueueResponseAsync(
        line,
        dispatcher,
        desktopLane,
        processLane,
        requestTimeoutMs,
        rescueConsole,
        responses.Writer,
        globalConcurrency));
}

await Task.WhenAll(inFlight);
responses.Writer.TryComplete();
await writerTask;
await codeIntel.DisposeAsync();
await rescueConsole.DisposeAsync();

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

        return JsonRpcResponse.Success(id.Clone(), ToolDispatcher.ToToolResult(runtimeStatusProvider()));
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
        assembly_sha256 = ServerInfo.AssemblySha256,
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
        activity_log = rescueConsole.EventLogPath,
        inherited_child_secrets_allowed = ChildEnvironmentSanitizer.AllowsInheritedSecrets,
        scheduler = rescueConsole.Snapshot()
    };
}

static async Task ProcessLineAndQueueResponseAsync(
    string line,
    ToolDispatcher dispatcher,
    SemaphoreSlim desktopLane,
    SemaphoreSlim processLane,
    int requestTimeoutMs,
    RescueConsole rescueConsole,
    ChannelWriter<JsonRpcResponse> writer,
    SemaphoreSlim globalConcurrency)
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
            await writer.WriteAsync(response);
        }
    }
    finally
    {
        globalConcurrency.Release();
    }
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
        using var activity = rescueConsole.Queued(lane, toolName, DescribeRequest(toolName, arguments));

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
            activity.MarkResult(DescribeResult(toolName, result));
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

static string? DescribeRequest(string toolName, JsonElement? arguments)
{
    string? path = SafeArg(arguments, "path");
    string? source = SafeArg(arguments, "source_path");
    string? destination = SafeArg(arguments, "destination_path");

    return toolName switch
    {
        "read_file" => Clip(path),
        "write_file" => JoinDetail(
            Clip(path),
            $"overwrite={SafeBool(arguments, "overwrite") ?? false}",
            $"chars={SafeArg(arguments, "content")?.Length ?? 0}"),
        "list_directory" => JoinDetail(Clip(path), SafeArg(arguments, "pattern")),
        "search_files" => JoinDetail(
            $"roots={SafeArrayCount(arguments, "roots")}",
            SafeArg(arguments, "name_pattern")),
        "copy_move_delete_path" => JoinDetail(
            SafeArg(arguments, "action"),
            Clip(source),
            string.IsNullOrWhiteSpace(destination) ? null : $"-> {Clip(destination)}"),
        "get_file_properties" => Clip(path),

        "execute_process" => JoinDetail(
            SafeExecutable(SafeArg(arguments, "executable_path")),
            $"args={SafeArrayCount(arguments, "arguments")}",
            $"wait={SafeBool(arguments, "wait_for_exit") ?? false}"),
        "execute_powershell" => $"PowerShell | chars={SafeArg(arguments, "command")?.Length ?? 0}",
        "start_process_operation" => JoinDetail(
            SafeExecutable(SafeArg(arguments, "executable_path")),
            $"args={SafeArrayCount(arguments, "arguments")}"),
        "get_process_operation" or "cancel_process_operation" =>
            $"operation={Clip(SafeArg(arguments, "operation_id"), 36)}",
        "manage_process" => JoinDetail(
            SafeArg(arguments, "action"),
            $"pid={SafeInt(arguments, "pid")}"),
        "get_process_details" => $"pid={SafeInt(arguments, "pid")}",
        "list_processes" => JoinDetail(
            SafeArg(arguments, "filter_name"),
            (SafeBool(arguments, "sort_by_memory") ?? false) ? "sort=memory" : null),

        "focus_window" or "move_resize_window" or "set_window_state" =>
            $"hwnd={SafeLong(arguments, "window_handle")}",
        "find_window" => JoinDetail(
            SafeArg(arguments, "title_contains"),
            SafeArg(arguments, "process_name")),
        "wait_for_window" => JoinDetail(
            SafeArg(arguments, "title_contains"),
            $"timeout={SafeInt(arguments, "timeout_ms")}ms"),

        "capture_screen" or "ocr_screen" or "detect_visual_elements" =>
            JoinDetail(SafeArg(arguments, "target"), $"hwnd={SafeLong(arguments, "window_handle")}"),
        "capture_screen_region" => JoinDetail(
            $"x={SafeInt(arguments, "x")}",
            $"y={SafeInt(arguments, "y")}",
            $"{SafeInt(arguments, "width")}x{SafeInt(arguments, "height")}"),

        "clipboard_access" => SafeArg(arguments, "action"),
        "type_text" => $"chars={SafeArg(arguments, "text")?.Length ?? 0}",
        "send_hotkey" => JoinDetail(
            $"mods={SafeArrayCount(arguments, "modifiers")}",
            SafeArg(arguments, "key")),
        "keyboard_action" => JoinDetail(
            SafeArg(arguments, "action"),
            SafeArg(arguments, "key")),
        "mouse_action" => JoinDetail(
            SafeArg(arguments, "action"),
            $"x={SafeInt(arguments, "x")}",
            $"y={SafeInt(arguments, "y")}"),
        "mouse_wheel" => JoinDetail(
            SafeArg(arguments, "direction"),
            $"amount={SafeInt(arguments, "amount")}"),

        "environment_variable" => JoinDetail(
            SafeArg(arguments, "action"),
            SafeArg(arguments, "name")),
        "registry_access" => JoinDetail(
            SafeArg(arguments, "action"),
            Clip(SafeArg(arguments, "path"))),
        "service_control" => JoinDetail(
            SafeArg(arguments, "action"),
            SafeArg(arguments, "service_name")),
        "launch_app" => JoinDetail(
            SafeExecutable(SafeArg(arguments, "executable_path")),
            SafeArg(arguments, "app_name")),

        "codeintel_start" => JoinDetail(
            SafeArg(arguments, "language_id"),
            Clip(SafeArg(arguments, "workspace_root"))),
        "codeintel_status" or "codeintel_stop" =>
            $"session={Clip(SafeArg(arguments, "session_id"), 24)}",
        "codeintel_symbols" or "codeintel_diagnostics" =>
            JoinDetail(
                $"session={Clip(SafeArg(arguments, "session_id"), 18)}",
                Clip(path)),
        "codeintel_definition" or "codeintel_references" or "codeintel_safe_delete_preflight" =>
            JoinDetail(
                Clip(path),
                $"line={SafeInt(arguments, "line")}",
                $"char={SafeInt(arguments, "character")}"),
        "codeintel_replace_symbol" => JoinDetail(
            Clip(path),
            $"symbol={Clip(SafeArg(arguments, "symbol_name"), 40)}",
            $"replacement_chars={SafeArg(arguments, "replacement")?.Length ?? 0}"),

        "serena_rescue_restart" => $"timeout={SafeInt(arguments, "timeout_ms")}ms",
        "serena_rescue_logs" => JoinDetail(
            $"files={SafeInt(arguments, "max_files")}",
            $"lines={SafeInt(arguments, "lines_per_file")}"),

        _ => DescribeGenericArguments(arguments)
    };
}

static string? DescribeResult(string toolName, object result)
{
    try
    {
        var json = UnwrapToolResult(JsonSerializer.SerializeToElement(result, JsonOptions.Default));

        if (json.ValueKind == JsonValueKind.Array)
        {
            return $"count={json.GetArrayLength()}";
        }

        if (json.ValueKind != JsonValueKind.Object)
        {
            return "completed";
        }

        return toolName switch
        {
            "read_file" => JoinDetail(
                $"read={JsonLong(json, "bytesRead")}B",
                JsonBool(json, "truncated") == true ? $"TRUNCATED total={JsonLong(json, "totalBytes")}B" : null),
            "write_file" => $"{JsonLong(json, "bytesWritten")}B written",
            "execute_process" => JoinDetail(
                $"pid={JsonLong(json, "processId")}",
                $"exit={JsonLong(json, "exitCode")}",
                JsonBool(json, "timedOut") == true ? "TIMEOUT" : null),
            "execute_powershell" => JoinDetail(
                $"exit={JsonLong(json, "exitCode")}",
                $"stdout={JsonStringLength(json, "standardOutput")} chars",
                $"stderr={JsonStringLength(json, "standardError")} chars",
                JsonBool(json, "timedOut") == true ? "TIMEOUT" : null),
            "start_process_operation" => JoinDetail(
                $"operation={Clip(JsonString(json, "operationId"), 24)}",
                JsonString(json, "state"),
                $"pid={JsonLong(json, "processId")}"),
            "get_process_operation" => JoinDetail(
                JsonString(json, "state"),
                $"pid={JsonLong(json, "processId")}",
                $"exit={JsonLong(json, "exitCode")}"),
            "serena_rescue_status" => JoinDetail(
                $"healthy={JsonBool(json, "healthy")}",
                JsonNestedBool(json, "router", "listening") == true ? "router=up" : "router=down",
                JsonNestedBool(json, "master", "listening") == true ? "master=up" : "master=down"),
            "codeintel_replace_symbol" => JoinDetail(
                $"applied={JsonBool(json, "applied")}",
                $"rollback={JsonBool(json, "rolledBack")}"),
            _ => DescribeGenericResult(json)
        };
    }
    catch
    {
        return "completed";
    }
}

static JsonElement UnwrapToolResult(JsonElement element)
{
    if (element.ValueKind != JsonValueKind.Object
        || !element.TryGetProperty("content", out var content)
        || content.ValueKind != JsonValueKind.Array)
    {
        return element;
    }

    foreach (var item in content.EnumerateArray())
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("type", out var type)
            || !string.Equals(type.GetString(), "text", StringComparison.Ordinal)
            || !item.TryGetProperty("text", out var text)
            || text.ValueKind != JsonValueKind.String)
        {
            continue;
        }

        var payload = text.GetString();
        if (string.IsNullOrWhiteSpace(payload)) continue;

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return element;
        }
    }

    return element;
}

static string? DescribeGenericArguments(JsonElement? arguments)
{
    if (arguments is null || arguments.Value.ValueKind != JsonValueKind.Object) return null;

    foreach (var name in new[] { "path", "service_name", "process_name", "app_name", "name" })
    {
        var value = SafeArg(arguments, name);
        if (!string.IsNullOrWhiteSpace(value)) return Clip(value);
    }

    foreach (var name in new[] { "pid", "window_handle" })
    {
        var value = SafeLong(arguments, name);
        if (value is not null) return $"{name}={value}";
    }

    return null;
}

static string DescribeGenericResult(JsonElement json)
{
    foreach (var name in new[] { "completed", "written", "cleared", "healthy", "ready", "stopped", "applied" })
    {
        var value = JsonBool(json, name);
        if (value is not null) return $"{name}={value.Value.ToString().ToLowerInvariant()}";
    }

    foreach (var name in new[] { "count", "processId", "pid" })
    {
        var value = JsonLong(json, name);
        if (value is not null) return $"{name}={value}";
    }

    return "completed";
}

static string? SafeArg(JsonElement? element, string propertyName)
{
    var property = GetProperty(element, propertyName);
    if (property is null || property.Value.ValueKind != JsonValueKind.String) return null;
    return property.Value.GetString();
}

static int SafeArrayCount(JsonElement? element, string propertyName)
{
    var property = GetProperty(element, propertyName);
    return property is not null && property.Value.ValueKind == JsonValueKind.Array
        ? property.Value.GetArrayLength()
        : 0;
}

static bool? SafeBool(JsonElement? element, string propertyName)
{
    var property = GetProperty(element, propertyName);
    if (property is null) return null;
    return property.Value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}

static int? SafeInt(JsonElement? element, string propertyName)
{
    var property = GetProperty(element, propertyName);
    return property is not null
        && property.Value.ValueKind == JsonValueKind.Number
        && property.Value.TryGetInt32(out var value)
            ? value
            : null;
}

static long? SafeLong(JsonElement? element, string propertyName)
{
    var property = GetProperty(element, propertyName);
    return property is not null
        && property.Value.ValueKind == JsonValueKind.Number
        && property.Value.TryGetInt64(out var value)
            ? value
            : null;
}

static string? JsonString(JsonElement element, string propertyName)
{
    return element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

static int JsonStringLength(JsonElement element, string propertyName)
{
    return JsonString(element, propertyName)?.Length ?? 0;
}

static long? JsonLong(JsonElement element, string propertyName)
{
    return element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var value)
            ? value
            : null;
}

static bool? JsonBool(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var property)) return null;
    return property.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}

static bool? JsonNestedBool(JsonElement element, string objectName, string propertyName)
{
    return element.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
        ? JsonBool(nested, propertyName)
        : null;
}

static string? SafeExecutable(string? executable)
{
    if (string.IsNullOrWhiteSpace(executable)) return null;
    try { return Path.GetFileName(executable); }
    catch { return Clip(executable, 50); }
}

static string? JoinDetail(params string?[] parts)
{
    var values = parts
        .Where(static part => !string.IsNullOrWhiteSpace(part))
        .Select(static part => part!.Trim())
        .ToArray();
    return values.Length == 0 ? null : string.Join(" | ", values);
}

static string? Clip(string? value, int max = 90)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    var cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    return cleaned.Length <= max ? cleaned : cleaned[..Math.Max(1, max - 3)] + "...";
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
