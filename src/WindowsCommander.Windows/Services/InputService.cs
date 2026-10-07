using System.Runtime.InteropServices;
using System.Windows.Forms;
using WindowsCommander.Core.Models;
using WindowsCommander.Core.Services;
using WindowsCommander.Windows.Native;

namespace WindowsCommander.Windows.Services;

public sealed class InputService : IInputService
{
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventWheel = 0x0800;
    private const uint MouseEventHorizontalWheel = 0x1000;
    private const uint KeyEventKeyUp = 0x0002;
    private const int ClipboardOperationTimeoutMs = 5000;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    public InputActionResult MouseAction(string action, string? button, int? x, int? y, long? targetWindowHandle)
    {
        var (targetX, targetY) = ResolvePoint(x, y, targetWindowHandle);
        NativeMethods.SetCursorPos(targetX, targetY);

        switch (action.ToLowerInvariant())
        {
            case "move":
                return new InputActionResult("move", Completed: true, 1);
            case "click":
                Click(button ?? "left");
                return new InputActionResult("click", Completed: true, 1);
            case "double_click":
                Click(button ?? "left");
                Click(button ?? "left");
                return new InputActionResult("double_click", Completed: true, 2);
            case "drag":
                if (x is null || y is null)
                {
                    throw new ArgumentException("Drag requires explicit x and y destination coordinates.");
                }

                MouseDown(button ?? "left");
                NativeMethods.SetCursorPos(x.Value, y.Value);
                MouseUp(button ?? "left");
                return new InputActionResult("drag", Completed: true, 1);
            default:
                throw new ArgumentException($"Unsupported mouse action: {action}");
        }
    }

    public Task<InputActionResult> TypeTextAsync(string text, int? speedMs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (text.Length == 0)
        {
            return Task.FromResult(new InputActionResult("type_text", Completed: true, 0));
        }

        // Text entry intentionally uses one clipboard paste rather than a long
        // synthetic-key sequence, preserving Unicode and avoiding key loss.
        // speedMs is retained only for interface compatibility and is not
        // advertised as a public control by the MCP schema.
        PasteText(text);
        return Task.FromResult(new InputActionResult("type_text", Completed: true, text.Length));
    }

    private void PasteText(string text)
    {
        StaExecutor.Shared.Invoke(() =>
        {
            var restoreClipboard = CaptureClipboard();
            Clipboard.SetText(text);
            var ownedSequence = GetClipboardSequenceNumber();

            try
            {
                SendHotkey(new[] { "ctrl" }, "v");

                // The target reads clipboard data asynchronously. Keep the
                // prior conservative settle period until a target-observed
                // completion primitive is proven across supported apps.
                Thread.Sleep(400);
            }
            finally
            {
                // Never overwrite content copied by the user/application while
                // Windows Commander was waiting for the paste to be consumed.
                if (GetClipboardSequenceNumber() == ownedSequence)
                {
                    restoreClipboard();
                }
            }

            return true;
        }, ClipboardOperationTimeoutMs);
    }

    private static Action CaptureClipboard()
    {
        try
        {
            var current = Clipboard.GetDataObject();
            var formats = current?.GetFormats(autoConvert: false);
            if (current is null || formats is null || formats.Length == 0)
            {
                return static () => Clipboard.Clear();
            }

            var snapshot = new DataObject();
            var captured = false;
            foreach (var format in formats)
            {
                try
                {
                    var data = current.GetData(format, autoConvert: false);
                    if (data is not null)
                    {
                        snapshot.SetData(format, data);
                        captured = true;
                    }
                }
                catch
                {
                    // Delay-rendered or COM-backed formats can be unreadable.
                }
            }

            return captured
                ? () => Clipboard.SetDataObject(snapshot, copy: true)
                : static () => { };
        }
        catch
        {
            return static () => { };
        }
    }

