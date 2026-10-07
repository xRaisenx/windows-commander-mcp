using System.Text.Json;
using WindowsCommander.Core.Safety;
using WindowsCommander.Safety.Audit;

namespace WindowsCommander.Tests;

public class PersistentAuditLogTests
{
    [Fact]
    public void GetRecent_SurvivesRestart_AndPersistsOnlyRedactedArguments()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "windows-commander-tests", Guid.NewGuid().ToString("N"));
        var auditPath = Path.Combine(tempDirectory, "audit.jsonl");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var first = new PersistentAuditLog(auditPath);
            first.Record(new AuditEntry(
                "op-persisted",
                "execute_process",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                "success",
                new Dictionary<string, object?>
                {
                    ["apiKey"] = "top-secret",
                    ["environment"] = new Dictionary<string, string>
                    {
                        ["MY_TOKEN"] = "nested-secret",
                        ["VISIBLE"] = "safe-value"
                    }
                },
                null));

            var persistedText = File.ReadAllText(auditPath);
            Assert.DoesNotContain("top-secret", persistedText, StringComparison.Ordinal);
            Assert.DoesNotContain("nested-secret", persistedText, StringComparison.Ordinal);
            Assert.Contains("***REDACTED***", persistedText, StringComparison.Ordinal);

            var second = new PersistentAuditLog(auditPath);
            var entries = second.GetRecent(limit: 10, includeSensitiveArguments: false);

            var entry = Assert.Single(entries);
            Assert.Equal("op-persisted", entry.OperationId);
            Assert.Equal("execute_process", entry.ToolName);

            var serializedArguments = JsonSerializer.Serialize(entry.RedactedArguments);
            Assert.DoesNotContain("top-secret", serializedArguments, StringComparison.Ordinal);
            Assert.DoesNotContain("nested-secret", serializedArguments, StringComparison.Ordinal);
            Assert.Contains("***REDACTED***", serializedArguments, StringComparison.Ordinal);
            Assert.Contains("safe-value", serializedArguments, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
