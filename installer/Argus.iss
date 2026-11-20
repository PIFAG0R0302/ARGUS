; ARGUS Windows x64 Installer Script (Inno Setup 6)
; Clean native installer without UPX or obfuscation; Defender & HVCI friendly.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef PayloadDir
  #define PayloadDir "..\publish\release-stage"
#endif

#ifndef OutputDirPath
  #define OutputDirPath "..\ArgusSetup"
#endif

#define MyAppName "ARGUS"
#define MyAppPublisher "Argus"
#define MyAppExeName "Argus.exe"

[Setup]
AppId={{D4B9F2E1-8A4C-4F92-B73E-1C5E9F80A311}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Argus
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDirPath}
OutputBaseFilename=Argus-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\src\Argus.App\Assets\argus.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
AppMutex=Local\Argus_Desktop_SingleInstance
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoDescription=ARGUS — моніторинг, очищення, діагностика та міграція Windows

[Languages]
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ukrainian.DesktopIconTask=Створити ярлик на Робочому столі
english.DesktopIconTask=Create a desktop shortcut
ukrainian.StartupTask=Запускати ARGUS разом із Windows (згорнутим у системний трей)
english.StartupTask=Start ARGUS with Windows (minimized to system tray)
ukrainian.LaunchAfterInstall=Запустити ARGUS після завершення встановлення
english.LaunchAfterInstall=Launch ARGUS after setup completes
ukrainian.UserGuideIcon=Інструкція користувача ARGUS
english.UserGuideIcon=ARGUS User Guide

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIconTask}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "{cm:StartupTask}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\Argus.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\USER-GUIDE.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\rules\*"; DestDir: "{app}\rules"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UserGuideIcon}"; Filename: "{app}\USER-GUIDE.md"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Argus"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchAfterInstall}"; Flags: shellexec nowait postinstall skipifsilent
