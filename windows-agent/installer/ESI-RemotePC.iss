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
; One shared self-contained layout, not three self-contained single files.
;
; Each single-file bundle carries its own LZMA-compressed copy of the .NET runtime, so the
; installer has nothing left to compress and ends up shipping the runtime three times: 129.7
; MB. Publishing the three executables as a loose self-contained layout instead puts the
; runtime in the directory once, where Inno compresses it at install time, for 43.8 MB. Same
; binaries, same behaviour, two thirds of the download.
;
; The three entry-point executables are 160 KB apphost launchers now rather than 40-60 MB
; bundles, and they only run as a set with the DLLs beside them. That is the whole trade:
; the install directory is a .NET application directory, not three independent binaries.
Source: "..\artifacts\publish\*"; DestDir: "{app}"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

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
; Service registration is not here. It is in RegisterEverything, in [Code], because the
; sequence has to wait for the SCM between each step and a [Run] entry cannot.
;
; Specifically: `sc stop` is asynchronous, and `sc delete` on a service that is still running
; or that any handle is still open to marks the key for deletion instead of removing it. Run
; without waits in between, the sequence reliably ends with `create` failing 1073 (silently,
; because nothing is checking it) and `start` failing 87, leaving a registered service that
; never runs. See WaitForServiceGone.
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

{ Problems found while installing, so the post-install step can report them instead of
  only checking that things exist. Newline-separated, kept in a plain string because Inno's
  Pascal Script has no way to construct a string list and a list of one field is not worth
  the contortions. }
var
  InstallProblems: String;

{ Kept in step with #define HttpsPort above by hand, because Pascal Script cannot read an
  ISPP constant and the completion message quotes this number to the user. }
const
  HttpsPortNumber = 48220;

{ Records one problem found while installing. Newline-separated, so the completion message
  and the log can both print the lot without a list type. }
procedure AddProblem(const Detail: String);
begin
  if InstallProblems <> '' then
    InstallProblems := InstallProblems + #13#10;
  InstallProblems := InstallProblems + '- ' + Detail;
end;

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

{ Runs a sc.exe subcommand and returns its exit code. }
function ScExitCode(const Args: String): Integer;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := ResultCode;
end;

{ Waits until a service is no longer registered, or until the deadline.
  <para>
  This is the step the installer was missing. `sc delete` on a service that is still running
  marks the key for deletion instead of removing it, and the key only disappears once the
  last handle to it closes. Creating the replacement in the meantime fails with 1073, and
  with nothing watching for that the install carries on and leaves a registered service that
  cannot be started - which is exactly what a user sees when they install over a copy that is
  already there, and then report that the program does not open.
  </para> }
function WaitForServiceGone(const Name: String): Boolean;
var
  AttemptsLeft: Integer;
begin
  { A countdown rather than a deadline: Inno's Pascal Script has Sleep but no clock to
    compare against, and 120 x 250 ms is the 30 seconds the message promises. }
  AttemptsLeft := 120;
  while ServiceIsRegistered(Name) do
  begin
    AttemptsLeft := AttemptsLeft - 1;
    if AttemptsLeft <= 0 then
    begin
      AddProblem('the previous ' + Name + ' was still registered 30 s after being ' +
        'stopped and deleted, so the new one could not take its place. Reboot, then run ' +
        'this installer again.');
      Log('WARNING: timed out waiting for ' + Name + ' to disappear from the SCM.');
      Result := False;
      Exit;
    end;
    Sleep(250);
  end;
  Result := True;
end;

{ Whether the agent is actually serving, by connecting to its HTTPS port.
  <para>
  This is deliberately not a check on the SCM's idea of the service state. What the user
  needs to know is whether the agent is answering, and that is the only question the SCM's
  state does not answer: a service can be RUNNING for ten seconds and then crash, and a
  registered-but-stopped service answers nothing at all. Inno's Pascal Script here has only
  the five-argument form of Exec, with no way to read a command's output, so the probe is
  done by a second process and judged on its exit code.
  </para> }
function AgentIsServing: Boolean;
var
  ResultCode: Integer;
