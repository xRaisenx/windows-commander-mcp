using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using WindowsCommander.Windows.Native;
using WindowsCommander.Core.Models;
using WindowsCommander.Core.Services;

namespace WindowsCommander.Windows.Services;

public sealed class UiAutomationService : IUiAutomationService
{
    private const int MaxTreeElements = 2000;
    private const int MaxFindResults = 200;
    private const int MaxCachedElements = 4096;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, CachedElement> elements = new(StringComparer.OrdinalIgnoreCase);

    public UiTreeResult ReadUiTree(long windowHandle, int maxDepth = 5, IReadOnlyList<string>? controlTypes = null, bool interactableOnly = false)
    {
        var depth = Math.Clamp(maxDepth, 1, 20);
        var root = GetRootElement(windowHandle);
        var truncated = false;

        // Bound traversal before materializing the result so a pathological UI
        // tree cannot exhaust the MCP response budget.
        IEnumerable<UiElementInfo> flattened = Flatten(root, windowHandle, depth, () => truncated = true)
            .Take(MaxTreeElements + 1);

        if (controlTypes is { Count: > 0 })
        {
            var wanted = new HashSet<string>(controlTypes, StringComparer.OrdinalIgnoreCase);
            flattened = flattened.Where(element => wanted.Contains(element.ControlType));
        }

        if (interactableOnly)
        {
            flattened = flattened.Where(IsInteractable);
        }

        var materialized = flattened.ToArray();
        if (materialized.Length > MaxTreeElements)
        {
            truncated = true;
            materialized = materialized[..MaxTreeElements];
        }

        return new UiTreeResult(materialized, truncated, depth, materialized.Length);
    }

