using System.Windows.Forms;
using WindowsCommander.Core.Services;

namespace WindowsCommander.Windows.Services;

public sealed class ClipboardService : IClipboardService
{
    private const int ClipboardOperationTimeoutMs = 5000;
    private const int MaxClipboardTextChars = 1_000_000;

    public object? Access(string action, string? content, string? format)
    {
        if (!string.Equals(format ?? "text", "text", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only text clipboard format is implemented in this slice.");
        }

        return StaExecutor.Shared.Invoke(() => action.ToLowerInvariant() switch
        {
            "read" => ReadText(),
            "write" => WriteText(content),
            "clear" => ClearClipboard(),
            _ => throw new ArgumentException($"Unsupported clipboard action: {action}")
        }, ClipboardOperationTimeoutMs);
    }

    private static string ReadText()
    {
        if (!Clipboard.ContainsText())
        {
            return string.Empty;
        }

        var text = Clipboard.GetText();
        if (text.Length > MaxClipboardTextChars)
        {
            throw new InvalidOperationException(
                $"Clipboard text contains {text.Length:N0} characters; maximum readable length is {MaxClipboardTextChars:N0}.");
        }

        return text;
    }

    private static object? WriteText(string? content)
    {
        var value = content ?? string.Empty;
        if (value.Length > MaxClipboardTextChars)
        {
            throw new ArgumentOutOfRangeException(nameof(content),
                $"Clipboard text contains {value.Length:N0} characters; maximum writable length is {MaxClipboardTextChars:N0}.");
        }

        Clipboard.SetText(value);
        return new { written = true, characters = value.Length };
    }

    private static object? ClearClipboard()
    {
        Clipboard.Clear();
        return new { cleared = true };
    }
}
