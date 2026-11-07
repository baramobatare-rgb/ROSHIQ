using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Localization;

namespace Roshiq.App.Views;

/// <summary>
/// Поле горячей клавиши: щёлкните и нажмите сочетание (Ctrl/Shift/Alt + клавиша).
/// Esc — отмена, Backspace/Delete — вернуть прежнее значение. Текст вида "Ctrl+Shift+F8" понимает HotkeyManager.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    private string _before = "";

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        ToolTip = Loc.T("hk_press");
        GotKeyboardFocus += (_, _) => { _before = Text; Text = Loc.T("hk_waiting"); };
        LostKeyboardFocus += (_, _) => { if (Text == Loc.T("hk_waiting")) Text = _before; };
    }

    /// <summary>Сочетание изменено пользователем.</summary>
    public event EventHandler? GestureChanged;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
            return; // ждём основную клавишу
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None && key == Key.Escape) { Text = _before; MoveFocusAway(); return; }
        if (mods == ModifierKeys.None && key is Key.Back or Key.Delete) { Text = _before; MoveFocusAway(); return; }
        if (mods == ModifierKeys.None && key == Key.Tab) { e.Handled = false; return; }

        var gesture = (mods.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (mods.HasFlag(ModifierKeys.Shift) ? "Shift+" : "")
                      + (mods.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + key;
        Text = HotkeyRules.Normalize(gesture);
        _before = Text;
        GestureChanged?.Invoke(this, EventArgs.Empty);
        MoveFocusAway();
    }

    private void MoveFocusAway()
    {
        var scope = FocusManager.GetFocusScope(this);
        FocusManager.SetFocusedElement(scope, null);
        Keyboard.ClearFocus();
    }
}
