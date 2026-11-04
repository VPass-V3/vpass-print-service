; Inno Setup script for the VPass Print Service (Windows x64).
; Build: iscc /DAppVersion=1.0.0 /DPublishDir=..\..\artifacts\win-x64 vpass-print-service.iss
; Installs to Program Files, registers the Windows Service, and (optionally) pairs with the
; code the admin got from the VPass portal (Setup > Badge Printing > Manage printers).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\artifacts\win-x64"
#endif

[Setup]
AppId={{5F9C2C4E-5D2B-4F7A-9A6B-VPASSPRINT01}
AppName=VPass Print Service
AppVersion={#AppVersion}
AppPublisher=VPass
AppPublisherURL=https://www.vpass.io
DefaultDirName={autopf}\VPass\PrintService
DefaultGroupName=VPass
DisableProgramGroupPage=yes
OutputDir=..\..\artifacts
OutputBaseFilename=vpass-print-service-{#AppVersion}-win-x64-setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
UninstallDisplayName=VPass Print Service

[Files]
Source: "{#PublishDir}\vpass-print.exe"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; Put vpass-print on PATH for the Command Prompt instructions shown in the admin portal.
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Check: NeedsAddPath('{app}')

[Run]
Filename: "{app}\vpass-print.exe"; Parameters: "pair {code:PairingCode} --region {code:Region}"; StatusMsg: "Pairing with VPass..."; Flags: runhidden waituntilterminated; Check: HasPairingCode
Filename: "{app}\vpass-print.exe"; Parameters: "install"; StatusMsg: "Installing the background service..."; Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "{app}\vpass-print.exe"; Parameters: "uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "UninstallService"

[Code]
var
  PairPage: TInputQueryWizardPage;
  RegionPage: TInputOptionWizardPage;

function NeedsAddPath(Param: string): boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(ExpandConstant(Param)) + ';', ';' + Uppercase(OrigPath) + ';') = 0;
end;

procedure InitializeWizard;
begin
  PairPage := CreateInputQueryPage(wpSelectDir, 'Pair with VPass',
    'Enter the pairing code from the VPass admin portal',
    'In VPass go to Setup > Badge Printing > Manage printers, add a printer and copy its code. Leave blank to pair later with "vpass-print pair <code>".');
  PairPage.Add('Pairing code (e.g. 7KQ2-M9XR):', False);

  RegionPage := CreateInputOptionPage(PairPage.ID, 'VPass region',
    'Which VPass region does your organisation use?', 'Pick the region that matches your dashboard address.', True, False);
  RegionPage.Add('Australia (dashboard.vpass.io)');
  RegionPage.Add('United Kingdom (dashboard-uk.vpass.io)');
  RegionPage.Add('United States (dashboard-us.vpass.io)');
  RegionPage.Add('Canada (dashboard-ca.vpass.io)');
  RegionPage.SelectedValueIndex := 0;
end;

function HasPairingCode: Boolean;
begin
  Result := Trim(PairPage.Values[0]) <> '';
end;

function PairingCode(Param: string): string;
begin
  Result := Trim(PairPage.Values[0]);
end;

function Region(Param: string): string;
begin
  case RegionPage.SelectedValueIndex of
    1: Result := 'uk';
    2: Result := 'us';
    3: Result := 'ca';
  else
    Result := 'au';
  end;
end;
