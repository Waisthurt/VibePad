#define AppName "VibePad Agent"
#ifndef AppVersion
#define AppVersion "0.1.4"
#endif
#define AppPublisher "VibePad"
#define AppExeName "VibePad.Agent.exe"

[Setup]
AppId={{B80DA5AB-54AF-44C1-9CBA-D52B3494BF02}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\VibePad Agent
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=VibePad-Agent-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExeName}

[Files]
Source: "payload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""VibePad Agent (TCP)"" dir=in action=allow program=""{app}\{#AppExeName}"" protocol=TCP localport=8765 remoteip=localsubnet profile=any"; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""VibePad Mouse Motion (UDP)"" dir=in action=allow program=""{app}\{#AppExeName}"" protocol=UDP localport=8767 remoteip=localsubnet profile=any"; Flags: runhidden
Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""VibePad Agent (TCP)"""; RunOnceId: "RemoveVibePadTcpFirewallRule"; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""VibePad Mouse Motion (UDP)"""; RunOnceId: "RemoveVibePadUdpFirewallRule"; Flags: runhidden
