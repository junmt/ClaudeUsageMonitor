; Claude Usage Monitor のインストーラー定義（Inno Setup 6）
;
; ビルド例（リポジトリのルートで実行。先に dotnet publish で publish\win-x64 を作っておく）:
;   iscc /DAppVersion=0.2.0 /DArch=x64 installer\ClaudeUsageMonitor.iss
;
; 出力: dist\ClaudeUsageMonitor-<AppVersion>-win-<Arch>-setup.exe

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
; ファイルのバージョン情報には数字のみ使える（0.2.0-beta.1 → 0.2.0）
#ifndef NumericVersion
  #define NumericVersion AppVersion
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-" + Arch
#endif

#define AppName "Claude Usage Monitor"
#define AppExeName "ClaudeUsageMonitor.exe"
#define AppUrl "https://github.com/junmt/ClaudeUsageMonitor"
; アプリ内の自動起動設定（StartupRegistration.cs）と同じ値名
#define RunValueName "ClaudeUsageMonitor"

[Setup]
AppId={{8C1F3B9E-5A4D-4E7B-9C2A-3D6F1E8B7A40}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=junmt
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#NumericVersion}
; 管理者権限なしでユーザーごとにインストールする（%LOCALAPPDATA%\Programs\ClaudeUsageMonitor）
PrivilegesRequired=lowest
DefaultDirName={autopf}\ClaudeUsageMonitor
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\dist
OutputBaseFilename=ClaudeUsageMonitor-{#AppVersion}-win-{#Arch}-setup
SetupIconFile=..\src\ClaudeUsageMonitor\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 起動中のアプリ（App.xaml.cs の単一起動用ミューテックス）を検出して終了を促す
AppMutex=ClaudeUsageMonitor.SingleInstance
CloseApplications=yes
RestartApplications=no
; Windows 10 22H2 以降
MinVersion=10.0.19045
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
japanese.AutoStart=Windows の起動時に自動起動する
english.AutoStart=Start automatically when Windows starts
japanese.OtherTasks=その他:
english.OtherTasks=Other:

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:OtherTasks}"

[Files]
Source: "{#SourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#RunValueName}"; \
    ValueData: """{app}\{#AppExeName}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

// アプリの設定画面で自動起動をオンにした場合もアンインストール時に登録を消す（このインストール先を指すものだけ）
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if RegQueryStringValue(HKCU, RunKey, '{#RunValueName}', Value) then
    begin
      if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Value)) > 0 then
        RegDeleteValue(HKCU, RunKey, '{#RunValueName}');
    end;
  end;
end;
