using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Denon.App.Core.Models;
using Denon.App.Core.Services;

namespace Denon.App.Desktop.Services;

public enum HotkeyRegistrationState
{
    Disabled,
    Registered,
    OsCollision,
    InvalidGesture,
}

public sealed record ParsedShortcutGesture(
    string CanonicalText,
    ModifierKeys Modifiers,
    Key Key,
    uint Win32Modifiers,
    uint VirtualKey);

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int ProbeHotkeyId = 0x7FFF;

    private static readonly HashSet<string> ReservedSystemGestures = new(StringComparer.OrdinalIgnoreCase)
    {
        "Alt+F4",
        "Alt+Tab",
        "Alt+Escape",
        "Ctrl+Alt+Delete",
        "Ctrl+Shift+Escape",
        "Win+L",
        "Win+D",
        "Win+E",
        "Win+R",
        "Win+Tab",
        "Win+V",
        "Ctrl+C",
        "Ctrl+V",
        "Ctrl+X",
        "Ctrl+Z",
        "Ctrl+Y",
        "Ctrl+A",
        "Ctrl+S",
    };

    private static readonly HashSet<string> ReservedNavigationGestures = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5", "Ctrl+6", "Ctrl+7", "Ctrl+R",
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly AppLogStore _logStore;
    private readonly Dictionary<int, string> _idToAction = [];
    private readonly Dictionary<string, (int Id, string CanonicalGesture)> _actionToRegistration = new(StringComparer.OrdinalIgnoreCase);

    private HwndSource? _hwndSource;
    private int _nextId = 100;
    private bool _disposed;

    public GlobalHotkeyService(AppLogStore logStore)
    {
        _logStore = logStore;
    }

    public event Action<string>? HotkeyPressed;

    public void Initialize()
    {
        if (_hwndSource is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("DenonDesktopHotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE (message-only window — works even when minimized to tray)
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
    }

    public IReadOnlyDictionary<string, HotkeyRegistrationState> ApplyGlobalBindings(IEnumerable<ShortcutBinding> bindings)
    {
        Initialize();
        UnregisterAll();

        var results = new Dictionary<string, HotkeyRegistrationState>(StringComparer.OrdinalIgnoreCase);
        var seenGestures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in bindings.Where(static b => b.IsGlobal))
        {
            if (string.IsNullOrWhiteSpace(binding.Gesture))
            {
                results[binding.ActionId] = HotkeyRegistrationState.Disabled;
                continue;
            }

            if (!TryParseGesture(binding.Gesture, out var parsed) || parsed is null)
            {
                results[binding.ActionId] = HotkeyRegistrationState.InvalidGesture;
                continue;
            }

            if (!seenGestures.Add(parsed.CanonicalText))
            {
                results[binding.ActionId] = HotkeyRegistrationState.OsCollision;
                continue;
            }

            var id = _nextId++;
            if (RegisterHotKey(_hwndSource!.Handle, id, parsed.Win32Modifiers | ModNoRepeat, parsed.VirtualKey))
            {
                _idToAction[id] = binding.ActionId;
                _actionToRegistration[binding.ActionId] = (id, parsed.CanonicalText);
                results[binding.ActionId] = HotkeyRegistrationState.Registered;
            }
            else
            {
                var err = Marshal.GetLastWin32Error();
                results[binding.ActionId] = HotkeyRegistrationState.OsCollision;
                _logStore.Warning($"Global hotkey '{parsed.CanonicalText}' ({binding.Label}) could not be registered (Win32 error {err} — already taken by Windows or another app).");
            }
        }

        _logStore.Info($"Registered {_actionToRegistration.Count} global Windows hotkey(s).");
        return results;
    }

    /// <summary>
    /// Live Win32 RegisterHotKey probe to detect whether another Windows application or the OS
    /// already owns this global hotkey before saving.
    /// </summary>
    public bool IsTakenByAnotherApp(string gestureText, string? actionIdBeingEdited)
    {
        if (!TryParseGesture(gestureText, out var parsed) || parsed is null)
        {
            return false;
        }

        // If Denon Desktop itself currently owns this exact gesture on the SAME action, it's ours — not an external collision!
        if (!string.IsNullOrWhiteSpace(actionIdBeingEdited) &&
            _actionToRegistration.TryGetValue(actionIdBeingEdited, out var owned) &&
            string.Equals(owned.CanonicalGesture, parsed.CanonicalText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // If another of OUR currently registered actions owns it, internal duplicate detection handles it
        if (_actionToRegistration.Values.Any(v => string.Equals(v.CanonicalGesture, parsed.CanonicalText, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        Initialize();
        if (_hwndSource is null)
        {
            return false;
        }

        if (RegisterHotKey(_hwndSource.Handle, ProbeHotkeyId, parsed.Win32Modifiers | ModNoRepeat, parsed.VirtualKey))
        {
            UnregisterHotKey(_hwndSource.Handle, ProbeHotkeyId);
            return false;
        }

        return true;
    }

    public static string? GetReservedWarning(string gestureText, bool isGlobal)
    {
        if (!TryParseGesture(gestureText, out var parsed) || parsed is null)
        {
            return "Invalid key combination.";
        }

        if (ReservedSystemGestures.Contains(parsed.CanonicalText))
        {
            return $"'{parsed.CanonicalText}' conflicts with a standard Windows system shortcut.";
        }

        if (!isGlobal && ReservedNavigationGestures.Contains(parsed.CanonicalText))
        {
            return $"'{parsed.CanonicalText}' is reserved for built-in view navigation.";
        }

        if (isGlobal && parsed.Win32Modifiers == 0 && !IsStandaloneGlobalFriendlyKey(parsed.Key))
        {
            return $"Global shortcut '{parsed.CanonicalText}' needs Ctrl, Alt, Shift, or Win (or a Function/Media key) so it doesn't hijack normal typing.";
        }

        return null;
    }

    public static bool TryFormatCapturedKey(Key key, ModifierKeys modifiers, out string canonicalText)
    {
        canonicalText = string.Empty;

        // Ignore pure modifier presses
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None)
        {
            return false;
        }

        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(FormatKeyName(key));

        canonicalText = string.Join('+', parts);
        return TryParseGesture(canonicalText, out _);
    }

    public static bool TryParseGesture(string? raw, out ParsedShortcutGesture? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var tokens = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        uint win32Mods = 0;
        Key? resolvedKey = null;

        foreach (var token in tokens)
        {
            switch (token.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= ModifierKeys.Control;
                    win32Mods |= ModControl;
                    break;
                case "ALT":
                    modifiers |= ModifierKeys.Alt;
                    win32Mods |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModifierKeys.Shift;
                    win32Mods |= ModShift;
                    break;
                case "WIN" or "WINDOWS":
                    modifiers |= ModifierKeys.Windows;
                    win32Mods |= ModWin;
                    break;
                default:
                    if (resolvedKey is not null || !TryParseKeyName(token, out var key))
                    {
                        return false;
                    }
                    resolvedKey = key;
                    break;
            }
        }

        if (resolvedKey is null || resolvedKey == Key.None)
        {
            return false;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(resolvedKey.Value);
        if (vk == 0)
        {
            return false;
        }

        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(FormatKeyName(resolvedKey.Value));

        parsed = new ParsedShortcutGesture(string.Join('+', parts), modifiers, resolvedKey.Value, win32Mods, vk);
        return true;
    }

    private static bool IsStandaloneGlobalFriendlyKey(Key key) =>
        (key >= Key.F1 && key <= Key.F24) ||
        key is Key.MediaPlayPause or Key.MediaStop or Key.MediaNextTrack or Key.MediaPreviousTrack or Key.VolumeMute or Key.VolumeUp or Key.VolumeDown or Key.Pause;

    private static string FormatKeyName(Key key) =>
        key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            Key.Space => "Space",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.OemPlus => "Plus",
            Key.OemMinus => "Minus",
            Key.Return => "Enter",
            Key.Back => "Backspace",
            _ => key.ToString(),
        };

    private static bool TryParseKeyName(string token, out Key key)
    {
        key = Key.None;
        var trimmed = token.Trim();
        if (trimmed.Length == 1 && char.IsDigit(trimmed[0]))
        {
            key = Key.D0 + (trimmed[0] - '0');
            return true;
        }

        switch (trimmed.ToUpperInvariant())
        {
            case "SPACE":
                key = Key.Space;
                return true;
            case "UP" or "↑":
                key = Key.Up;
                return true;
            case "DOWN" or "↓":
                key = Key.Down;
                return true;
            case "LEFT" or "←":
                key = Key.Left;
                return true;
            case "RIGHT" or "→":
                key = Key.Right;
                return true;
            case "PLUS" or "+":
                key = Key.OemPlus;
                return true;
            case "MINUS" or "-":
                key = Key.OemMinus;
                return true;
            case "ENTER":
                key = Key.Return;
                return true;
            case "ESC":
                key = Key.Escape;
                return true;
            case "DEL":
                key = Key.Delete;
                return true;
            case "BACKSPACE":
                key = Key.Back;
                return true;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out key) && key != Key.None;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey)
        {
            var id = wParam.ToInt32();
            if (_idToAction.TryGetValue(id, out var actionId))
            {
                handled = true;
                HotkeyPressed?.Invoke(actionId);
            }
        }

        return IntPtr.Zero;
    }

    private void UnregisterAll()
    {
        if (_hwndSource is not null)
        {
            foreach (var id in _idToAction.Keys)
            {
                try { UnregisterHotKey(_hwndSource.Handle, id); } catch { }
            }
        }

        _idToAction.Clear();
        _actionToRegistration.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }
}
