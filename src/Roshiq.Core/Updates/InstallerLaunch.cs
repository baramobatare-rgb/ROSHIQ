namespace Roshiq.Core.Updates;

/// <summary>
/// Как запускать скачанный установщик. Если ROSHIQ установлен установщиком (рядом лежит деинсталлятор Inno Setup),
/// обновление ставится тихо поверх: без окон мастера, с закрытием программы и её автоматическим перезапуском
/// (это делает сам установщик — запись [Run] с флагом skipifnotsilent). Настройки, история и ярлыки сохраняются.
/// Портативную копию (распакованный zip) тихо не обновляем — там показывается обычный мастер, чтобы пользователь
/// сам решил, ставить ли программу.
/// </summary>
public static class InstallerLaunch
{
    /// <summary>Деинсталлятор, который Inno Setup кладёт в папку установленной программы.</summary>
    public const string UninstallerName = "unins000.exe";

    public static bool IsInstalledCopy(string appDir)
    {
        try { return File.Exists(Path.Combine(appDir, UninstallerName)); }
        catch { return false; }
    }

    /// <summary>Аргументы установщика: тихое обновление поверх установленной копии или обычный мастер.</summary>
    public static string Arguments(bool silent) =>
        silent ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NOCANCEL" : "";
}
