using System.Text;
using System.Text.Json;
using WindowsCommander.Core.Safety;

namespace WindowsCommander.Safety.Audit;

public sealed class PersistentAuditLog : IAuditLog
{
    private const long DefaultMaxFileBytes = 8L * 1024 * 1024;
    private readonly object gate = new();
    private readonly Queue<AuditEntry> entries = new();
    private readonly int capacity;
    private readonly string filePath;
    private readonly long maxFileBytes;

    public PersistentAuditLog(string filePath, int capacity = 500, long maxFileBytes = DefaultMaxFileBytes)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Audit log path must not be empty.", nameof(filePath));
        }

        this.filePath = Path.GetFullPath(filePath);
        this.capacity = Math.Max(1, capacity);
        this.maxFileBytes = Math.Max(64 * 1024, maxFileBytes);
    }

    public static string GetDefaultPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = AppContext.BaseDirectory;
        }

        return Path.Combine(localAppData, "WindowsCommander", "audit.jsonl");
    }

    public void Record(AuditEntry entry)
    {
        lock (gate)
        {
            entries.Enqueue(entry);
            while (entries.Count > capacity)
            {
                entries.Dequeue();
            }

            TryPersist(entry with { RedactedArguments = Redact(entry.RedactedArguments) });
        }
    }

    public IReadOnlyList<AuditEntry> GetRecent(int limit, bool includeSensitiveArguments)
    {
        lock (gate)
        {
            var boundedLimit = Math.Clamp(limit, 1, capacity);

            if (includeSensitiveArguments && entries.Count > 0)
            {
                return entries
                    .Reverse()
                    .Take(boundedLimit)
                    .ToArray();
            }

            var persisted = ReadPersisted(boundedLimit);
            if (persisted.Count > 0)
            {
                return persisted;
            }

            return entries
                .Reverse()
                .Take(boundedLimit)
                .Select(entry => entry with { RedactedArguments = Redact(entry.RedactedArguments) })
                .ToArray();
        }
    }

    private void TryPersist(AuditEntry entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            RotateIfNeeded();
            File.AppendAllText(
                filePath,
                JsonSerializer.Serialize(entry) + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Audit persistence failed: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"Audit persistence failed: {exception.Message}");
        }
    }

    private IReadOnlyList<AuditEntry> ReadPersisted(int limit)
    {
        var recent = new Queue<AuditEntry>(limit);

        foreach (var path in new[] { filePath + ".1", filePath })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var entry = JsonSerializer.Deserialize<AuditEntry>(line);
                        if (entry is null)
                        {
                            continue;
                        }

                        recent.Enqueue(entry);
                        while (recent.Count > limit)
                        {
                            recent.Dequeue();
                        }
                    }
                    catch (JsonException)
                    {
                        // Ignore a corrupt or partially written line and keep
                        // the remaining durable history readable.
                    }
                }
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"Audit read failed: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                Console.Error.WriteLine($"Audit read failed: {exception.Message}");
            }
        }

        return recent.Reverse().ToArray();
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(filePath) || new FileInfo(filePath).Length < maxFileBytes)
        {
            return;
        }

        var backupPath = filePath + ".1";
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        File.Move(filePath, backupPath);
    }

    private static IReadOnlyDictionary<string, object?> Redact(IReadOnlyDictionary<string, object?> arguments)
    {
        return arguments.ToDictionary(
            pair => pair.Key,
            pair => IsSensitive(pair.Key) ? "***REDACTED***" : RedactValue(pair.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private static object? RedactValue(object? value)
    {
        return value switch
        {
            null => null,
            JsonElement element => RedactJsonElement(element),
            IReadOnlyDictionary<string, object?> dictionary => Redact(dictionary),
            IDictionary<string, object?> dictionary => Redact(
                new Dictionary<string, object?>(dictionary, StringComparer.OrdinalIgnoreCase)),
            IReadOnlyDictionary<string, string> dictionary => dictionary.ToDictionary(
                pair => pair.Key,
                pair => IsSensitive(pair.Key) ? "***REDACTED***" : pair.Value,
                StringComparer.OrdinalIgnoreCase),
            IDictionary<string, string> dictionary => dictionary.ToDictionary(
                pair => pair.Key,
                pair => IsSensitive(pair.Key) ? "***REDACTED***" : pair.Value,
                StringComparer.OrdinalIgnoreCase),
            IEnumerable<object?> sequence => sequence.Select(RedactValue).ToArray(),
            _ => value
        };
    }

    private static object? RedactJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => IsSensitive(property.Name)
                    ? (object?)"***REDACTED***"
                    : RedactJsonElement(property.Value),
                StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => element.EnumerateArray().Select(RedactJsonElement).ToArray(),
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
        return key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("key", StringComparison.OrdinalIgnoreCase);
    }
}
