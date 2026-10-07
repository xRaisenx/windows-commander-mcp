using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WindowsCommander.Core.Models;
using WindowsCommander.Core.Services;

namespace WindowsCommander.Windows.Services;

public sealed class FileSystemService : IFileSystemService
{
    private const int DefaultMaxReadBytes = 256 * 1024;
    private const int MaximumMaxReadBytes = 4 * 1024 * 1024;
    private const int CopyBufferSize = 128 * 1024;

    public IReadOnlyList<DirectoryEntry> ListDirectory(
        string path,
        bool recursive,
        bool includeHidden,
        string? pattern,
        int? maxResults,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maxResults ?? 1000, 1, 5000);
        var directory = new DirectoryInfo(NormalizeExistingDirectory(path));
        var attributesToSkip = FileAttributes.ReparsePoint;
        if (!includeHidden)
        {
            attributesToSkip |= FileAttributes.Hidden | FileAttributes.System;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = attributesToSkip
        };
        var results = new List<DirectoryEntry>(Math.Min(limit, 256));

        foreach (var entry in directory.EnumerateFileSystemInfos(string.IsNullOrWhiteSpace(pattern) ? "*" : pattern, options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(ToDirectoryEntry(entry));
            if (results.Count >= limit)
            {
                break;
            }
        }

        return results;
    }

    public async Task<FileReadResult> ReadFileAsync(
        string path,
        string? encoding,
        int? maxBytes,
        bool asBase64,
        CancellationToken cancellationToken)
    {
        var fullPath = NormalizeExistingFile(path);
        var limit = Math.Clamp(maxBytes ?? DefaultMaxReadBytes, 1, MaximumMaxReadBytes);
        var (bytes, totalBytes) = await ReadLimitedBytesAsync(fullPath, limit, cancellationToken);
        var truncated = bytes.LongLength < totalBytes;

        if (asBase64)
        {
            return new FileReadResult(fullPath, Convert.ToBase64String(bytes), "base64", IsBase64: true, bytes.Length)
            {
                TotalBytes = totalBytes,
                Truncated = truncated
            };
        }

        var textEncoding = ResolveEncoding(encoding);
        return new FileReadResult(
            fullPath,
            DecodePrefix(bytes, textEncoding, truncated),
            textEncoding.WebName,
            IsBase64: false,
            bytes.Length)
        {
            TotalBytes = totalBytes,
            Truncated = truncated
        };
    }

