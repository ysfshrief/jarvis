using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using Jarvis.Core.Tools;
using Jarvis.Core.Web;

namespace Jarvis.Platform.Windows;

/// <summary>A control in another app's window, numbered so the AI (and you) can refer to it.</summary>
public sealed record UiElement(int Id, string Name, string Type, string? AutomationId, bool Enabled, bool IsPassword, string? Value,
    bool CanInvoke, bool CanType, bool CanToggle, int X, int Y, int Width, int Height)
{
    public string Describe() => $"[{Id}] {Type} “{Name}”{(Value is { Length: > 0 } v ? $" = “{(v.Length > 60 ? v[..57] + "…" : v)}”" : "")}{(Enabled ? "" : " (disabled)")}";
}

public sealed record UiSnapshot(string Window, string Process, IReadOnlyList<UiElement> Elements, string Text);

/// <summary>
/// Reads and operates other apps' controls through Windows UI Automation — the accessibility layer screen
/// readers use — so JARVIS presses the actual button rather than guessing pixels. Falls back to a real
/// mouse click only when a control offers no automation pattern.
/// </summary>
public sealed class WindowsUiAutomation
{
    private const int InvokePattern = 10000, ValuePattern = 10002, ExpandCollapsePattern = 10005, SelectionItemPattern = 10010, TogglePattern = 10015;
    private static readonly HashSet<string> OwnProcesses = new(StringComparer.OrdinalIgnoreCase) { "JARVIS", "jarvis-core", "msedgewebview2" };
    private static readonly Dictionary<int, string> Types = new()
    {
        [50000] = "button", [50002] = "checkbox", [50003] = "combobox", [50004] = "edit", [50005] = "link", [50007] = "listitem",
        [50011] = "menuitem", [50013] = "radio", [50019] = "tab", [50024] = "treeitem", [50030] = "document", [50031] = "splitbutton",
        [50020] = "text", [50015] = "slider",
    };

    private readonly Lazy<IUIAutomation> _uia = new(() => new CUIAutomation8());
    private readonly object _gate = new();
    private Dictionary<int, IUIAutomationElement> _elements = [];
    private Dictionary<int, UiElement> _known = [];

    public UiElement? Known(int id) { lock (_gate) return _known.GetValueOrDefault(id); }

    /// <summary>The window to work in: the one named, or the app in front (skipping JARVIS's own windows).</summary>
    public static WindowInfo? Target(string? window)
    {
        if (!string.IsNullOrWhiteSpace(window)) return WindowManager.Find(window).FirstOrDefault();
        var fg = WindowManager.Foreground();
        if (fg is not null && !OwnProcesses.Contains(fg.ProcessName)) return fg;
        return WindowManager.List().FirstOrDefault(w => !OwnProcesses.Contains(w.ProcessName) && !w.Minimized);
    }

    public UiSnapshot Snapshot(WindowInfo window, int max = 150)
    {
        var uia = _uia.Value;
        var root = uia.ElementFromHandle(window.Handle) ?? throw new InvalidOperationException("That window can't be read.");
        var walker = uia.ControlViewWalker;
        var queue = new Queue<IUIAutomationElement>();
        queue.Enqueue(root);
        var elements = new Dictionary<int, IUIAutomationElement>();
        var known = new Dictionary<int, UiElement>();
        var text = new List<string>();
        var visited = 0;
        while (queue.Count > 0 && visited < 2500 && known.Count < max)
        {
            var e = queue.Dequeue();
            visited++;
            try
            {
                if (!ReferenceEquals(e, root) && e.CurrentIsOffscreen == 0)
                {
                    var typeId = e.CurrentControlType;
                    var name = Clean(e.CurrentName);
                    if (typeId == 50020) { if (name.Length > 1) text.Add(name); }
                    else if (Types.TryGetValue(typeId, out var type))
                    {
                        var id = known.Count + 1;
                        var info = Describe(e, id, type, name);
                        if (info is not null) { elements[id] = e; known[id] = info; if (type == "document" && info.Value is { Length: > 0 } doc) text.Add(doc); }
                    }
                }
                for (var c = walker.GetFirstChildElement(e); c is not null; c = walker.GetNextSiblingElement(c)) queue.Enqueue(c);
            }
            catch (COMException) { /* the element vanished while we read it */ }
        }
        lock (_gate) { _elements = elements; _known = known; }
        var all = string.Join("\n", text.Distinct());
        return new UiSnapshot(window.Title, window.ProcessName, known.Values.ToList(), all.Length > 6000 ? all[..6000] + "…" : all);
    }

    /// <summary>Re-reads a numbered control, or null if it's gone.</summary>
    public UiElement? Inspect(int id)
    {
        IUIAutomationElement? e;
        UiElement? before;
        lock (_gate) { e = _elements.GetValueOrDefault(id); before = _known.GetValueOrDefault(id); }
        if (e is null || before is null) return null;
        try { return Describe(e, id, before.Type, Clean(e.CurrentName)); }
        catch (COMException) { return null; }
    }

    public string Click(int id)
    {
        var e = Element(id);
        if (e.GetCurrentPattern(InvokePattern) is IUIAutomationInvokePattern invoke) { invoke.Invoke(); return "invoked"; }
        if (e.GetCurrentPattern(TogglePattern) is IUIAutomationTogglePattern toggle) { toggle.Toggle(); return "toggled"; }
        if (e.GetCurrentPattern(SelectionItemPattern) is IUIAutomationSelectionItemPattern select) { select.Select(); return "selected"; }
        if (e.GetCurrentPattern(ExpandCollapsePattern) is IUIAutomationExpandCollapsePattern expand) { expand.Expand(); return "expanded"; }
        var r = e.CurrentBoundingRectangle;
        Mouse.Click((r.left + r.right) / 2, (r.top + r.bottom) / 2, right: false, doubleClick: false);
        return "clicked";
    }

