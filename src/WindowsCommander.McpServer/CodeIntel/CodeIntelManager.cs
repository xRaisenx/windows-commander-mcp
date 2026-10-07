using System.IO;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WindowsCommander.McpServer.CodeIntel;

public sealed class CodeIntelManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);
    public async Task<object> StartAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workspaceRoot,
        string languageId,
        string? tsserverPath,
        string? tsserverFallbackPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new ArgumentException("Language server executable is required.", nameof(executable));
        }

        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"CodeIntel workspace does not exist: {root}");
        }

        var normalizedLanguageId = NormalizeLanguageId(languageId);
        var usesTypeScriptServer = normalizedLanguageId is "typescript" or "javascript";
        var configuredTsserverPath = usesTypeScriptServer
            ? (string.IsNullOrWhiteSpace(tsserverPath)
                ? Environment.GetEnvironmentVariable("WINDOWS_COMMANDER_TSSERVER_PATH")
                : tsserverPath)
            : null;
        var configuredTsserverFallbackPath = usesTypeScriptServer
            ? (string.IsNullOrWhiteSpace(tsserverFallbackPath)
                ? Environment.GetEnvironmentVariable("WINDOWS_COMMANDER_TSSERVER_FALLBACK_PATH")
                : tsserverFallbackPath)
            : null;

        var id = Guid.NewGuid().ToString("N");
        var client = await LspClient.StartAsync(
            executable,
            arguments,
            root,
            configuredTsserverPath,
            configuredTsserverFallbackPath,
            cancellationToken);
        var session = new Session(id, root, normalizedLanguageId, executable, arguments.ToArray(), client);

        if (!sessions.TryAdd(id, session))
        {
            await client.DisposeAsync();
            throw new InvalidOperationException("Unable to allocate CodeIntel session.");
        }

        return session.Status();
    }
    public object Status(string sessionId)
    {
        var session = GetSession(sessionId);
        return session.Status();
    }

    public async Task<object> SymbolsAsync(string sessionId, string path, CancellationToken cancellationToken)
    {
        var session = GetSession(sessionId);
        var document = await EnsureDocumentAsync(session, path, cancellationToken);
        var result = await session.Client.DocumentSymbolsAsync(document.Path, cancellationToken);

        return new
        {
            session_id = session.Id,
            workspace_root = session.WorkspaceRoot,
            language_id = session.LanguageId,
            document_path = document.Path,
            document_sha256 = document.Sha256,
            symbols = result
        };
    }

    public async Task<object> DefinitionAsync(
        string sessionId,
        string path,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        var session = GetSession(sessionId);
        var document = await EnsureDocumentAsync(session, path, cancellationToken);
        var result = await session.Client.DefinitionAsync(document.Path, line, character, cancellationToken);

        return new
        {
            session_id = session.Id,
            workspace_root = session.WorkspaceRoot,
            language_id = session.LanguageId,
            document_path = document.Path,
            document_sha256 = document.Sha256,
            definition = result
        };
    }

    public async Task<object> ReferencesAsync(
        string sessionId,
        string path,
        int line,
        int character,
        bool includeDeclaration,
        CancellationToken cancellationToken)
    {
        var session = GetSession(sessionId);
        var document = await EnsureDocumentAsync(session, path, cancellationToken);
        var result = await session.Client.ReferencesAsync(
            document.Path,
            line,
            character,
            includeDeclaration,
            cancellationToken);

        return new
        {
            session_id = session.Id,
            workspace_root = session.WorkspaceRoot,
            language_id = session.LanguageId,
            document_path = document.Path,
            document_sha256 = document.Sha256,
            references = result
        };
    }

    public async Task<int> ErrorCountAsync(
        string sessionId,
        string path,
        CancellationToken cancellationToken)
    {
        var session = GetSession(sessionId);
        var document = await EnsureDocumentAsync(session, path, cancellationToken);
        var diagnostics = await WaitForDiagnosticsAsync(session, document.Path, cancellationToken);
        return CountErrors(diagnostics);
    }
    public async Task<object> DiagnosticsAsync(
        string sessionId,
        string path,
        CancellationToken cancellationToken)
    {
        var session = GetSession(sessionId);
        var document = await EnsureDocumentAsync(session, path, cancellationToken);
        var diagnostics = await WaitForDiagnosticsAsync(session, document.Path, cancellationToken);

        return new
        {
            session_id = session.Id,
            workspace_root = session.WorkspaceRoot,
            language_id = session.LanguageId,
            document_path = document.Path,
            document_sha256 = document.Sha256,
            diagnostics,
            error_count = CountErrors(diagnostics)
        };
    }

    public async Task<object> SafeDeletePreflightAsync(
        string sessionId,
        string path,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        var session = GetSession(sessionId);
        var document = await EnsureDocumentAsync(session, path, cancellationToken);
        var references = await session.Client.ReferencesAsync(
            document.Path,
            line,
            character,
            includeDeclaration: false,
            cancellationToken);

        var count = references.ValueKind == JsonValueKind.Array ? references.GetArrayLength() : 0;
        return new
        {
            session_id = session.Id,
            document_path = document.Path,
            document_sha256 = document.Sha256,
            reference_count = count,
            safe_to_delete = count == 0,
            references
        };
    }

    internal async Task<SymbolReplacementProposal> CreateReplacementProposalAsync(
        string sessionId,
        string path,
        string symbolName,
        string replacement,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbolName))
        {
            throw new ArgumentException("Symbol name is required.", nameof(symbolName));
        }

        var session = GetSession(sessionId);
        var document = await EnsureDocumentAsync(session, path, cancellationToken);
        var symbols = await session.Client.DocumentSymbolsAsync(document.Path, cancellationToken);

        if (!TryFindSymbolRange(symbols, symbolName, out var range))
        {
            throw new InvalidOperationException($"Symbol was not found: {symbolName}");
        }

        var text = await File.ReadAllTextAsync(document.Path, cancellationToken);
        var start = PositionToOffset(text, range.StartLine, range.StartCharacter);
        var end = PositionToOffset(text, range.EndLine, range.EndCharacter);
        if (start < 0 || end < start || end > text.Length)
        {
            throw new InvalidOperationException("Language server returned an invalid symbol range.");
        }

        var newText = string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(end));
        return new SymbolReplacementProposal(
            session.Id,
            session.WorkspaceRoot,
            session.LanguageId,
            document.Path,
            symbolName,
            document.Sha256,
            text,
            newText,
            range.StartLine,
            range.StartCharacter,
            range.EndLine,
            range.EndCharacter);
    }

    public async Task RefreshDocumentAsync(
        string sessionId,
        string path,
        CancellationToken cancellationToken)
    {
        _ = await EnsureDocumentAsync(GetSession(sessionId), path, cancellationToken, forceRefresh: true);
    }

    public async Task<object> StopAsync(string sessionId)
    {
        if (!sessions.TryRemove(sessionId, out var session))
        {
            throw new ArgumentException($"Unknown CodeIntel session: {sessionId}", nameof(sessionId));
        }

        await session.Client.DisposeAsync();
        return new { session_id = sessionId, stopped = true };
    }

    private Session GetSession(string sessionId)
    {
        if (!sessions.TryGetValue(sessionId, out var session))
        {
            throw new ArgumentException($"Unknown CodeIntel session: {sessionId}", nameof(sessionId));
        }

        if (!session.Client.IsAlive)
        {
            sessions.TryRemove(sessionId, out _);
            throw new InvalidOperationException($"CodeIntel language server exited: {sessionId}");
        }

        return session;
    }

    private static async Task<DocumentIdentity> EnsureDocumentAsync(
        Session session,
        string path,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureInsideWorkspace(session.WorkspaceRoot, fullPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"CodeIntel document does not exist: {fullPath}", fullPath);
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var text = DecodeText(bytes);

        string? previousSha;
        lock (session.Gate)
        {
            session.DocumentHashes.TryGetValue(fullPath, out previousSha);
        }

        if (previousSha is null)
        {
            await session.Client.OpenDocumentAsync(fullPath, session.LanguageId, cancellationToken);
        }
        else if (forceRefresh || !string.Equals(previousSha, sha, StringComparison.OrdinalIgnoreCase))
        {
            await session.Client.ChangeDocumentAsync(fullPath, session.LanguageId, text, cancellationToken);
        }

        lock (session.Gate)
        {
            session.DocumentHashes[fullPath] = sha;
        }

        return new DocumentIdentity(fullPath, sha);
    }

    private static async Task<JsonElement?> WaitForDiagnosticsAsync(
        Session session,
        string path,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostics = session.Client.GetDiagnostics(path);
            if (diagnostics is not null)
            {
                return diagnostics;
            }

            await Task.Delay(50, cancellationToken);
        }

        return null;
    }

    public static int CountErrors(JsonElement? diagnostics)
    {
        if (diagnostics is null
            || diagnostics.Value.ValueKind != JsonValueKind.Object
            || !diagnostics.Value.TryGetProperty("diagnostics", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var count = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("severity", out var severity)
                || severity.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            if (severity.TryGetInt32(out var value) && value == 1)
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryFindSymbolRange(JsonElement element, string symbolName, out SymbolRange range)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (TryFindSymbolRange(child, symbolName, out range))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("name", out var name)
                && string.Equals(name.GetString(), symbolName, StringComparison.Ordinal)
                && TryReadRange(element, out range))
            {
                return true;
            }

            if (element.TryGetProperty("children", out var children)
                && TryFindSymbolRange(children, symbolName, out range))
            {
                return true;
            }
        }

        range = default;
        return false;
    }

    private static bool TryReadRange(JsonElement symbol, out SymbolRange range)
    {
        if (!symbol.TryGetProperty("range", out var rangeElement)
            || !rangeElement.TryGetProperty("start", out var start)
            || !rangeElement.TryGetProperty("end", out var end)
            || !TryReadPosition(start, out var startLine, out var startCharacter)
            || !TryReadPosition(end, out var endLine, out var endCharacter))
        {
            range = default;
            return false;
        }

        range = new SymbolRange(startLine, startCharacter, endLine, endCharacter);
        return true;
    }

    private static bool TryReadPosition(
        JsonElement position,
        out int line,
        out int character)
    {
        line = 0;
        character = 0;
        return position.TryGetProperty("line", out var lineElement)
            && lineElement.TryGetInt32(out line)
            && position.TryGetProperty("character", out var characterElement)
            && characterElement.TryGetInt32(out character);
    }

    private static int PositionToOffset(string text, int line, int character)
    {
        if (line < 0 || character < 0)
        {
            return -1;
        }

        var currentLine = 0;
        var offset = 0;
        while (currentLine < line && offset < text.Length)
        {
            var newline = text.IndexOf('\n', offset);
            if (newline < 0)
            {
                return -1;
            }

            offset = newline + 1;
            currentLine++;
        }

        var lineEnd = text.IndexOf('\n', offset);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        if (lineEnd > offset && text[lineEnd - 1] == '\r')
        {
            lineEnd--;
        }

        var lineLength = lineEnd - offset;
        return character <= lineLength ? offset + character : -1;
    }

    private static void EnsureInsideWorkspace(string workspaceRoot, string path)
    {
        var relative = Path.GetRelativePath(workspaceRoot, path);
        if (relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException($"CodeIntel path is outside workspace root: {path}");
        }
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static string NormalizeLanguageId(string languageId)
    {
        return languageId.Trim().ToLowerInvariant() switch
        {
            "cs" or "csharp" => "csharp",
            "ts" or "typescript" => "typescript",
            "js" or "javascript" => "javascript",
            "py" or "python" => "python",
            var value when !string.IsNullOrWhiteSpace(value) => value,
            _ => throw new ArgumentException("Language id is required.", nameof(languageId))
        };
    }

    public async ValueTask DisposeAsync()
    {
        var values = sessions.Values.ToArray();
        sessions.Clear();
        foreach (var session in values)
        {
            try { await session.Client.DisposeAsync(); } catch { }
        }
    }

    private sealed class Session
    {
        public Session(
            string id,
            string workspaceRoot,
            string languageId,
            string executable,
            IReadOnlyList<string> arguments,
            LspClient client)
        {
            Id = id;
            WorkspaceRoot = workspaceRoot;
            LanguageId = languageId;
            Executable = executable;
            Arguments = arguments;
            Client = client;
        }

        public string Id { get; }
        public string WorkspaceRoot { get; }
        public string LanguageId { get; }
        public string Executable { get; }
        public IReadOnlyList<string> Arguments { get; }
        public LspClient Client { get; }
        public object Gate { get; } = new();
        public Dictionary<string, string> DocumentHashes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public object Status() => new
        {
            session_id = Id,
            workspace_root = WorkspaceRoot,
            language_id = LanguageId,
            executable = Executable,
            arguments = Arguments,
            process_id = Client.ProcessId,
            alive = Client.IsAlive,
            opened_documents = DocumentHashes.Count
        };
    }

    private readonly record struct DocumentIdentity(string Path, string Sha256);
    private readonly record struct SymbolRange(int StartLine, int StartCharacter, int EndLine, int EndCharacter);
}

internal sealed record SymbolReplacementProposal(
    string SessionId,
    string WorkspaceRoot,
    string LanguageId,
    string Path,
    string SymbolName,
    string ExpectedSha256,
    string OriginalText,
    string NewText,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter);
