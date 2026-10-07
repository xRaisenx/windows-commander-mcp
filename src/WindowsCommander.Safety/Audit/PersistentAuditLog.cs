using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using WindowsCommander.Core.Safety;

namespace WindowsCommander.Safety.Audit;

public sealed class PersistentAuditLog : IAuditLog
{
    private const long CompactThresholdBytes = 4L * 1024L * 1024L;
    private const string RedactedValue = "***REDACTED***";
    private static readonly HashSet<string> AlwaysRedactedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "arguments",
        "command",
        "content",
        "environment",
        "text",
        "value"
    };

    private static readonly string[] SensitiveNameMarkers =
    {
        "password", "passwd", "token", "secret", "api_key", "apikey",
        "private_key", "credential", "authorization", "connection_string"
    };

    private readonly object gate = new();
    private readonly object persistenceGate = new();
    private readonly Queue<AuditEntry> entries = new();
    private readonly Channel<AuditEntry> persistenceQueue;
    private readonly Task persistenceWorker;
    private readonly int capacity;
    private readonly string path;

    public PersistentAuditLog(string? path = null, int capacity = 500)
    {
        this.capacity = Math.Max(1, capacity);
        this.path = ResolvePath(path);
        persistenceQueue = Channel.CreateBounded<AuditEntry>(new BoundedChannelOptions(Math.Max(256, this.capacity * 2))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        LoadExisting();
        persistenceWorker = Task.Run(PersistenceLoopAsync);
    }

    public string Path => path;

    public void Record(AuditEntry entry)
    {
        lock (gate)
        {
            entries.Enqueue(entry);
            TrimToCapacity();
        }

        var persisted = entry with { RedactedArguments = Redact(entry.RedactedArguments) };

        // High-risk mutations pay the small durability cost synchronously.
        // Ordinary reads and medium-risk operations leave file I/O/compaction
        // off the request hot path.
        if (entry.Risk == RiskLevel.High)
        {
            PersistOne(persisted);
            return;
        }

        if (!persistenceQueue.Writer.TryWrite(persisted))
        {
            // Backpressure must not silently lose audit evidence. A saturated
            // queue falls back to synchronous persistence rather than dropping.
            PersistOne(persisted);
        }
    }

    public IReadOnlyList<AuditEntry> GetRecent(int limit, bool includeSensitiveArguments)
    {
        lock (gate)
        {
            return entries
                .Reverse()
                .Take(Math.Clamp(limit, 1, capacity))
                .Select(entry => includeSensitiveArguments
                    ? entry
                    : entry with { RedactedArguments = Redact(entry.RedactedArguments) })
                .ToArray();
        }
    }

    private async Task PersistenceLoopAsync()
    {
        await foreach (var entry in persistenceQueue.Reader.ReadAllAsync())
        {
            PersistOne(entry);
        }
    }

    private void PersistOne(AuditEntry persisted)
    {
        lock (persistenceGate)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(path, JsonSerializer.Serialize(persisted) + Environment.NewLine);

                if (new FileInfo(path).Length > CompactThresholdBytes)
                {
                    Compact();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"Failed to persist Windows Commander audit entry: {exception.Message}");
            }
        }
    }

    private void LoadExisting()
    {
        lock (gate)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                foreach (var line in File.ReadLines(path).TakeLast(capacity))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var entry = JsonSerializer.Deserialize<AuditEntry>(line);
                    if (entry is not null)
                    {
                        entries.Enqueue(entry);
                    }
                }

                TrimToCapacity();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"Failed to load Windows Commander audit history: {exception.Message}");
            }
        }
    }

    private void Compact()
    {
        AuditEntry[] snapshot;
        lock (gate)
        {
            snapshot = entries
                .Select(entry => entry with { RedactedArguments = Redact(entry.RedactedArguments) })
                .ToArray();
        }

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        var lines = snapshot.Select(entry => JsonSerializer.Serialize(entry));
        File.WriteAllLines(tempPath, lines);
        File.Move(tempPath, path, overwrite: true);
    }

    private void TrimToCapacity()
    {
        while (entries.Count > capacity)
        {
            entries.Dequeue();
        }
    }

    private static string ResolvePath(string? configuredPath)
    {
        var path = configuredPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Environment.GetEnvironmentVariable("WINDOWS_COMMANDER_AUDIT_LOG");
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            return System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }

        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsCommander",
            "audit.jsonl");
    }

    private static IReadOnlyDictionary<string, object?> Redact(IReadOnlyDictionary<string, object?> arguments)
    {
        return arguments.ToDictionary(
            pair => pair.Key,
            pair => RedactValue(pair.Key, pair.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private static object? RedactValue(string key, object? value)
    {
        if (IsSensitive(key))
        {
            return RedactedValue;
        }

        return value switch
        {
            JsonElement element => RedactJsonElement(element),
            IReadOnlyDictionary<string, object?> dictionary => Redact(dictionary),
            IDictionary<string, object?> dictionary => Redact(
                new Dictionary<string, object?>(dictionary, StringComparer.OrdinalIgnoreCase)),
            IEnumerable<object?> sequence => sequence.Select(item => RedactValue(string.Empty, item)).ToArray(),
            _ => value
        };
    }

    private static object? RedactJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => RedactValue(property.Name, property.Value),
                StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(RedactJsonElement)
                .ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    private static bool IsSensitive(string key)
    {
        if (AlwaysRedactedKeys.Contains(key))
        {
            return true;
        }

        return SensitiveNameMarkers.Any(marker =>
            key.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
