[Setup]
AppName=Delitools
AppVersion=2.1.5
AppPublisher=Felipe Assis
AppVerName=Delitools 2.1.5
DefaultDirName={autopf}\Delitools
DefaultGroupName=Delitools
OutputDir=.
OutputBaseFilename=Delitools_Setup_2.1.5
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
UninstallDisplayName=Delitools
UninstallDisplayIcon={app}\Delitools.exe
WizardStyle=modern
SetupIconFile=

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar &icone na area de trabalho"; GroupDescription: "Icones adicionais:"

[Files]
Source: "Delitools.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "NetConfigTools\*"; DestDir: "{app}\NetConfigTools"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: DirExists(ExpandConstant('{src}\NetConfigTools'))
; A pasta Drivers NAO e mais empacotada: o app nao usa mais (USB/rede sempre com driver generico),
; e os .sys de kernel dela (Epson TMUSB64/TMUSBXP) sao o maior gatilho de falso-positivo de antivirus.

[Icons]
Name: "{group}\Delitools"; Filename: "{app}\Delitools.exe"
Name: "{group}\Desinstalar Delitools"; Filename: "{uninstallexe}"
Name: "{commondesktop}\Delitools"; Filename: "{app}\Delitools.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Delitools.exe"; Description: "Abrir Delitools agora"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