    public InputActionResult SendHotkey(IReadOnlyList<string> modifiers, string key)
    {
        var keys = modifiers.Select(ToVirtualKey).Concat(new[] { ToVirtualKey(key) }).ToArray();
        foreach (var virtualKey in keys)
        {
            NativeMethods.keybd_event(virtualKey, 0, 0, 0);
        }

        foreach (var virtualKey in keys.Reverse())
        {
            NativeMethods.keybd_event(virtualKey, 0, KeyEventKeyUp, 0);
        }

        return new InputActionResult("send_hotkey", Completed: true, 1);
    }

    public async Task<InputActionResult> KeyboardActionAsync(string action, string key, int? repeat, int? delayMs, CancellationToken cancellationToken)
    {
        var virtualKey = ToVirtualKey(key);
        var count = Math.Clamp(repeat ?? 1, 1, 1000);
        var delay = Math.Clamp(delayMs ?? 0, 0, 5000);

        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (action.ToLowerInvariant())
            {
                case "press":
                    NativeMethods.keybd_event(virtualKey, 0, 0, 0);
                    break;
                case "release":
                    NativeMethods.keybd_event(virtualKey, 0, KeyEventKeyUp, 0);
                    break;
                case "tap":
                    NativeMethods.keybd_event(virtualKey, 0, 0, 0);
                    NativeMethods.keybd_event(virtualKey, 0, KeyEventKeyUp, 0);
                    break;
                default:
                    throw new ArgumentException($"Unsupported keyboard action: {action}");
            }

            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        return new InputActionResult("keyboard_action", Completed: true, count);
    }

    public InputActionResult MouseWheel(string direction, int amount, long? targetWindowHandle, int? x, int? y)
    {
        var (targetX, targetY) = ResolvePoint(x, y, targetWindowHandle);
        NativeMethods.SetCursorPos(targetX, targetY);

        var normalized = direction.ToLowerInvariant();
        var delta = normalized switch
        {
            "up" => 120 * Math.Abs(amount),
            "down" => -120 * Math.Abs(amount),
            "left" => -120 * Math.Abs(amount),
            "right" => 120 * Math.Abs(amount),
            _ => throw new ArgumentException($"Unsupported wheel direction: {direction}")
        };
        var flag = normalized is "left" or "right" ? MouseEventHorizontalWheel : MouseEventWheel;

        NativeMethods.mouse_event(flag, 0, 0, unchecked((uint)delta), 0);
        return new InputActionResult("mouse_wheel", Completed: true, 1);
    }

