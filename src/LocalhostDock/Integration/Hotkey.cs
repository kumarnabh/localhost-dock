using System.Runtime.InteropServices;

namespace LocalhostDock.Integration;

/// <summary>A system-wide keyboard shortcut (RegisterHotKey) such as "Ctrl+Alt+L".</summary>
internal sealed class Hotkey : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int Id = 0x4C44; // "LD"
    private readonly Action _pressed;
    private bool _registered;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public Hotkey(Action pressed)
    {
        _pressed = pressed;
        CreateHandle(new CreateParams());
    }

    /// <summary>Registers the shortcut; returns an error message, or null on success (or when disabled).</summary>
    public string? Set(string? spec)
    {
        if (_registered) { UnregisterHotKey(Handle, Id); _registered = false; }
        if (string.IsNullOrWhiteSpace(spec)) return null;
        if (!TryParse(spec, out uint mods, out uint vk)) return $"\"{spec}\" isn't a valid shortcut (try Ctrl+Alt+L).";
        _registered = RegisterHotKey(Handle, Id, mods | 0x4000 /* MOD_NOREPEAT */, vk);
        return _registered ? null : $"{spec} is already used by another app. Pick another in apps.json (\"hotkey\").";
    }

    public static bool TryParse(string spec, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (string raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= 0x2; break;
                case "alt": modifiers |= 0x1; break;
                case "shift": modifiers |= 0x4; break;
                case "win" or "windows": modifiers |= 0x8; break;
                default:
                    if (vk != 0 || !Enum.TryParse(raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw, true, out Keys key)) return false;
                    vk = (uint)key;
                    break;
            }
        }
        return vk != 0 && modifiers != 0;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == Id) _pressed();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(Handle, Id);
        DestroyHandle();
    }
}