    public async Task<FileWriteResult> WriteFileAsync(
        string path,
        string content,
        string? encoding,
        bool overwrite,
        bool createDirectories,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        await using var mutationLock = await PathMutationLock.Shared.AcquireAsync(new[] { fullPath }, cancellationToken);
        var directoryPath = Path.GetDirectoryName(fullPath);
        var createdDirectory = false;

        if (!string.IsNullOrWhiteSpace(directoryPath) && !Directory.Exists(directoryPath))
        {
            if (!createDirectories)
            {
                throw new DirectoryNotFoundException($"Directory does not exist: {directoryPath}");
            }

            Directory.CreateDirectory(directoryPath);
            createdDirectory = true;
        }

        if (File.Exists(fullPath) && !overwrite)
        {
            throw new IOException($"File already exists and overwrite is false: {fullPath}");
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            await VerifyExpectedSha256Async(fullPath, expectedSha256, cancellationToken);
        }

        var bytes = ResolveEncoding(encoding).GetBytes(content);
        var tempPath = CreateSiblingTemporaryPath(fullPath, "write");
        try
        {
            await WriteFileDurablyAsync(tempPath, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // External writers are not governed by our path lock. Revalidate
            // the caller's expected identity at the commit boundary.
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                await VerifyExpectedSha256Async(fullPath, expectedSha256, cancellationToken);
            }

            if (File.Exists(fullPath))
            {
                var existingAttributes = File.GetAttributes(fullPath);
                File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                File.SetAttributes(fullPath, existingAttributes);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }

            return new FileWriteResult(fullPath, bytes.LongLength, createdDirectory);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    public async Task<PathOperationResult> CopyMoveDeletePathAsync(
        string action,
        string sourcePath,
        string? destinationPath,
        bool recursive,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var destinationFullPath = string.IsNullOrWhiteSpace(destinationPath) ? null : Path.GetFullPath(destinationPath);
        await using var mutationLock = await PathMutationLock.Shared.AcquireAsync(
            new[] { sourceFullPath, destinationFullPath },
            cancellationToken);

        switch (action.ToLowerInvariant())
        {
            case "copy":
                await CopyPathAsync(sourceFullPath, RequireDestination(destinationFullPath), recursive, overwrite, cancellationToken);
                break;
            case "move":
                MovePathTransactional(sourceFullPath, RequireDestination(destinationFullPath), overwrite, cancellationToken);
                break;
            case "delete":
                await DeletePathAsync(sourceFullPath, recursive, cancellationToken);
                break;
            case "recycle":
                throw new NotSupportedException("Recycle is not implemented and is not advertised by the MCP schema.");
            default:
                throw new ArgumentException($"Unsupported path action: {action}");
        }

        return new PathOperationResult(action, sourceFullPath, destinationFullPath, Completed: true);
    }

    public async Task<FileProperties> GetFilePropertiesAsync(string path, string? hashAlgorithm, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var info = GetFileSystemInfo(fullPath);
        var fileInfo = info as FileInfo;
        var hash = fileInfo is not null && !string.IsNullOrWhiteSpace(hashAlgorithm)
            ? await ComputeHashAsync(fileInfo.FullName, hashAlgorithm, cancellationToken)
            : null;

        return new FileProperties(
            info.FullName,
            info is DirectoryInfo ? "directory" : "file",
            fileInfo?.Length ?? 0,
            info.CreationTimeUtc,
            info.LastWriteTimeUtc,
            info.LastAccessTimeUtc,
            info.Attributes.ToString(),
            fileInfo?.Extension ?? string.Empty,
            fileInfo is null ? null : FileVersionInfo.GetVersionInfo(fileInfo.FullName).FileVersion,
            string.IsNullOrWhiteSpace(hashAlgorithm) ? null : hashAlgorithm.ToUpperInvariant(),
            hash);
    }

    public IReadOnlyList<FileSearchResult> SearchFiles(
        IReadOnlyList<string> roots,
        string? namePattern,
        string? contentQuery,
        bool includeHidden,
        int? maxResults,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maxResults ?? 100, 1, 1000);
        var results = new List<FileSearchResult>(limit);

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count >= limit)
            {
                break;
            }

            var rootPath = NormalizeExistingDirectory(root);
            var attributesToSkip = FileAttributes.ReparsePoint;
            if (!includeHidden)
            {
                attributesToSkip |= FileAttributes.Hidden | FileAttributes.System;
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = attributesToSkip
            };

            foreach (var file in Directory.EnumerateFiles(rootPath, string.IsNullOrWhiteSpace(namePattern) ? "*" : namePattern, options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (results.Count >= limit)
                {
                    break;
                }

                if (!MatchesContent(file, contentQuery, cancellationToken))
                {
                    continue;
                }

                var info = new FileInfo(file);
                results.Add(new FileSearchResult(info.FullName, "file", info.Length, info.LastWriteTimeUtc));
            }
        }

        return results;
    }

    private static DirectoryEntry ToDirectoryEntry(FileSystemInfo info)
    {
        var fileInfo = info as FileInfo;
        return new DirectoryEntry(
            info.FullName,
            info is DirectoryInfo ? "directory" : "file",
            fileInfo?.Length ?? 0,
            info.CreationTimeUtc,
            info.LastWriteTimeUtc,
            info.LastAccessTimeUtc,
            info.Attributes.ToString(),
            fileInfo?.Extension ?? string.Empty);
    }

    private static async Task<(byte[] Bytes, long TotalBytes)> ReadLimitedBytesAsync(
        string fullPath,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);

