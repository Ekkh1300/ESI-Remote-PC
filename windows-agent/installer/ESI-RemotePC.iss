; ESI Remote PC - Windows installer
;
; Build with:  iscc /DAppVersion=1.0.0 installer\ESI-RemotePC.iss
; (Inno Setup 6, https://jrsoftware.org/isdl.php)
;
; The script packages the three published binaries into one MSI-free installer that
; registers both Windows services, sets the failure-recovery actions the SCM needs, and
; writes the requiredPrivileges registry value the service host reads at start.
;
; It deliberately does NOT run the app on finish. Starting a service that opens a listening
; socket as a side effect of "installing" is surprising on a machine where the user only
; wanted the files, and the first run is better done deliberately from the Start menu.

#define AppName        "ESI Remote PC"
#define AppPublisher   "ESI Remote PC"
#define AppVersion     "1.0.0"
#define AppURL         "https://github.com/esi/remote-pc"

; Must match ProtocolConstants.DefaultHttpsPort. The firewall rule below uses it too.
#define HttpsPort      48220
#define DiscoveryPort  48211

[Setup]
AppId={{7C4A1E92-3B5D-4A7E-9E21-0D6F2B8C41A7}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\ESI Remote PC
DefaultGroupName=ESI Remote PC
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\ESI.RemotePc.Agent.Service.exe
UninstallDisplayName={#AppName}
OutputDir=..\artifacts
OutputBaseFilename=ESI-RemotePC-Setup-{#AppVersion}-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Per-machine by default: the service must run as LocalSystem, so a per-user install under
; %LocalAppData% would not work and would fail at the SCM call.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableDirPage=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "addfirewall"; Description: "Add a Windows Firewall rule for the agent's LAN port"; \
  GroupDescription: "Networking:"; Flags: checkedonce
Name: "createdesktop"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; \
  Flags: unchecked

[Files]
; The three published executables. They are self-contained single files, so there is no
; dependency tree to lay down and the install directory can be moved or deleted freely.
; All three are packed into this one installer executable, which is the deliverable.
Source: "..\artifacts\publish\ESI.RemotePc.Agent.Service.exe"; DestDir: "{app}"; \
  Flags: ignoreversion
Source: "..\artifacts\publish\ESI.RemotePc.Session.exe";         DestDir: "{app}"; \
  Flags: ignoreversion
Source: "..\artifacts\publish\ESI.RemotePc.Agent.Cli.exe";       DestDir: "{app}"; \
  Flags: ignoreversion

[Dirs]
; The service creates this on first run; making it here means the ACLs are set once, at
; install, instead of by a service running as LocalSystem.
Name: "{commonappdata}\ESI-RemotePC"; Permissions: users-modify

[Icons]
Name: "{group}\ESI Remote PC (status and pairing)"; \
  Filename: "{app}\ESI.RemotePc.Agent.Cli.exe"; Parameters: "status"; \
  WorkingDir: "{app}"; Comment: "Show the agent status and the pairing address"
Name: "{group}\Pair a device"; Filename: "https://localhost:{#HttpsPort}/pairing"
Name: "{group}\Uninstall ESI Remote PC"; Filename: "{uninstallexe}"
Name: "{autodesktop}\ESI Remote PC"; Filename: "{app}\ESI.RemotePc.Agent.Cli.exe"; \
  Parameters: "status"; WorkingDir: "{app}"; Tasks: createdesktop

[Run]
; Clear any previous registration first. sc.exe reports an error for a service that is not
; installed, which is exactly what we want to ignore, so these entries carry no Check.
;
; The two registrations are verified afterwards rather than checked inline. A quoting
; mistake in binPath prints "Unrecognized command" to stderr and Inno carries on regardless,
; which would leave an installed program with no services and no error to go on. The
; post-install step in [Code] queries the SCM and says so plainly if either is missing.
Filename: "{sys}\sc.exe"; Parameters: "stop ESIRemotePcAgent"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "delete ESIRemotePcAgent"; Flags: runhidden
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /f /tn ""ESI Remote PC Session Agent"""; Flags: runhidden

Filename: "{sys}\sc.exe"; Parameters: "create ESIRemotePcAgent binPath= ""{app}\ESI.RemotePc.Agent.Service.exe"" start= auto DisplayName= ""ESI Remote PC Agent"" obj= LocalSystem"; \
  Flags: runhidden; StatusMsg: "Registering the ESI Remote PC service"

Filename: "{sys}\sc.exe"; Parameters: "description ""ESIRemotePcAgent"" ""LAN remote control agent. HTTPS API, telemetry, and power control."""; \
  Flags: runhidden

; Failure recovery. Without it a crash of the HTTPS listener leaves the machine unreachable
; until somebody walks over to it. Three escalating delays: a transient port conflict
; deserves a fast retry, a persistent failure deserves not to spin.
Filename: "{sys}\sc.exe"; Parameters: "failure ESIRemotePcAgent reset= 86400 actions= restart/5000/restart/15000/restart/60000"; \
  Flags: runhidden

; requiredPrivileges. Undocumented but load-bearing: without it the account's SeTcbPrivilege
; is present but disabled, and the first CreateProcessAsUser into the console session fails,
; so the session agent can only be started by the logon task.
Filename: "{sys}\reg.exe"; \
  Parameters: "add ""HKLM\SYSTEM\CurrentControlSet\Services\ESIRemotePcAgent"" /v requiredPrivileges /t REG_SZ /d ""SeTcbPrivilege SeShutdownPrivilege SeAssignPrimaryTokenPrivilege"" /f"; \
  Flags: runhidden

; The per-user session helper.
;
; A logon-triggered scheduled task rather than a second Windows service. The helper has to
; run inside each interactive session: SendInput is bound to the caller's session and is
; subject to UIPI, and BitBlt has no desktop to read from anywhere else. A per-user service
; (sc create with an empty obj=) is the other way to express that, but the empty obj= is
; easily rejected outright by the SCM, and the rejection is silent from the user's point of
; view - the agent appears to install fine and remote control then never becomes available.
; A logon task is the documented mechanism for exactly this, needs no stored credentials,
; and starts once per interactive user.
Filename: "{sys}\schtasks.exe"; Parameters: "/create /f /sc ONLOGON /tn ""ESI Remote PC Session Agent"" /tr ""{app}\ESI.RemotePc.Session.exe"" /rl LIMITED /it"; \
  Flags: runhidden; StatusMsg: "Registering the session agent"
Filename: "{sys}\sc.exe"; Parameters: "start ESIRemotePcAgent"; Flags: runhidden; \
  StatusMsg: "Starting the agent"

Filename: "{app}\ESI.RemotePc.Agent.Cli.exe"; Parameters: "status"; \
  Flags: runhidden waituntilterminated; Description: "Show the installed status"

[UninstallRun]
; Stop before deleting, or the SCM keeps the exe mapped and the file cannot be removed until
; the next reboot - which looks to the user like a failed uninstall.
Filename: "{sys}\schtasks.exe"; Parameters: "/end /tn ""ESI Remote PC Session Agent"""; Flags: runhidden; RunOnceId: "StopSession"
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /f /tn ""ESI Remote PC Session Agent"""; Flags: runhidden; RunOnceId: "DeleteSession"
Filename: "{sys}\sc.exe"; Parameters: "stop ESIRemotePcAgent"; Flags: runhidden; RunOnceId: "StopAgent"
Filename: "{sys}\sc.exe"; Parameters: "delete ESIRemotePcAgent"; Flags: runhidden; RunOnceId: "DeleteAgent"

[UninstallDelete]
; Certificates, keys, the device database and logs. Deleting these on uninstall is
; deliberate: leaving a usable TLS identity and a list of paired devices behind on a machine
; that no longer has the software is exactly the kind of residue that gets missed during an
; audit. There is no 'keep my data' option; reinstalling simply means pairing again.
Type: filesandordirs; Name: "{commonappdata}\ESI-RemotePC"

[Code]
{ The firewall rules are added here rather than in [Run] because they are conditional on a
  task the user chose, and this is the only place that can ask. }

{ Reports whether a service is registered with the SCM. }
function ServiceIsRegistered(const Name: String): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'query ' + Name, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := ResultCode = 0;
end;

{ Reports whether a scheduled task is registered with Task Scheduler. }
function TaskIsRegistered(const Name: String): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/query /tn ' + Chr(34) + Name + Chr(34), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := ResultCode = 0;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Missing: String;
begin
  if CurStep = ssPostInstall then
  begin
    { Verify the two registrations actually landed. sc.exe reports a syntax problem as text
      on stderr and still exits with an error the installer would otherwise ignore, so the
      failure mode is a successful install with nothing registered. Saying so here is the
      difference between a user who knows to re-run as administrator and a user who thinks
      the product is broken. }
    Missing := '';
    if not ServiceIsRegistered('ESIRemotePcAgent') then
      Missing := Missing + 'ESIRemotePcAgent' + #13#10;
    if not TaskIsRegistered('ESI Remote PC Session Agent') then
      Missing := Missing + 'logon task: ESI Remote PC Session Agent' + #13#10;

    if Missing <> '' then
    begin
      Log('WARNING: these services were not registered:' + #13#10 + Missing);
    end
    else
    begin
      Log('Verified: both services are registered with the Service Control Manager.');
    end;

    if WizardIsTaskSelected('addfirewall') then
    begin
      { Scoped to the private and domain profiles only. On a public network - a coffee shop,
        an airport - a rule that opened this port would expose the agent to the internet, and
        the agent's own LAN guard cannot help with that because the traffic would already have
        arrived. }
      Exec(ExpandConstant('{sys}\netsh.exe'),
        'advfirewall firewall add rule name="ESI Remote PC (LAN)" dir=in action=allow ' +
        'protocol=TCP localport={#HttpsPort} profile=private,domain',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

      Exec(ExpandConstant('{sys}\netsh.exe'),
        'advfirewall firewall add rule name="ESI Remote PC discovery (LAN)" dir=in action=allow ' +
        'protocol=UDP localport={#DiscoveryPort} profile=private,domain',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{sys}\netsh.exe'),
      'advfirewall firewall delete rule name="ESI Remote PC (LAN)"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\netsh.exe'),
      'advfirewall firewall delete rule name="ESI Remote PC discovery (LAN)"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