    // True when the element exposes a pattern an agent can act on. Used by
    // read_ui_tree's interactable_only filter to drop purely structural noise
    // (panes, static text, group containers) and shrink the payload.
    private bool IsInteractable(UiElementInfo element)
    {
        if (!TryGetCached(element.ElementRef, out var cached))
        {
            return false;
        }

        var automationElement = cached.Element;
        return automationElement.TryGetCurrentPattern(InvokePattern.Pattern, out _)
            || automationElement.TryGetCurrentPattern(TogglePattern.Pattern, out _)
            || automationElement.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)
            || automationElement.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _)
            || automationElement.TryGetCurrentPattern(ValuePattern.Pattern, out _);
    }

    public IReadOnlyList<UiElementInfo> FindUiElement(long windowHandle, string? nameContains, string? automationId, string? controlType, string? className, bool enabledOnly, int maxDepth = 5)
    {
        var depth = Math.Clamp(maxDepth, 1, 20);
        var root = GetRootElement(windowHandle);
        var results = new List<UiElementInfo>();
        foreach (var element in FlattenElements(root, 0, depth, static () => { }))
        {
            var info = ToInfo(element, windowHandle);
            if (!Matches(info, nameContains, automationId, controlType, className, enabledOnly)) continue;
            results.Add(info);
            if (results.Count >= MaxFindResults) break;
        }

        return results;
    }

    public UiActionResult InvokeUiElement(string elementRef, string action)
    {
        var element = GetElement(elementRef);
        switch (action.ToLowerInvariant())
        {
            case "invoke" when element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern):
                ((InvokePattern)invokePattern).Invoke();
                break;
            case "select" when element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern):
                ((SelectionItemPattern)selectionPattern).Select();
                break;
            case "expand" when element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern):
                ((ExpandCollapsePattern)expandPattern).Expand();
                break;
            case "collapse" when element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var collapsePattern):
                ((ExpandCollapsePattern)collapsePattern).Collapse();
                break;
            case "toggle" when element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern):
                ((TogglePattern)togglePattern).Toggle();
                break;
            case "focus":
                element.SetFocus();
                break;
            default:
                throw new InvalidOperationException($"UI element does not support action: {action}");
        }

        return new UiActionResult(elementRef, action, Completed: true);
    }

    public UiActionResult SetUiValue(string elementRef, string value)
    {
        var element = GetElement(elementRef);
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
        {
            throw new InvalidOperationException("UI element does not support ValuePattern.");
        }

        ((ValuePattern)pattern).SetValue(value);
        return new UiActionResult(elementRef, "set_value", Completed: true);
    }

    public UiElementDetails GetUiElementDetails(string elementRef)
    {
        if (!TryGetCached(elementRef, out var cached))
        {
            throw new ArgumentException($"Unknown or expired UI element reference: {elementRef}");
        }

        var element = GetElement(elementRef);
        var rootHandle = cached.RootWindowHandle;
        var info = ToInfo(element, rootHandle);
        var parent = TreeWalker.ControlViewWalker.GetParent(element);
        var children = EnumerateChildren(element).Select(child => ToInfo(child, rootHandle)).Take(MaxFindResults).ToArray();
        var actions = GetSupportedActions(element);

        return new UiElementDetails(info, actions, SafeName(parent), children);
    }

    private AutomationElement GetRootElement(long windowHandle)
    {
        return AutomationElement.FromHandle(new IntPtr(windowHandle)) ?? throw new ArgumentException($"Window handle was not found: {windowHandle}");
    }

    private AutomationElement GetElement(string elementRef)
    {
        if (!TryGetCached(elementRef, out var cached))
        {
            throw new ArgumentException($"Unknown or expired UI element reference: {elementRef}");
        }

        if (cached.RootWindowHandle != 0 && !IsWindow(new IntPtr(cached.RootWindowHandle)))
        {
            elements.TryRemove(elementRef, out _);
            throw new InvalidOperationException("UI element root window is no longer valid.");
        }

        try
        {
            if (cached.Element.Current.ProcessId != cached.ProcessId)
            {
                elements.TryRemove(elementRef, out _);
                throw new InvalidOperationException("UI element identity changed; reacquire it before acting.");
            }
        }
        catch (ElementNotAvailableException)
        {
            elements.TryRemove(elementRef, out _);
            throw new InvalidOperationException("UI element is no longer available.");
        }

        return cached.Element;
    }

    private bool TryGetCached(string elementRef, out CachedElement cached)
    {
        if (!elements.TryGetValue(elementRef, out var found))
        {
            cached = null!;
            return false;
        }

        if (DateTimeOffset.UtcNow - found.CreatedUtc <= CacheTtl)
        {
            cached = found;
            return true;
        }

        elements.TryRemove(elementRef, out _);
        cached = null!;
        return false;
    }

    private IEnumerable<UiElementInfo> Flatten(AutomationElement root, long rootWindowHandle, int maxDepth, Action onTruncated)
    {
        foreach (var element in FlattenElements(root, 0, maxDepth, onTruncated))
        {
            yield return ToInfo(element, rootWindowHandle);
        }
    }

    private static IEnumerable<AutomationElement> FlattenElements(AutomationElement root, int depth, int maxDepth, Action onTruncated)
    {
        yield return root;
        if (depth >= maxDepth)
        {
            // The deepest visited level still has unvisited children: the
            // returned tree is incomplete, so signal truncation to the caller.
            if (TreeWalker.ControlViewWalker.GetFirstChild(root) is not null)
            {
                onTruncated();
            }

            yield break;
        }

        foreach (var child in EnumerateChildren(root))
        {
            foreach (var nested in FlattenElements(child, depth + 1, maxDepth, onTruncated))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<AutomationElement> EnumerateChildren(AutomationElement element)
    {
        var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
        while (child is not null)
        {
            yield return child;
            child = TreeWalker.ControlViewWalker.GetNextSibling(child);
        }
    }

    private UiElementInfo ToInfo(AutomationElement element, long rootWindowHandle)
    {
        var runtimeId = string.Join(".", element.GetRuntimeId());
        var processId = element.Current.ProcessId;
        var identity = $"{rootWindowHandle}:{processId}:{runtimeId}";
        var elementRef = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(identity));
        elements[elementRef] = new CachedElement(element, rootWindowHandle, processId, DateTimeOffset.UtcNow);
        if (elements.Count > MaxCachedElements)
        {
            foreach (var stale in elements.OrderBy(pair => pair.Value.CreatedUtc).Take(elements.Count - MaxCachedElements))
            {
                elements.TryRemove(stale.Key, out _);
            }
        }
        var rectangle = element.Current.BoundingRectangle;

        return new UiElementInfo(
            elementRef,
            SafeName(element),
            element.Current.AutomationId ?? string.Empty,
            element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.OrdinalIgnoreCase),
            element.Current.ClassName ?? string.Empty,
            new RectBounds((int)rectangle.X, (int)rectangle.Y, (int)rectangle.Width, (int)rectangle.Height),
            element.Current.IsEnabled,
            element.Current.IsOffscreen,
            TryGetValue(element));
    }

    private static bool Matches(UiElementInfo element, string? nameContains, string? automationId, string? controlType, string? className, bool enabledOnly)
    {
        return (string.IsNullOrWhiteSpace(nameContains) || element.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(automationId) || string.Equals(element.AutomationId, automationId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(controlType) || string.Equals(element.ControlType, controlType, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(className) || string.Equals(element.ClassName, className, StringComparison.OrdinalIgnoreCase))
            && (!enabledOnly || element.IsEnabled);
    }

    private static string? TryGetValue(AutomationElement element)
    {
        return element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
            ? ((ValuePattern)pattern).Current.Value
            : null;
    }

    private static IReadOnlyList<string> GetSupportedActions(AutomationElement element)
    {
        var actions = new List<string> { "focus" };
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out _))
        {
            actions.Add("invoke");
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _))
        {
            actions.Add("select");
        }

        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _))
        {
            actions.Add("expand");
            actions.Add("collapse");
        }

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out _))
        {
            actions.Add("toggle");
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out _))
        {
            actions.Add("set_value");
        }

        return actions;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    private sealed record CachedElement(AutomationElement Element, long RootWindowHandle, int ProcessId, DateTimeOffset CreatedUtc);

    private static string SafeName(AutomationElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        try
        {
            return element.Current.Name ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
    }
}
