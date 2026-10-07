using System.Diagnostics;

namespace WindowsCommander.Windows.Services;

public static class ChildEnvironmentSanitizer
{
    private static readonly string[] SecretMarkers =
    {
        "API_KEY", "APIKEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD",
        "CREDENTIAL", "PRIVATE_KEY", "AUTHORIZATION", "CONNECTION_STRING"
    };

    public static IReadOnlyList<string> ApplyTo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (AllowChildSecrets())
        {
            return Array.Empty<string>();
        }

        var removed = new List<string>();
        foreach (var name in startInfo.Environment.Keys.ToArray())
        {
            if (name.StartsWith("WINDOWS_COMMANDER_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!SecretMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            startInfo.Environment.Remove(name);
            removed.Add(name);
        }

        return removed;
    }

    public static bool AllowsInheritedSecrets => AllowChildSecrets();

    private static bool AllowChildSecrets()
    {
        var value = Environment.GetEnvironmentVariable("WINDOWS_COMMANDER_ALLOW_CHILD_SECRETS");
        return value is "1" or "true" or "TRUE" or "True" or "yes";
    }
}
