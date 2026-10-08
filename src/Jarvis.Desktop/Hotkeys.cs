using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Jarvis.Desktop;

/// <summary>System-wide keyboard shortcuts via RegisterHotKey.</summary>
public sealed class Hotkeys : IDisposable
{
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public Hotkeys()
    {
        _source = new HwndSource(new HwndSourceParameters("JARVIS hotkeys") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    /// <summary>Returns false if another app already owns the shortcut.</summary>
    public bool Register(uint modifiers, uint virtualKey, Action action)
    {
        var id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, modifiers | ModNoRepeat, virtualKey)) return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    /// <summary>Parses "Ctrl+Alt+J", "Ctrl+Shift+Space", "Win+F9"… Returns false for anything without a modifier.</summary>
    public static bool TryParse(string? text, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win" or "meta": modifiers |= ModWin; break;
                case "space": key = 0x20; break;
                default:
                    var k = raw.ToUpperInvariant();
                    if (k.Length == 1 && (char.IsAsciiLetterUpper(k[0]) || char.IsAsciiDigit(k[0]))) key = k[0];
                    else if (k.Length is 2 or 3 && k[0] == 'F' && int.TryParse(k[1..], out var f) && f is >= 1 and <= 24) key = (uint)(0x70 + f - 1);
                    else return false;
                    break;
            }
        }
        return modifiers != 0 && key != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
