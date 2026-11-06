using System.Windows.Input;
using System.Windows.Interop;
using Roshiq.Core.Diagnostics;

namespace Roshiq.Overlay;

/// <summary>Глобальные горячие клавиши через RegisterHotKey (не перехватывает ввод игры и не нажимает клавиши).</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0xB000;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("RoshiqHotkeys") { Width = 0, Height = 0, WindowStyle = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Зарегистрировать сочетание вида "F8", "Ctrl+Shift+F8". Возвращает false, если занято.</summary>
    public bool Register(string gesture, Action action)
    {
        if (!TryParse(gesture, out var mods, out var vk))
        {
            Log.Warn("Горячая клавиша не распознана: " + gesture);
            return false;
        }
        int id = _nextId++;
        if (!Native.RegisterHotKey(_source.Handle, id, mods | Native.MOD_NOREPEAT, vk))
        {
            Log.Warn("Горячая клавиша занята другим приложением: " + gesture);
            return false;
        }
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    public static bool TryParse(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        Key key = Key.None;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= Native.MOD_CONTROL; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                default:
                    if (!Enum.TryParse(raw, true, out key)) return false;
                    break;
            }
        }
        if (key == Key.None) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try { action(); } catch (Exception ex) { Log.Error("Ошибка обработчика горячей клавиши", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
