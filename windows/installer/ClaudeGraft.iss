; Inno Setup script for Claude Graft (Windows)
; Produces a single installer .exe that bundles the self-contained app,
; installs per-user with no admin rights, and handles the VC++ runtime.

#define MyAppName "Claude Graft"
#define MyAppExeName "ClaudeGraft.exe"
#define MyAppPublisher "Claude Graft"
#define MyAppURL "https://github.com/snowtyler/claude-graft"

; Version is passed in from the build: iscc /DMyAppVersion=1.1.0
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{E8F3B2A1-7C4D-4E5F-9A1B-3D6E8F2C4A5B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\ClaudeGraft
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=ClaudeGraft-{#MyAppVersion}-x64-setup
SetupIconFile=..\ClaudeGraft\Assets\AppIcon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startuplaunch"; Description: "Start automatically when I sign in"; GroupDescription: "Startup:"; Flags: checkedonce

[Files]
Source: "..\dist\ClaudeGraft\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The VC++ runtime redistributable, installed silently if needed.
Source: "vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: VCRedistNeeded

[Icons]
Name: "{userstartmenu}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Run extra Claude Desktop profiles"
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startuplaunch

[Run]
; Install VC++ runtime silently before launching the app.
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Visual C++ Runtime..."; Check: VCRedistNeeded; Flags: waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill"; Parameters: "/F /IM ClaudeGraft.exe"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function VCRedistNeeded: Boolean;
var
  Version: String;
begin
  // Check for the VC++ 2015-2022 runtime (14.x) via the registry.
  Result := True;
  if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Version', Version) then
    // Any 14.x version is sufficient for .NET apps.
    if (CompareStr(Version, 'v14.') >= 0) then
      Result := False;
end;
