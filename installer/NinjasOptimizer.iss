; Instalador do Ninjas Optimizer (Inno Setup 6)
; Compilar: ISCC.exe /DAppVersion=0.1.0 /DPublishDir=..\artifacts\publish NinjasOptimizer.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#define AppName "Ninjas Optimizer"
#define AppExe "NinjasOptimizer.exe"

[Setup]
AppId={{6E1B7C3A-4408-4C8C-9A1E-44088C0A1B2C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Ninjas Optimizer
DefaultDirName={autopf}\Ninjas Optimizer
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\NinjasOptimizer.App\Assets\app.ico
OutputDir=Output
OutputBaseFilename=NinjasOptimizer-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Instalar em Program Files exige admin; o app em si roda como usuário comum.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Observação: a desinstalação NÃO apaga %LocalAppData%\NinjasOptimizer (journal e backups .reg),
; para que o usuário ainda possa reverter alterações se reinstalar.
