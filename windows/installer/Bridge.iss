; Carries a 1.x install onto the Velopack build. A 1.x updater runs only an asset
; ending in -x64-setup.exe, with Inno's own switches, so this is that asset: it
; installs nothing itself and hands over to Velopack's setup, which starts the
; new app; the new app then moves the old folder's links and removes it.

#define MyAppName "Claude Graft"

; Passed in from the build: iscc /DMyAppVersion=2.0.0
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
; Not the 1.x AppId, whose uninstall entry the new app reads to find the old folder.
AppId={{6C1D0F7E-3B52-4C8A-9E41-0A7D2B5F9C38}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppPublisher={#MyAppName}
PrivilegesRequired=lowest
CreateAppDir=no
Uninstallable=no
DisableWelcomePage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
OutputBaseFilename=ClaudeGraft-{#MyAppVersion}-x64-setup
SetupIconFile=..\ClaudeGraft\Assets\AppIcon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\dist\velopack\ClaudeGraftApp-win-Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Run]
Filename: "{tmp}\ClaudeGraftApp-win-Setup.exe"; Parameters: "--silent"; StatusMsg: "Installing {#MyAppName}..."; Flags: waituntilterminated
