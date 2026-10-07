namespace WindowsCommander.McpServer.Mcp;

internal static class EnvironmentSanitizer
{
    private static readonly string[] SecretMarkers =
    {
        "API_KEY", "APIKEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "PRIVATE_KEY"
    };

    public static IReadOnlyList<string> ScrubCurrentProcess()
    {
        var removed = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process))
        {
            var name = entry.Key?.ToString();
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("WINDOWS_COMMANDER_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!SecretMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
            removed.Add(name);
        }

        return removed;
    }
}
