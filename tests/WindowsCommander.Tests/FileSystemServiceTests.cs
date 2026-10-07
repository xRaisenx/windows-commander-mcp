using WindowsCommander.Windows.Services;

namespace WindowsCommander.Tests;

public class FileSystemServiceTests
{
    [Fact]
    public async Task WriteReadAndProperties_RoundTripTextFile()
    {
        var service = new FileSystemService();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "windows-commander-tests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(tempDirectory, "sample.txt");

        try
        {
            var writeResult = await service.WriteFileAsync(filePath, "hello", "utf-8", overwrite: false, createDirectories: true, CancellationToken.None);
            var readResult = await service.ReadFileAsync(filePath, "utf-8", maxBytes: null, asBase64: false, CancellationToken.None);
            var properties = await service.GetFilePropertiesAsync(filePath, "SHA256", CancellationToken.None);

            Assert.True(writeResult.CreatedDirectory);
            Assert.Equal("hello", readResult.Content);
            Assert.Equal("file", properties.Type);
            Assert.Equal("SHA256", properties.HashAlgorithm);
            Assert.False(string.IsNullOrWhiteSpace(properties.Hash));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ListDirectory_ReturnsCreatedFile()
    {
        var service = new FileSystemService();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "windows-commander-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var filePath = Path.Combine(tempDirectory, "listed.txt");
        File.WriteAllText(filePath, "listed");

        try
        {
            var entries = service.ListDirectory(
                tempDirectory,
                recursive: false,
                includeHidden: false,
                pattern: "*.txt",
                maxResults: null,
                CancellationToken.None);

            Assert.Contains(entries, entry => entry.Path == filePath && entry.Type == "file");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
    [Fact]
    public void ListDirectory_HonorsMaxResults()
    {
        var service = new FileSystemService();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "windows-commander-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            for (var index = 0; index < 10; index++)
            {
                File.WriteAllText(Path.Combine(tempDirectory, $"item-{index}.txt"), index.ToString());
            }

            var entries = service.ListDirectory(
                tempDirectory,
                recursive: false,
                includeHidden: false,
                pattern: "*.txt",
                maxResults: 3,
                CancellationToken.None);

            Assert.Equal(3, entries.Count);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void SearchFiles_ObservesCancellation()
    {
        var service = new FileSystemService();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "windows-commander-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        File.WriteAllText(Path.Combine(tempDirectory, "item.txt"), "needle");

        try
        {
            using var cancellationSource = new CancellationTokenSource();
            cancellationSource.Cancel();

            Assert.Throws<OperationCanceledException>(() => service.SearchFiles(
                new[] { tempDirectory },
                "*.txt",
                "needle",
                includeHidden: false,
                maxResults: 100,
                cancellationSource.Token));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

}