begin
  { The TCP probe is done with PowerShell, which is present on every machine this installer
    supports and whose exit code is the connection result. }
  Exec(ExpandConstant('{cmd}'),
       '/c powershell -NoProfile -NonInteractive -Command "' +
       'try { $c = New-Object Net.Sockets.TcpClient; $c.Connect(''127.0.0.1'',' +
       IntToStr(HttpsPortNumber) + '); exit 0 } catch { exit 1 }"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Result := ResultCode = 0;
end;

{ Waits until the agent answers on its port, and says so plainly if it never does.
  <para>
  This is the check that would have caught the bug where a reinstall left a registered but
  permanently stopped service: the install reported success, services.msc showed the agent
  there, and nothing was listening. A post-install step that only asks the SCM whether the
  service exists cannot tell that apart from a working install.
  </para> }
function WaitForAgentServing: Boolean;
var
  AttemptsLeft: Integer;
begin
  { 90 x 500 ms. The countdown stands in for a deadline because Inno's Pascal Script has
    Sleep but no clock function to compare against. }
  AttemptsLeft := 90;
  while True do
  begin
    if AgentIsServing then
    begin
      Result := True;
      Exit;
    end;

    AttemptsLeft := AttemptsLeft - 1;
    if AttemptsLeft <= 0 then
    begin
      AddProblem('the agent is not answering on port ' + IntToStr(HttpsPortNumber) +
        ' 45 s after the install started it. The service is' +
        ' registered, so this is a failure inside the agent rather than a failed install: ' +
        'check C:\ProgramData\ESI-RemotePC\logs for the reason, and run' +
        ' "esirc status" from ' + ExpandConstant('{app}') + '.');
      Log('WARNING: the agent never answered on its port after the install.');
      Result := False;
      Exit;
    end;

    Sleep(500);
  end;
end;

{ Registers the agent service and the session agent's logon task, replacing whatever was
  there before, and starts the agent. Each step's exit code is checked, because a silent
  failure here is what makes an install report success and leave nothing working. }
procedure RegisterEverything;
var
  ResultCode: Integer;
  Q: Char;
begin
  { ---- Remove the previous registration, and wait for it to actually be gone ---- }
  Q := '"';

  Exec(ExpandConstant('{sys}\sc.exe'), 'stop ESIRemotePcAgent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  WaitForServiceGone('ESIRemotePcAgent');

  Exec(ExpandConstant('{sys}\sc.exe'), 'delete ESIRemotePcAgent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  WaitForServiceGone('ESIRemotePcAgent');

  Exec(ExpandConstant('{sys}\schtasks.exe'), '/end /tn ' + Q + 'ESI Remote PC Session Agent' + Q,
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/delete /f /tn ' + Q + 'ESI Remote PC Session Agent' + Q,
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { ---- Create the agent ---- }
  ResultCode := ScExitCode('create ESIRemotePcAgent binPath= ' + Q +
    ExpandConstant('{app}\ESI.RemotePc.Agent.Service.exe') + Q + ' start= auto DisplayName= ' +
    Q + 'ESI Remote PC Agent' + Q + ' obj= LocalSystem');

  { 1073 is "marked for deletion", which is the race described above; 1072 is a duplicate
    name, which means the delete did not take. Both mean the service is not ours to
    configure, so stop here rather than configuring somebody else's service. }
  if ResultCode = 1073 then
  begin
    AddProblem('Windows still had the previous ESI Remote PC service marked for ' +
      'deletion, so the new one could not be created. Reboot, then run this installer again.');
    Exit;
  end;

  if ResultCode = 1072 then
  begin
    AddProblem('The ESI Remote PC service name was already taken by something ' +
      'else. Nothing has been changed; remove that service and run this installer again.');
    Exit;
  end;

  if ResultCode <> 0 then
  begin
    AddProblem('the ESI Remote PC service could not be created (sc.exe returned ' +
      IntToStr(ResultCode) + '). Windows will not have recorded why, so this needs ' +
      'looking at by hand in an elevated console.');
    Exit;
  end;

  { The steps below are refinements. A failure in any of them leaves a working service, so
    they are logged rather than treated as fatal. }
  Exec(ExpandConstant('{sys}\sc.exe'), 'description ' + Q + 'ESIRemotePcAgent' + Q + ' ' + Q +
       'LAN remote control agent. HTTPS API, telemetry, and power control.' + Q,
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { Failure recovery. Without it a crash of the HTTPS listener leaves the machine
    unreachable until somebody walks over to it. Three escalating delays: a transient port
    conflict deserves a fast retry, a persistent failure deserves not to spin. }
  Exec(ExpandConstant('{sys}\sc.exe'),
       'failure ESIRemotePcAgent reset= 86400 actions= restart/5000/restart/15000/restart/60000',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { requiredPrivileges. Undocumented but load-bearing: without it the account's
    SeTcbPrivilege is present but disabled, and the first CreateProcessAsUser into the
    console session fails, so the session agent can only be started by the logon task. }
  Exec(ExpandConstant('{sys}\reg.exe'),
       'add ' + Q + 'HKLM\SYSTEM\CurrentControlSet\Services\ESIRemotePcAgent' + Q +
       ' /v requiredPrivileges /t REG_SZ /d ' + Q +
       'SeTcbPrivilege SeShutdownPrivilege SeAssignPrimaryTokenPrivilege' + Q + ' /f',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { ---- The per-user session helper ----
    A logon-triggered scheduled task rather than a second Windows service. The helper has to
    run inside each interactive session: SendInput is bound to the caller's session and is
    subject to UIPI, and BitBlt has no desktop to read from anywhere else. A per-user service
    (sc create with an empty obj=) is the other way to express that, but the empty obj= is
    easily rejected outright by the SCM, and the rejection is silent from the user's point of
    view - the agent appears to install fine and remote control then never becomes
    available. A logon task is the documented mechanism for exactly this, needs no stored
    credentials, and starts once per interactive user. }
  Exec(ExpandConstant('{sys}\schtasks.exe'),
       '/create /f /sc ONLOGON /tn ' + Q + 'ESI Remote PC Session Agent' + Q + ' /tr ' + Q +
       Q + ExpandConstant('{app}\ESI.RemotePc.Session.exe') + Q + Q + ' /rl LIMITED /it',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { Start it now rather than waiting for the next logon.
    <para>
    An ONLOGON trigger fires when somebody signs in. Installing while somebody is already
    signed in - which is the normal case, and always the case on the machine that ran the
    installer - means the trigger has already been and gone, so the helper never starts: no
    tray icon, and no desktop approval prompt, so pairing cannot be completed either. From
    the user's side the install looks like it did nothing at all.
    </para>
    <para>
    /it on the task means it only runs for an interactive user, so a headless install
    correctly gets nothing here and everything on the next sign-in.
    </para> }
  Exec(ExpandConstant('{sys}\schtasks.exe'),
       '/run /tn ' + Q + 'ESI Remote PC Session Agent' + Q,
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { ---- Start the agent ---- }
  ResultCode := ScExitCode('start ESIRemotePcAgent');
  if ResultCode <> 0 then
    Log('WARNING: sc start returned ' + IntToStr(ResultCode) + '.');

  WaitForAgentServing;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Missing: String;
begin
  if CurStep = ssInstall then
    InstallProblems := '';

  if CurStep = ssPostInstall then
  begin
    { Register the service and the logon task, replacing whatever was there, and start the
      agent. This has to happen here rather than in [Run] because each step waits for the
      SCM to catch up before the next one runs. }
    RegisterEverything;

    { Verify the two registrations actually landed, and that the agent is running rather
      than merely registered. sc.exe reports a syntax problem as text on stderr and still
      exits with an error the installer would otherwise ignore, so the failure mode is a
      successful install with nothing registered. Saying so here is the difference between a
      user who knows what to do next and a user who thinks the product is broken. }
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
      Log('Verified: the agent service and the logon task are both registered.');
    end;

    { Anything the waits above noticed. Said out loud, because a silent install that leaves
      a stopped service is the failure this whole section exists to prevent. }
    if Trim(InstallProblems) <> '' then
    begin
      Log('WARNING: ' + InstallProblems);

      if not WizardSilent then
      begin
        MsgBox('ESI Remote PC installed, but something did not finish:' + #13#10 + #13#10 +
          InstallProblems + #13#10 + #13#10 +
          'The details are in the setup log. Re-running the installer usually clears this.',
          mbError, MB_OK);
      end;
    end
    else if not WizardSilent then
    begin
      { The one thing a new user needs, and the product has no window to show them.
        Note the line breaks: a line starting with #13 is read as an ISPP directive, which
        is a baffling way to lose five minutes. }
      MsgBox('ESI Remote PC is installed and running.' + #13#10 + #13#10 +
        'To pair a phone, open this page on the PC:' + #13#10 +
        'https://localhost:' + IntToStr(HttpsPortNumber) + '/pairing' + #13#10 + #13#10 +
        'There is no window to open. The agent runs as a Windows service; its tray icon ' +
        'and the pairing approval prompt only exist while somebody is signed in to the PC.',
        mbInformation, MB_OK);
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
