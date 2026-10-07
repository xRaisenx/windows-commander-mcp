using System.Text.Json;
using WindowsCommander.Core.Safety;
using WindowsCommander.Safety.Audit;

namespace WindowsCommander.Tests;

public class PersistentAuditLogTests
{
    [Fact]
    public void PersistsAndReloadsRecentEntries_WithSensitiveArgumentsRedactedAtRest()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "windows-commander-tests", Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "audit.jsonl");

        try
        {
            var first = new PersistentAuditLog(path, capacity: 10);
            first.Record(new AuditEntry(
                "op-1",
                "execute_process",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                "success",
                new Dictionary<string, object?>
                {
                    ["apiKey"] = "secret-value",
                    ["name"] = "visible-value",
                    ["command"] = "run --token command-secret",
                    ["environment"] = JsonSerializer.Deserialize<JsonElement>("{\"NORMAL\":\"visible-nested\",\"ACCESS_TOKEN\":\"nested-secret\"}")
                },
                null));

            var reloaded = new PersistentAuditLog(path, capacity: 10);
            var entries = reloaded.GetRecent(limit: 10, includeSensitiveArguments: true);

            Assert.Single(entries);
            Assert.Equal("op-1", entries[0].OperationId);
            Assert.Equal("***REDACTED***", entries[0].RedactedArguments["apiKey"]?.ToString());
            Assert.Equal("visible-value", entries[0].RedactedArguments["name"]?.ToString());
            Assert.Equal("***REDACTED***", entries[0].RedactedArguments["command"]?.ToString());
            Assert.Equal("***REDACTED***", entries[0].RedactedArguments["environment"]?.ToString());

            var persistedText = File.ReadAllText(path);
            Assert.DoesNotContain("secret-value", persistedText, StringComparison.Ordinal);
            Assert.DoesNotContain("command-secret", persistedText, StringComparison.Ordinal);
            Assert.DoesNotContain("nested-secret", persistedText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void CapacityIsPreservedAcrossRestart()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "windows-commander-tests", Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "audit.jsonl");

        try
        {
            var first = new PersistentAuditLog(path, capacity: 2);
            for (var index = 1; index <= 3; index++)
            {
                first.Record(new AuditEntry(
                    $"op-{index}",
                    "list_processes",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    "success",
                    new Dictionary<string, object?>(),
                    null));
            }

            var reloaded = new PersistentAuditLog(path, capacity: 2);
            var entries = reloaded.GetRecent(limit: 10, includeSensitiveArguments: false);

            Assert.Equal(2, entries.Count);
            Assert.Equal("op-3", entries[0].OperationId);
            Assert.Equal("op-2", entries[1].OperationId);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
