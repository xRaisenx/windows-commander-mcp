using System.IO;
using System.Net.Sockets;

namespace WindowsCommander.McpServer.Mcp;

public sealed class SerenaRescueService
{
    private readonly ProcessOperationSupervisor processOperations;

    public SerenaRescueService(ProcessOperationSupervisor processOperations)
    {
        this.processOperations = processOperations;
    }

    public async Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        var configuration = ResolveConfiguration();
        var routerTask = TestPortAsync(configuration.RouterPort, cancellationToken);
        var masterTask = TestPortAsync(configuration.MasterPort, cancellationToken);
        await Task.WhenAll(routerTask, masterTask);

        return new
        {
            configured = configuration.Root is not null,
            root = configuration.Root,
            start_script = configuration.StartScript,
            start_script_exists = configuration.StartScript is not null && File.Exists(configuration.StartScript),
            router = new
            {
                host = "127.0.0.1",
                port = configuration.RouterPort,
                listening = routerTask.Result
            },
            master = new
            {
                host = "127.0.0.1",
                port = configuration.MasterPort,
                listening = masterTask.Result
            },
            listeners_ready = routerTask.Result && masterTask.Result
        };
    }

    public object Restart(int? timeoutMs)
    {
        var configuration = ResolveConfiguration();
        if (configuration.Root is null || !Directory.Exists(configuration.Root))
        {
            throw new InvalidOperationException(
                "Serena root is not configured. Set WINDOWS_COMMANDER_SERENA_ROOT.");
        }

        if (configuration.StartScript is null || !File.Exists(configuration.StartScript))
        {
            throw new FileNotFoundException(
                "Serena start/recovery script was not found. Set WINDOWS_COMMANDER_SERENA_START_SCRIPT.",
                configuration.StartScript);
        }

        var launch = GetLaunchCommand(configuration.StartScript);
        var operation = processOperations.Start(
            launch.Executable,
            launch.Arguments,
            configuration.Root,
            timeoutMs ?? 180_000,
            512 * 1024);

        return new
        {
            action = "serena_restart",
            operation
        };
    }

    public object GetRecentLogs(int? maxFiles, int? linesPerFile)
    {
        var configuration = ResolveConfiguration();
        if (configuration.Root is null || !Directory.Exists(configuration.Root))
        {
            throw new InvalidOperationException(
                "Serena root is not configured. Set WINDOWS_COMMANDER_SERENA_ROOT.");
        }

        var fileLimit = Math.Clamp(maxFiles ?? 3, 1, 10);
        var lineLimit = Math.Clamp(linesPerFile ?? 40, 1, 200);
        var candidateRoots = new[]
        {
            Path.Combine(configuration.Root, ".serena"),
            Path.Combine(configuration.Root, "logs")
        };

        var files = candidateRoots
            .Where(Directory.Exists)
            .SelectMany(root => EnumerateLogFiles(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Take(fileLimit)
            .Select(info => new
            {
                path = info.FullName,
                modified_at = info.LastWriteTimeUtc,
                size = info.Length,
                tail = ReadTail(info.FullName, lineLimit)
            })
            .ToArray();

        return new
        {
            root = configuration.Root,
            count = files.Length,
            files
        };
    }

    private static IEnumerable<string> EnumerateLogFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var pattern in new[] { "*.log", "*.jsonl", "*.txt" })
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, pattern, options);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private static string[] ReadTail(string path, int lineLimit)
    {
        try
        {
            var queue = new Queue<string>(lineLimit);
            foreach (var line in File.ReadLines(path))
            {
                if (queue.Count == lineLimit)
                {
                    queue.Dequeue();
                }

                queue.Enqueue(line.Length <= 4096 ? line : line[..4096]);
            }

            return queue.ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new[] { $"<unable to read: {exception.Message}>" };
        }
    }

    private static async Task<bool> TestPortAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await client.ConnectAsync("127.0.0.1", port, linked.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static SerenaConfiguration ResolveConfiguration()
    {
        var root = Environment.GetEnvironmentVariable("WINDOWS_COMMANDER_SERENA_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            const string nmbvDefault = @"D:\DEV\nmbv-v3-clean";
            root = Directory.Exists(nmbvDefault) ? nmbvDefault : null;
        }

        if (!string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetFullPath(root);
        }

        var script = Environment.GetEnvironmentVariable("WINDOWS_COMMANDER_SERENA_START_SCRIPT");
        if (string.IsNullOrWhiteSpace(script) && root is not null)
        {
            script = new[]
            {
                Path.Combine(root, "Start-NMBV-Serena.ps1"),
                Path.Combine(root, "START-NMBV-V3-SERENA.sh"),
                Path.Combine(root, "Start-NMBV-Serena.cmd"),
                Path.Combine(root, "Start-NMBV-Serena.bat")
            }.FirstOrDefault(File.Exists);
        }

        if (!string.IsNullOrWhiteSpace(script))
        {
            script = Path.GetFullPath(script);
        }

        return new SerenaConfiguration(
            root,
            script,
            ReadPort("WINDOWS_COMMANDER_SERENA_ROUTER_PORT", 9321),
            ReadPort("WINDOWS_COMMANDER_SERENA_MASTER_PORT", 9330));
    }

    private static LaunchCommand GetLaunchCommand(string script)
    {
        var extension = Path.GetExtension(script).ToLowerInvariant();
        return extension switch
        {
            ".ps1" => new LaunchCommand(
                "pwsh.exe",
                new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }),
            ".sh" => new LaunchCommand(
                "bash.exe",
                new[] { script }),
            ".cmd" or ".bat" => new LaunchCommand(
                "cmd.exe",
                new[] { "/d", "/s", "/c", script }),
            _ => throw new NotSupportedException($"Unsupported Serena recovery script type: {extension}")
        };
    }
    private static int ReadPort(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var port) && port is > 0 and <= 65535
            ? port
            : fallback;
    }

    private sealed record LaunchCommand(string Executable, IReadOnlyList<string> Arguments);

    private sealed record SerenaConfiguration(
        string? Root,
        string? StartScript,
        int RouterPort,
        int MasterPort);
}
