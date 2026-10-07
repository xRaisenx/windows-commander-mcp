using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WindowsCommander.Windows.Services;

namespace WindowsCommander.McpServer.CodeIntel;

internal sealed class LspClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly Stream input;
    private readonly Stream output;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly ConcurrentDictionary<string, JsonElement> diagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task readerLoop;
    private readonly Task stderrLoop;
    private readonly StringBuilder stderr = new();
    private long nextId;
    private int documentVersion;

    private LspClient(Process process)
    {
        this.process = process;
        input = process.StandardOutput.BaseStream;
        output = process.StandardInput.BaseStream;
        readerLoop = Task.Run(ReadLoopAsync);
        stderrLoop = Task.Run(ReadStderrAsync);
    }

    public int ProcessId => process.Id;
    public bool IsAlive => !process.HasExited;

    public static async Task<LspClient> StartAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workspaceRoot
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _ = ChildEnvironmentSanitizer.ApplyTo(startInfo);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start language server: {executable}");

        var client = new LspClient(process);
        try
        {
            var rootUri = new Uri(Path.GetFullPath(workspaceRoot)).AbsoluteUri;
            _ = await client.RequestAsync("initialize", new
            {
                processId = Environment.ProcessId,
                rootUri,
                workspaceFolders = new[] { new { uri = rootUri, name = Path.GetFileName(Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar)) } },
                capabilities = new
                {
                    textDocument = new
                    {
                        documentSymbol = new { hierarchicalDocumentSymbolSupport = true },
                        publishDiagnostics = new { relatedInformation = true },
                        references = new { }
                    },
                    workspace = new { workspaceFolders = true }
                },
                clientInfo = new { name = "windows-commander-rescue", version = ServerInfo.Version }
            }, TimeSpan.FromSeconds(15), cancellationToken);

            await client.NotifyAsync("initialized", new { }, cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task OpenDocumentAsync(string path, string languageId, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var text = await File.ReadAllTextAsync(fullPath, cancellationToken);
        await NotifyAsync("textDocument/didOpen", new
        {
            textDocument = new
            {
                uri = new Uri(fullPath).AbsoluteUri,
                languageId,
                version = Interlocked.Increment(ref documentVersion),
                text
            }
        }, cancellationToken);
    }

    public async Task ChangeDocumentAsync(string path, string languageId, string text, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        await NotifyAsync("textDocument/didChange", new
        {
            textDocument = new
            {
                uri = new Uri(fullPath).AbsoluteUri,
                version = Interlocked.Increment(ref documentVersion)
            },
            contentChanges = new[] { new { text } }
        }, cancellationToken);
    }

    public Task<JsonElement> DocumentSymbolsAsync(string path, CancellationToken cancellationToken)
        => RequestAsync("textDocument/documentSymbol", new
        {
            textDocument = new { uri = new Uri(Path.GetFullPath(path)).AbsoluteUri }
        }, TimeSpan.FromSeconds(15), cancellationToken);

    public Task<JsonElement> ReferencesAsync(
        string path,
        int line,
        int character,
        bool includeDeclaration,
        CancellationToken cancellationToken)
        => RequestAsync("textDocument/references", new
        {
            textDocument = new { uri = new Uri(Path.GetFullPath(path)).AbsoluteUri },
            position = new { line, character },
            context = new { includeDeclaration }
        }, TimeSpan.FromSeconds(15), cancellationToken);

    public JsonElement? GetDiagnostics(string path)
    {
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        return diagnostics.TryGetValue(uri, out var value) ? value.Clone() : null;
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDead();
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Failed to allocate LSP request id.");
        }

        try
        {
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token, lifetime.Token);
            return await completion.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            throw new TimeoutException($"Language server request timed out: {method}");
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken)
        => SendAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await output.WriteAsync(header, cancellationToken);
            await output.WriteAsync(body, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var length = await ReadContentLengthAsync(input, lifetime.Token);
                if (length is null)
                {
                    break;
                }

                if (length.Value <= 0 || length.Value > 16 * 1024 * 1024)
                {
                    throw new InvalidDataException($"Invalid LSP Content-Length: {length}");
                }

                var body = new byte[length.Value];
                await ReadExactlyAsync(input, body, lifetime.Token);
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                if (root.TryGetProperty("id", out var idElement)
                    && idElement.ValueKind == JsonValueKind.Number
                    && idElement.TryGetInt64(out var id)
                    && pending.TryGetValue(id, out var completion))
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        completion.TrySetException(new InvalidOperationException($"Language server error: {error.GetRawText()}"));
                    }
                    else if (root.TryGetProperty("result", out var result))
                    {
                        completion.TrySetResult(result.Clone());
                    }
                    else
                    {
                        completion.TrySetResult(JsonSerializer.SerializeToElement<object?>(null));
                    }

                    continue;
                }

                if (root.TryGetProperty("method", out var method)
                    && string.Equals(method.GetString(), "textDocument/publishDiagnostics", StringComparison.Ordinal)
                    && root.TryGetProperty("params", out var parameters)
                    && parameters.TryGetProperty("uri", out var uri))
                {
                    diagnostics[uri.GetString() ?? string.Empty] = parameters.Clone();
                }
            }
        }
        catch (Exception exception)
        {
            foreach (var completion in pending.Values)
            {
                completion.TrySetException(new InvalidOperationException(
                    $"Language server transport failed: {exception.Message}. stderr: {GetStderrTail()}",
                    exception));
            }
        }
        finally
        {
            lifetime.Cancel();
        }
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            var buffer = new char[2048];
            while (!lifetime.IsCancellationRequested)
            {
                var read = await process.StandardError.ReadAsync(buffer.AsMemory(0, buffer.Length), lifetime.Token);
                if (read == 0)
                {
                    break;
                }

                lock (stderr)
                {
                    stderr.Append(buffer, 0, read);
                    if (stderr.Length > 32_768)
                    {
                        stderr.Remove(0, stderr.Length - 32_768);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private string GetStderrTail()
    {
        lock (stderr)
        {
            return stderr.ToString();
        }
    }

    private void ThrowIfDead()
    {
        if (lifetime.IsCancellationRequested || process.HasExited)
        {
            throw new InvalidOperationException($"Language server is not running. stderr: {GetStderrTail()}");
        }
    }

    private static async Task<int?> ReadContentLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new List<byte>(256);
        var state = 0;
        var one = new byte[1];

        while (header.Count < 16 * 1024)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                return header.Count == 0 ? null : throw new EndOfStreamException("Language server closed mid-header.");
            }

            var value = one[0];
            header.Add(value);
            state = (state, value) switch
            {
                (0, 13) => 1,
                (1, 10) => 2,
                (2, 13) => 3,
                (3, 10) => 4,
                (_, 13) => 1,
                _ => 0
            };

            if (state == 4)
            {
                var text = Encoding.ASCII.GetString(header.ToArray());
                foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        continue;
                    }

                    if (line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(line[(colon + 1)..].Trim(), out var length))
                    {
                        return length;
                    }
                }

                throw new InvalidDataException("Language server message is missing Content-Length.");
            }
        }

        throw new InvalidDataException("Language server header exceeded 16 KiB.");
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Language server closed mid-message.");
            }

            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!lifetime.IsCancellationRequested)
        {
            try
            {
                _ = await RequestAsync("shutdown", new { }, TimeSpan.FromSeconds(2), CancellationToken.None);
                await NotifyAsync("exit", new { }, CancellationToken.None);
            }
            catch
            {
            }

            lifetime.Cancel();
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        try { await readerLoop; } catch { }
        try { await stderrLoop; } catch { }
        process.Dispose();
        lifetime.Dispose();
        writeGate.Dispose();
    }
}
