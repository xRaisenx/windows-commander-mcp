using WindowsCommander.Core.Services;

namespace WindowsCommander.Windows.Services;

public sealed class EnvironmentService : IEnvironmentService
{
    private static readonly string[] SecretMarkers =
    {
        "password", "passwd", "token", "secret", "api_key", "apikey",
        "private_key", "credential", "authorization", "connection_string"
    };

    public string? GetEnvironmentVariable(string name, string scope)
    {
        ValidateName(name);
        var value = Environment.GetEnvironmentVariable(name, ParseScope(scope));
        if (value is null)
        {
            return null;
        }

        return IsSensitiveName(name) ? "***REDACTED_PRESENT***" : value;
    }

    public void SetEnvironmentVariable(string name, string? value, string scope)
    {
        ValidateName(name);
        Environment.SetEnvironmentVariable(name, value, ParseScope(scope));
    }

    private static bool IsSensitiveName(string name)
    {
        return SecretMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Environment variable name must not be empty.", nameof(name));
        }
    }

    private static EnvironmentVariableTarget ParseScope(string scope)
    {
        return scope.ToLowerInvariant() switch
        {
            "process" => EnvironmentVariableTarget.Process,
            "user" => EnvironmentVariableTarget.User,
            "machine" => EnvironmentVariableTarget.Machine,
            _ => throw new ArgumentException($"Unsupported environment variable scope: {scope}")
        };
    }
}
