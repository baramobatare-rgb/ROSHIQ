; Inno Setup 6 — установщик ROSHIQ (без прав администратора, в профиль пользователя)
#define AppName "ROSHIQ"
; Версию передаёт build.ps1 (/DAppVersion=…) из Directory.Build.props; значение ниже — для ручной сборки
#ifndef AppVersion
  #define AppVersion "0.13.0"
#endif
#define AppExe "Roshiq.exe"

[Setup]
AppId={{7B1C2E64-5F0A-4C61-9D3E-0D0A7A5E2B11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=ROSHIQ (неофициальный проект, не связан с Valve)
DefaultDirName={localappdata}\Programs\ROSHIQ
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=Roshiq-Setup-{#AppVersion}
SetupIconFile=..\src\Roshiq.App\Assets\roshiq.ico
UninstallDisplayIcon={app}\{#AppExe}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
; Подпись: build.ps1 передаёт /DSign и команду /Sroshiq=…, если настроен сертификат (см. RELEASING.md)
#ifdef Sign
SignTool=roshiq
SignedUninstaller=yes
#endif
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\Roshiq\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; обновление поверх DOTA PULSE (тот же AppId): убрать старые exe/dll и ярлыки
Type: files; Name: "{app}\DotaPulse*"
Type: files; Name: "{userdesktop}\DOTA PULSE.lnk"
Type: filesandordirs; Name: "{userprograms}\DOTA PULSE"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; настройки пользователя (%AppData%\Roshiq) намеренно не удаляются
Type: filesandordirs; Name: "{app}"