    public CursorPositionInfo GetCursorPosition()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            throw new InvalidOperationException("Unable to read cursor position.");
        }

        var windowHandle = NativeMethods.WindowFromPoint(point);
        var screen = Screen.FromPoint(new System.Drawing.Point(point.X, point.Y));
        var screenIndex = Array.IndexOf(Screen.AllScreens, screen);
        return new CursorPositionInfo(point.X, point.Y, $"screen-{(screenIndex < 0 ? 0 : screenIndex) + 1}", windowHandle != nint.Zero, windowHandle == nint.Zero ? null : windowHandle);
    }

    public async Task<InputActionResult> SetCursorPositionAsync(int x, int y, int? durationMs, CancellationToken cancellationToken)
    {
        var duration = Math.Clamp(durationMs ?? 0, 0, 30000);
        if (duration == 0 || !NativeMethods.GetCursorPos(out var start))
        {
            NativeMethods.SetCursorPos(x, y);
            return new InputActionResult("set_cursor_position", Completed: true, 1);
        }

        const int steps = 20;
        for (var step = 1; step <= steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextX = start.X + ((x - start.X) * step / steps);
            var nextY = start.Y + ((y - start.Y) * step / steps);
            NativeMethods.SetCursorPos(nextX, nextY);
            await Task.Delay(duration / steps, cancellationToken);
        }

        return new InputActionResult("set_cursor_position", Completed: true, steps);
    }

    public async Task<InputActionResult> InputSequenceAsync(IReadOnlyList<InputSequenceStep> steps, bool abortOnError, CancellationToken cancellationToken)
    {
        var completed = 0;
        Exception? firstError = null;
        foreach (var step in steps)
        {
            try
            {
                switch (step.Type.ToLowerInvariant())
                {
                    case "mouse":
                        MouseAction(step.Action ?? "move", "left", step.X, step.Y, null);
                        break;
                    case "text":
                        await TypeTextAsync(step.Text ?? string.Empty, step.DelayMs, cancellationToken);
                        break;
                    case "keyboard":
                        await KeyboardActionAsync(step.Action ?? "tap", step.Key ?? throw new ArgumentException("Keyboard step requires key."), 1, step.DelayMs, cancellationToken);
                        break;
                    case "hotkey":
                        SendHotkey(step.Modifiers ?? Array.Empty<string>(), step.Key ?? throw new ArgumentException("Hotkey step requires key."));
                        break;
                    case "delay":
                        await Task.Delay(Math.Clamp(step.DelayMs ?? 0, 0, 30000), cancellationToken);
                        break;
                    default:
                        throw new ArgumentException($"Unsupported input sequence step: {step.Type}");
                }

                completed++;
            }
            catch (Exception exception) when (!abortOnError)
            {
                firstError ??= exception;
            }
        }

        if (firstError is not null && completed == 0)
        {
            throw new InvalidOperationException($"Input sequence failed before completing any step: {firstError.Message}", firstError);
        }

        return new InputActionResult("input_sequence", completed == steps.Count, completed);
    }

    private static (int X, int Y) ResolvePoint(int? x, int? y, long? targetWindowHandle)
    {
        if (targetWindowHandle is not null)
        {
            if (!NativeMethods.GetWindowRect(new IntPtr(targetWindowHandle.Value), out var rect))
            {
                throw new ArgumentException($"Window handle was not found: {targetWindowHandle}");
            }

            return (rect.Left + ((rect.Right - rect.Left) / 2), rect.Top + ((rect.Bottom - rect.Top) / 2));
        }

        if (x is null || y is null)
        {
            throw new ArgumentException("x and y are required when target_hwnd is not provided.");
        }

        return (x.Value, y.Value);
    }

    private static void Click(string button)
    {
        MouseDown(button);
        MouseUp(button);
    }

    private static void MouseDown(string button)
    {
        NativeMethods.mouse_event(button.ToLowerInvariant() switch
        {
            "left" => MouseEventLeftDown,
            "right" => MouseEventRightDown,
            "middle" => MouseEventMiddleDown,
            _ => throw new ArgumentException($"Unsupported mouse button: {button}")
        }, 0, 0, 0, 0);
    }

    private static void MouseUp(string button)
    {
        NativeMethods.mouse_event(button.ToLowerInvariant() switch
        {
            "left" => MouseEventLeftUp,
            "right" => MouseEventRightUp,
            "middle" => MouseEventMiddleUp,
            _ => throw new ArgumentException($"Unsupported mouse button: {button}")
        }, 0, 0, 0, 0);
    }

    private static byte ToVirtualKey(string key)
    {
        return key.ToLowerInvariant() switch
        {
            "ctrl" or "control" => 0x11,
            "shift" => 0x10,
            "alt" => 0x12,
            "win" or "windows" => 0x5B,
            "enter" => 0x0D,
            "escape" or "esc" => 0x1B,
            "tab" => 0x09,
            "space" => 0x20,
            "backspace" => 0x08,
            "delete" => 0x2E,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            _ when key.Length == 1 => (byte)char.ToUpperInvariant(key[0]),
            _ when key.StartsWith('f') && int.TryParse(key[1..], out var number) && number is >= 1 and <= 24 => (byte)(0x6F + number),
            _ => throw new ArgumentException($"Unsupported key: {key}")
        };
    }
}
