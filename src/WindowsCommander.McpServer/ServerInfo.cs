using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace WindowsCommander.McpServer;

/// <summary>Identity and provenance reported by the running rescue server.</summary>
public static class ServerInfo
{
    public const string Name = "windows-commander-mcp";

    private static readonly Assembly ExecutingAssembly = Assembly.GetExecutingAssembly();

    public static string InformationalVersion { get; } =
        ExecutingAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? ExecutingAssembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public static string Version { get; } = CleanVersion(InformationalVersion, ExecutingAssembly.GetName().Version);

    public static string? SourceRevision { get; } = ResolveSourceRevision();

    public static string? ExecutableSha256 { get; } = ResolveExecutableSha256();

    public static DateTimeOffset ProcessStartedAt { get; } = ResolveProcessStart();

    public static string CleanVersion(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var plus = informationalVersion.IndexOf('+');
            return plus >= 0 ? informationalVersion[..plus] : informationalVersion;
        }

        return assemblyVersion?.ToString(3) ?? "0.0.0";
    }

    private static string? ResolveSourceRevision()
    {
        var explicitRevision = ExecutingAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                attribute.Key.Equals("RepositoryCommit", StringComparison.OrdinalIgnoreCase)
                || attribute.Key.Equals("SourceRevisionId", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (!string.IsNullOrWhiteSpace(explicitRevision))
        {
            return explicitRevision;
        }

        var plus = InformationalVersion.IndexOf('+');
        return plus >= 0 && plus + 1 < InformationalVersion.Length
            ? InformationalVersion[(plus + 1)..]
            : null;
    }

    private static string? ResolveExecutableSha256()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTimeOffset ResolveProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
