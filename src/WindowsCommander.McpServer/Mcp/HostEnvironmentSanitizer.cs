namespace WindowsCommander.McpServer.Mcp;

internal static class HostEnvironmentSanitizer
{
    private static readonly string[] SecretMarkers =
    {
        "API_KEY", "APIKEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD",
        "CREDENTIAL", "PRIVATE_KEY", "AUTHORIZATION", "CONNECTION_STRING"
    };

    public static IReadOnlyList<string> ScrubCurrentProcess()
    {
        if (AllowsInheritedSecrets())
        {
            return Array.Empty<string>();
        }

        var removed = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in
                 Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process))
        {
            var name = entry.Key?.ToString();
            if (string.IsNullOrWhiteSpace(name)
                || name.StartsWith("WINDOWS_COMMANDER_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!SecretMarkers.Any(marker =>
                    name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
            removed.Add(name);
        }

        return removed;
    }

    private static bool AllowsInheritedSecrets()
    {
        var value = Environment.GetEnvironmentVariable("WINDOWS_COMMANDER_ALLOW_HOST_SECRETS");
        return value is "1" or "true" or "TRUE" or "True" or "yes";
    }
}