    /// <summary>Puts text into a field and returns what the field now contains (to verify it worked).</summary>
    public string? Type(int id, string text, bool submit)
    {
        var e = Element(id);
        if (e.GetCurrentPattern(ValuePattern) is IUIAutomationValuePattern value && value.CurrentIsReadOnly == 0 && e.CurrentIsPassword == 0)
            value.SetValue(text);
        else
        {
            e.SetFocus();
            Thread.Sleep(80);
            Keyboard.TypeText(text);
        }
        if (submit) { e.SetFocus(); Keyboard.Press(0x0D); }
        Thread.Sleep(120);
        try { return (e.GetCurrentPattern(ValuePattern) as IUIAutomationValuePattern)?.CurrentValue; }
        catch (COMException) { return null; }
    }

    /// <summary>What control is at a screen point (to grade a raw mouse click).</summary>
    public (string Name, string Type)? At(int x, int y)
    {
        try
        {
            var e = _uia.Value.ElementFromPoint(new tagPOINT { x = x, y = y });
            if (e is null) return null;
            return (Clean(e.CurrentName), Types.GetValueOrDefault(e.CurrentControlType, Clean(e.CurrentLocalizedControlType)));
        }
        catch (COMException) { return null; }
    }

    private IUIAutomationElement Element(int id)
    {
        lock (_gate)
            return _elements.GetValueOrDefault(id) ?? throw new InvalidOperationException($"Control {id} isn't known; read the window first.");
    }

    private static UiElement? Describe(IUIAutomationElement e, int id, string type, string name)
    {
        var r = e.CurrentBoundingRectangle;
        if (r.right - r.left < 2 || r.bottom - r.top < 2) return null;
        var isPassword = e.CurrentIsPassword != 0;
        string? value = null;
        var canType = false;
        if (e.GetCurrentPattern(ValuePattern) is IUIAutomationValuePattern vp)
        {
            canType = vp.CurrentIsReadOnly == 0;
            if (!isPassword) value = Clean(vp.CurrentValue);
        }
        if (name.Length == 0 && value is null && type is not ("edit" or "document")) return null;
        return new UiElement(id, name, type, NullIfEmpty(e.CurrentAutomationId), e.CurrentIsEnabled != 0, isPassword, value,
            e.GetCurrentPattern(InvokePattern) is not null, canType || type is "edit" or "document", e.GetCurrentPattern(TogglePattern) is not null,
            r.left, r.top, r.right - r.left, r.bottom - r.top);
    }

    private static string Clean(string? s)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return t.Length > 200 ? t[..200] : t;
    }
    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>Risk of acting on a control: consequential labels (send, delete, buy…) and passwords are critical.</summary>
public static class UiRisk
{
    public static RiskAssessment ForClick(UiElement e, string app)
    {
        var label = e.Name.Length > 0 ? $"“{e.Name}”" : $"control {e.Id}";
        return BrowserRisk.Consequential(e.Name)
            ? new(RiskLevel.Critical, $"Press {label} in {app} — this may send, buy, publish, delete or change something")
            : new(RiskLevel.Sensitive, $"Press {label} in {app}");
    }

    public static RiskAssessment ForType(UiElement e, string app, bool submit)
    {
        var label = e.Name.Length > 0 ? $"“{e.Name}”" : $"field {e.Id}";
        if (e.IsPassword) return new(RiskLevel.Critical, $"Type into the password field {label} in {app}");
        return new(RiskLevel.Sensitive, submit ? $"Type into {label} in {app} and press Enter" : $"Type into {label} in {app}");
    }
}

/// <summary>Real mouse input (used only when a control can't be operated through UI Automation, or when asked).</summary>
public static class Mouse
{
    public static void Click(int x, int y, bool right, bool doubleClick)
    {
        Native.SetCursorPos(x, y);
        Thread.Sleep(30);
        var (down, up) = right ? (Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP) : (Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP);
        var inputs = new List<Native.INPUT>();
        for (var i = 0; i < (doubleClick ? 2 : 1); i++)
        {
            inputs.Add(new Native.INPUT { type = Native.INPUT_MOUSE, u = new() { mi = new() { dwFlags = down } } });
            inputs.Add(new Native.INPUT { type = Native.INPUT_MOUSE, u = new() { mi = new() { dwFlags = up } } });
        }
        Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.INPUT>());
    }

    public static void Scroll(int notches)
    {
        var input = new Native.INPUT { type = Native.INPUT_MOUSE, u = new() { mi = new() { dwFlags = Native.MOUSEEVENTF_WHEEL, mouseData = unchecked((uint)(notches * 120)) } } };
        Native.SendInput(1, [input], Marshal.SizeOf<Native.INPUT>());
    }
}

/// <summary>Keystrokes via SendInput (Unicode, so Arabic works).</summary>
public static class Keyboard
{
    public static bool TypeText(string text)
    {
        var inputs = new List<Native.INPUT>();
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n') { inputs.Add(TypeTextTool.Key(0x0D, false)); inputs.Add(TypeTextTool.Key(0x0D, true)); continue; }
            inputs.Add(new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new() { ki = new() { wScan = ch, dwFlags = Native.KEYEVENTF_UNICODE } } });
            inputs.Add(new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new() { ki = new() { wScan = ch, dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP } } });
        }
        return Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.INPUT>()) == inputs.Count;
    }

    public static void Press(ushort vk) =>
        Native.SendInput(2, [TypeTextTool.Key(vk, false), TypeTextTool.Key(vk, true)], Marshal.SizeOf<Native.INPUT>());
}