        var totalBytes = stream.Length;
        var length = (int)Math.Min(maxBytes, totalBytes);
        var buffer = new byte[length];
        var totalRead = 0;

        while (totalRead < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return (totalRead == length ? buffer : buffer[..totalRead], totalBytes);
    }

    private static string DecodePrefix(byte[] bytes, Encoding encoding, bool truncated)
    {
        if (!truncated || bytes.Length == 0)
        {
            return encoding.GetString(bytes);
        }

        var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        decoder.Convert(
            bytes,
            0,
            bytes.Length,
            chars,
            0,
            chars.Length,
            flush: false,
            out _,
            out var charsUsed,
            out _);

        return new string(chars, 0, charsUsed);
    }

    private static Encoding ResolveEncoding(string? encoding)
    {
        return string.IsNullOrWhiteSpace(encoding) ? Encoding.UTF8 : Encoding.GetEncoding(encoding);
    }

    private static string NormalizeExistingDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Directory does not exist: {fullPath}");
        }

        return fullPath;
    }

    private static string NormalizeExistingFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File does not exist: {fullPath}", fullPath);
        }

        return fullPath;
    }

    private static FileSystemInfo GetFileSystemInfo(string path)
    {
        if (File.Exists(path))
        {
            return new FileInfo(path);
        }

        if (Directory.Exists(path))
        {
            return new DirectoryInfo(path);
        }

        throw new FileNotFoundException($"Path does not exist: {path}", path);
    }

    private static string RequireDestination(string? destinationPath)
    {
        return string.IsNullOrWhiteSpace(destinationPath)
            ? throw new ArgumentException("destination_path is required for copy and move actions.")
            : destinationPath;
    }

    private static async Task CopyPathAsync(
        string sourcePath,
        string destinationPath,
        bool recursive,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (File.Exists(sourcePath))
        {
            await CopyFileAtomicallyAsync(sourcePath, destinationPath, overwrite, cancellationToken);
            return;
        }

        if (!Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Source path does not exist: {sourcePath}", sourcePath);
        }

        if (!recursive)
        {
            throw new IOException("Recursive must be true to copy directories.");
        }

        var stage = CreateSiblingTemporaryPath(destinationPath, "copydir");
        var backup = CreateSiblingTemporaryPath(destinationPath, "backup");
        try
        {
            await CopyDirectoryAsync(sourcePath, stage, overwrite: false, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(destinationPath))
            {
                if (!overwrite)
                {
                    throw new IOException($"Destination directory already exists and overwrite is false: {destinationPath}");
                }

                Directory.Move(destinationPath, backup);
            }
            else if (File.Exists(destinationPath))
            {
                throw new IOException($"Destination is an existing file: {destinationPath}");
            }

            try
            {
                Directory.Move(stage, destinationPath);
                if (Directory.Exists(backup))
                {
                    await DeleteDirectoryAsync(backup, cancellationToken);
                }
            }
            catch
            {
                if (!Directory.Exists(destinationPath) && Directory.Exists(backup))
                {
                    Directory.Move(backup, destinationPath);
                }

                throw;
            }
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                await DeleteDirectoryBestEffortAsync(stage);
            }

            if (Directory.Exists(backup) && Directory.Exists(destinationPath))
            {
                await DeleteDirectoryBestEffortAsync(backup);
            }
        }
    }

    private static void MovePathTransactional(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(sourcePath))
        {
            if (File.Exists(destinationPath) && !overwrite)
            {
                throw new IOException($"Destination file already exists and overwrite is false: {destinationPath}");
            }

            File.Move(sourcePath, destinationPath, overwrite);
            return;
        }

        if (!Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Source path does not exist: {sourcePath}", sourcePath);
        }

        if (File.Exists(destinationPath))
        {
            throw new IOException($"Destination is an existing file: {destinationPath}");
        }

        var backup = CreateSiblingTemporaryPath(destinationPath, "movebackup");
        var hadDestination = Directory.Exists(destinationPath);
        if (hadDestination && !overwrite)
        {
            throw new IOException($"Destination directory already exists and overwrite is false: {destinationPath}");
        }

        if (hadDestination)
        {
            Directory.Move(destinationPath, backup);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(sourcePath, destinationPath);
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
        }
        catch
        {
            if (!Directory.Exists(destinationPath) && Directory.Exists(backup))
            {
                Directory.Move(backup, destinationPath);
            }

            throw;
        }
    }

    private static async Task DeletePathAsync(string sourcePath, bool recursive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(sourcePath))
        {
            File.Delete(sourcePath);
            return;
        }

        if (!Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Source path does not exist: {sourcePath}", sourcePath);
        }

        var attributes = File.GetAttributes(sourcePath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(sourcePath, recursive: false);
            return;
        }

        if (!recursive)
        {
            Directory.Delete(sourcePath, recursive: false);
            return;
        }

        await DeleteDirectoryAsync(sourcePath, cancellationToken);
    }

    private static async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destinationDirectory);

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(filePath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(filePath));
            await CopyFileAtomicallyAsync(filePath, destinationFile, overwrite, cancellationToken);
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(directoryPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var destinationSubdirectory = Path.Combine(destinationDirectory, Path.GetFileName(directoryPath));
            await CopyDirectoryAsync(directoryPath, destinationSubdirectory, overwrite, cancellationToken);
        }
    }

    private static async Task CopyFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        if (File.Exists(destinationPath) && !overwrite)
        {
            throw new IOException($"Destination file already exists and overwrite is false: {destinationPath}");
        }

        var stage = CreateSiblingTemporaryPath(destinationPath, "copy");
        try
        {
            await using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                stage,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, CopyBufferSize, cancellationToken);
                destination.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stage, destinationPath, overwrite);
        }
        finally
        {
            TryDeleteFile(stage);
        }
    }

    private static async Task DeleteDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
    {
        foreach (var filePath in Directory.EnumerateFiles(directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(filePath);
        }

        foreach (var childDirectory in Directory.EnumerateDirectories(directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(childDirectory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(childDirectory, recursive: false);
                continue;
            }

            await DeleteDirectoryAsync(childDirectory, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.Delete(directoryPath, recursive: false);
    }

    private static async Task DeleteDirectoryBestEffortAsync(string directoryPath)
    {
        try
        {
            await DeleteDirectoryAsync(directoryPath, CancellationToken.None);
        }
        catch
        {
            // Cleanup must not hide the primary operation outcome.
        }
    }

    private static async Task WriteFileDurablyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.WriteThrough);

        await stream.WriteAsync(bytes, cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static string CreateSiblingTemporaryPath(string targetPath, string purpose)
    {
        var directory = Path.GetDirectoryName(targetPath);
        var name = Path.GetFileName(targetPath);
        return Path.Combine(directory ?? string.Empty, $".{name}.windows-commander-{purpose}-{Guid.NewGuid():N}.tmp");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static async Task VerifyExpectedSha256Async(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Guarded write target no longer exists: {path}");
        }

        var actual = await ComputeHashAsync(path, "SHA256", cancellationToken);
        if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Guarded write rejected because the target changed. Expected SHA256 {expectedSha256}, actual {actual}.");
        }
    }

    private static async Task<string> ComputeHashAsync(string path, string hashAlgorithm, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var normalizedAlgorithm = hashAlgorithm.ToUpperInvariant();
        var hashBytes = normalizedAlgorithm switch
        {
            "SHA256" => await SHA256.HashDataAsync(stream, cancellationToken),
            "SHA1" => await SHA1.HashDataAsync(stream, cancellationToken),
            "MD5" => await MD5.HashDataAsync(stream, cancellationToken),
            _ => throw new ArgumentException($"Unsupported hash algorithm: {hashAlgorithm}")
        };

        return Convert.ToHexString(hashBytes);
    }

    private static bool MatchesContent(string filePath, string? contentQuery, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(contentQuery))
        {
            return true;
        }

        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Contains(contentQuery, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
