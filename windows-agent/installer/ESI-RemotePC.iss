; ESI Remote PC - Windows installer
;
; Build with:  iscc /DAppVersion=1.0.1 installer\ESI-RemotePC.iss
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
#define AppVersion     "1.1.1"
#define AppURL         "https://github.com/Ekkh1300/ESI-Remote-PC"

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
; The setup executable gets the same mark as the shortcuts. Without it the installer has the
; generic Inno Setup icon, which reads as "some other program" in a downloads folder.
SetupIconFile=..\..\release\esi.ico
UninstallDisplayIcon={app}\esi.ico
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
  Flags: checkedonce

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
; The shortcuts point at this rather than at one of the executables, because the executables
; are 160 KB .NET apphost launchers that carry no icon of their own - every shortcut came
; out with an empty IconLocation, which is what "there is no icon" meant.
Source: "..\..\release\esi.ico"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
; The service creates this on first run; making it here means the ACLs are set once, at
; install, instead of by a service running as LocalSystem.
Name: "{commonappdata}\ESI-RemotePC"; Permissions: users-modify

[InstallDelete]
; Shortcuts that an earlier version of this installer created and that no longer have a
; counterpart. Inno overwrites a shortcut whose name is unchanged and silently leaves every
; other file alone, so renaming an entry does not remove the old one: upgrading left the
; user with a Start Menu and a desktop shortcut that still ran `esirc status`, still had no
; icon, and still flashed a console window and closed. Renaming an entry is therefore a
; breaking change unless the old name is deleted here.
Type: files; Name: "{group}\ESI Remote PC (status and pairing).lnk"
Type: files; Name: "{group}\Pair a device.url"
Type: files; Name: "{autodesktop}\ESI Remote PC (status and pairing).lnk"

[Icons]
; The desktop and Start Menu entries open the pairing page in the browser. They used to run
; `esirc status` directly, and that is a console program: launched from a shortcut it opens a
; black window, prints eleven lines and closes again in about a third of a second, which is
; indistinguishable from "nothing happens". This product has no window of its own, so the
; pairing page is the one thing that can actually be shown to a person.
Name: "{group}\ESI Remote PC"; \
  Filename: "https://localhost:{#HttpsPort}/pairing"; IconFilename: "{app}\esi.ico"; \
  Comment: "Open the pairing page for this PC"
; Kept for anyone who wants the numbers, but through `cmd /k` so the window survives the
; command instead of vanishing with it.
Name: "{group}\Agent status (console)"; \
  Filename: "{sys}\cmd.exe"; \
  Parameters: "/k ""{app}\ESI.RemotePc.Agent.Cli.exe"" status"; \
  WorkingDir: "{app}"; IconFilename: "{app}\esi.ico"; \
  Comment: "Show the agent status and the pairing address"
Name: "{group}\Uninstall ESI Remote PC"; Filename: "{uninstallexe}"; IconFilename: "{app}\esi.ico"
Name: "{autodesktop}\ESI Remote PC"; \
  Filename: "https://localhost:{#HttpsPort}/pairing"; IconFilename: "{app}\esi.ico"; \
  Comment: "Open the pairing page for this PC"

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

  ; The install directory itself. Every file in it is removed by the [Files] entries, but the
  ; empty folder survives, because Inno only removes an application directory it created
  ; through [Dirs] and this one is populated wholesale from a publish output. An empty
  ; "ESI Remote PC" left in Program Files after an uninstall reads as a failed removal.
  Type: filesandordirs; Name: "{app}"

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

{ Reports whether a service is registered *and* running.
    Registered is not the same as working, and the gap between the two is where this
    product spent months: the key existed, every check the installer made passed, and the
    agent still refused to start on every boot. sc query prints the state in a line
    beginning "        STATE", and Exec cannot capture stdout, hence the temp file. }
function ServiceIsRunning(const Name: String): Boolean;
var
  OutputFile: String;
  { AnsiString, and filename first: the declaration is
    LoadStringFromFile(const FileName: String; var S: AnsiString). }
  Contents: AnsiString;
  ResultCode: Integer;
begin
  OutputFile := ExpandConstant('{tmp}\esi-sc-query.txt');
  DeleteFile(OutputFile);
  Exec(ExpandConstant('{sys}\cmd.exe'), '/c ' + ExpandConstant('{sys}\sc.exe') +
       ' query ' + Name + ' > ' + Chr(34) + OutputFile + Chr(34) + ' 2>&1',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
  begin
    Result := False;
    exit;
  end;
  if not LoadStringFromFile(OutputFile, Contents) then
  begin
    Result := False;
    exit;
  end;
  Result := Pos('RUNNING', Uppercase(Contents)) > 0;
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

{ Stops the agent service, and waits until it has actually gone.
  <para>
  This is needed on both the install and the uninstall path, and on the uninstall path it is
  the difference between a clean removal and a directory left behind.
  </para>
  <para>
  The uninstaller's [UninstallRun] entries - which include `sc stop` - run *after* the files
  have been removed. By then the running service is holding several hundred DLLs open out of
  the very directory being deleted, and the removal silently leaves all of them. The
  uninstaller was reporting success and leaving a complete 139 MB application directory on
  disk.
  </para>
  <para>
  Waiting is done by asking the agent's own port rather than by parsing `sc query`, because
  Inno's Pascal Script here offers no way to read a command's output. The port answering is
  the more meaningful signal anyway: it is the question a user is actually asking.
  </para> }
procedure StopAgentService;
var
  AttemptsLeft: Integer;
begin
  if not ServiceIsRegistered('ESIRemotePcAgent') then
    Exit;

  ScExitCode('stop ESIRemotePcAgent');

  AttemptsLeft := 60;
  while AgentIsServing and (AttemptsLeft > 0) do
  begin
    Sleep(500);
    AttemptsLeft := AttemptsLeft - 1;
  end;

  if AgentIsServing then
    Log('WARNING: the agent was still answering on its port 30 s after being stopped.');
end;

{ Stops the session helper, so its executable and DLLs are not locked.
  <para>
  This exists because of a real failure. Installing over a working install failed with exit
  code 5 and left the agent stopped: the helper is running as the signed-in user, it holds
  ESI.RemotePc.Session.exe and its DLLs open, and the installer cannot replace a file that is
  in use. The same lock then stopped the uninstaller from removing the directory.
  </para>
  <para>
  Two steps, in this order, and the second is unconditional on purpose. `schtasks /end` asks
  Task Scheduler to stop the instances it started, which is the tidy way, but it is
  asynchronous and it knows nothing about a helper that was started by something else. The
  kill catches both, and it is blunt on purpose: this is a per-user tray helper that is
  about to be replaced by a newer copy, and the worst a force-kill can cost is a tray icon
  that reappears when the new one registers its task.
  </para> }
procedure StopSessionAgent;
var
  ResultCode: Integer;
  Q: Char;
  AttemptsLeft: Integer;
begin
  Q := '"';

  Exec(ExpandConstant('{sys}\schtasks.exe'),
       '/end /tn ' + Q + 'ESI Remote PC Session Agent' + Q,
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { A moment for the tidy path to take effect, since /end is asynchronous. Two seconds is
    generous; the kill below is what actually guarantees the files are free. }
  Sleep(2000);

  Exec(ExpandConstant('{cmd}'), '/c taskkill /f /im ESI.RemotePc.Session.exe',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { taskkill is asynchronous too, so give the handles a moment to be released before the
    installer starts overwriting the files. }
  AttemptsLeft := 20;
  while AttemptsLeft > 0 do
  begin
    Sleep(250);
    AttemptsLeft := AttemptsLeft - 1;
  end;
end;

{ Registers the session agent's logon task, and starts it.
  <para>
  This is a call out to `esirc task register` rather than a schtasks command line, and the
  reason is not tidiness. `schtasks /create /tr "C:\Program Files\...\something.exe"` cannot
  be made to work: Task Scheduler parses the action out of the raw command line itself rather
  than taking it as an argv element, so it stores the quoted path perfectly and then fails to
  run it with 0x80070002, "file not found". The workarounds that are supposed to cover that -
  a second pair of quotes, or routing the launch through `cmd /c` - are rejected by the same
  parser with "Invalid argument/option - 'Files\ESI'".
  </para>
  <para>
  Passing the task as an XML document sidesteps it: the command is element content, where a
  space is just a character. The document has to be written in the encoding it declares, and
  Inno's Pascal Script has one string-to-file routine and no way to say which encoding that
  is - so the writing is done in C#, where it is unambiguous and testable.
  </para> }
procedure RegisterSessionAgentTask;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{app}\ESI.RemotePc.Agent.Cli.exe'), 'task register',
       ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if ResultCode <> 0 then
    AddProblem('the session helper''s logon task could not be registered, so there will be ' +
      'no tray icon and no approval prompt. Without the prompt, pairing cannot be completed ' +
      'and remote control cannot work. "esirc task register" returned ' +
      IntToStr(ResultCode) + '.');
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

  { Inno Setup is a 32-bit program, so the sc.exe it runs is the 32-bit one, and the 32-bit
    sc.exe stamps WOW64 = IMAGE_FILE_MACHINE_I386 into the key it creates. Every executable
    this installer publishes is x64. The SCM honours that value when it launches the service
    image, so a service carrying a 32-bit flag over a 64-bit binary is a registration that
    cannot start - and on this machine it failed on every single boot with SCM 7000
    "The parameter is incorrect", while the same executable ran perfectly when launched by
    hand. Removing the value leaves the key describing what is actually in the file. }
  Exec(ExpandConstant('{sys}\reg.exe'),
       'delete ' + Q + 'HKLM\SYSTEM\CurrentControlSet\Services\ESIRemotePcAgent' + Q +
       ' /v WOW64 /f',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

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
    console session fails, so the session agent can only be started by the logon task.

    It has to be REG_MULTI_SZ, and getting that wrong is the single most expensive bug in
    this product's history. Written as REG_SZ it is one string naming a privilege actually
    called "SeTcbPrivilege SeShutdownPrivilege SeAssignPrimaryTokenPrivilege", which does
    not exist; the SCM's privilege lookup then fails and it refuses to launch the image at
    all. The user sees SCM 7000 "The parameter is incorrect" on every boot, `sc start`
    returns 87, and the agent's own log has no line after the boot timestamp because the
    process never reached its first one - so it looks exactly like a broken executable. It
    is not: the same binary runs perfectly by hand, and the same registration starts on
    the first attempt once the value is a real multi-string. }
  { The list is passed through reg.exe rather than RegWriteMultiStringValue: that routine takes
    RootKey as an HKEY and the payload as a plain string, so it cannot express three separate
    privileges. Note the trailing \0 on the last element - reg.exe rejects the \0\0 that
    the documentation shows, with "Invalid value specified for '/d'". }
  Exec(ExpandConstant('{sys}\reg.exe'),
       'add ' + Q + 'HKLM\SYSTEM\CurrentControlSet\Services\ESIRemotePcAgent' + Q +
       ' /v requiredPrivileges /t REG_MULTI_SZ /d ' + Q +
       'SeTcbPrivilege\0SeShutdownPrivilege\0SeAssignPrimaryTokenPrivilege\0' + Q + ' /f',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
    AddProblem('requiredPrivileges could not be written as a REG_MULTI_SZ (reg.exe returned ' +
      IntToStr(ResultCode) + '). Without it the SCM will not launch the agent at all.');

  { ---- The per-user session helper ----
    A logon-triggered scheduled task rather than a second Windows service. The helper has to
    run inside each interactive session: SendInput is bound to the caller's session and is
    subject to UIPI, and BitBlt has no desktop to read from anywhere else. A per-user service
    (sc create with an empty obj=) is the other way to express that, but the empty obj= is
    easily rejected outright by the SCM, and the rejection is silent from the user's point of
    view - the agent appears to install fine and remote control then never becomes
    available. A logon task is the documented mechanism for exactly this, needs no stored
    credentials, and starts once per interactive user. }
  RegisterSessionAgentTask;

  { ---- Start the agent ---- }
  ResultCode := ScExitCode('start ESIRemotePcAgent');
  if ResultCode <> 0 then
    Log('WARNING: sc start returned ' + IntToStr(ResultCode) + '.');

  WaitForAgentServing;
end;

{ A rule scoped to the private profile does nothing at all on a network Windows has classified
  as Public, and plenty of home routers never advertise themselves, so Windows guesses Public.
  What the user sees is a phone timing out against a PC that is right there and whose log says
  it is listening - which reads as the product being broken rather than as a network setting.
  Say it out loud at the moment the rule is added, and say what to do about it. }
procedure WarnIfNetworkIsPublic;
var
  ResultCode: Integer;
  ReportFile: String;
  Lines: TArrayOfString;
  Index: Integer;
  Contents: String;
begin
  ReportFile := ExpandConstant('{tmp}\esi-netprofile.txt');
  Exec(ExpandConstant('{sys}\cmd.exe'),
    '/c netsh advfirewall show currentprofile > "' + ReportFile + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if not FileExists(ReportFile) then Exit;
  if not LoadStringsFromFile(ReportFile, Lines) then Exit;
  DeleteFile(ReportFile);

  for Index := 0 to GetArrayLength(Lines) - 1 do
    Contents := Contents + Lines[Index];

  if Pos('Public', Contents) = 0 then Exit;

  MsgBox('This network is set to Public, so Windows will block the agent''s port and a phone ' +
    'on this same Wi-Fi cannot reach this PC - it will sit timing out even though the agent ' +
    'is running and listening.' + #13#10 + #13#10 +
    'If this is your own network, set it to Private:' + #13#10 + #13#10 +
    '  Settings > Network and internet > Wi-Fi > your network >' +
    '''Private network''' + #13#10 + '  (on Windows 10 this is under Properties)' + #13#10 + #13#10 +
    'The firewall rule was deliberately not opened on the Public profile, because that would ' +
    'expose the agent on any network the PC is plugged into - a coffee shop, an airport.',
    mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Missing: String;
begin
  if CurStep = ssInstall then
  begin
    InstallProblems := '';

    { ssInstall is the step just before files are copied, which is the last moment anything
      can still be stopped without its files being locked. Both of these are running
      processes that hold the files about to be written; see StopAgentService and
      StopSessionAgent for what goes wrong if they are not. }
    StopAgentService;
    StopSessionAgent;
  end;

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
      Missing := Missing + 'ESIRemotePcAgent' + #13#10
    else if not ServiceIsRunning('ESIRemotePcAgent') then
    { Registered is not running. This gap is why a registration the SCM cannot start still
      installed cleanly and reported success for months: the check below only ever asked
      whether the key existed. A service that is present and stopped is the exact state the
      product was in after every boot. }
      Missing := Missing + 'ESIRemotePcAgent (registered, but not running)' + #13#10;
    if not TaskIsRegistered('ESI Remote PC Session Agent') then
      Missing := Missing + 'logon task: ESI Remote PC Session Agent' + #13#10;

    if Missing <> '' then
    begin
      Log('WARNING: these services were not registered:' + #13#10 + Missing);
    end
    else
    begin
      Log('Verified: the agent service is running and the logon task is registered.');
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
      { Delete first, then add. `netsh advfirewall firewall add rule` has no notion of
        "replace", so installing over an existing copy added a second rule with the same name
        every time - this machine had accumulated three of each, and the list a user sees in
        Windows Defender Firewall was six entries long for two ports. Uninstall deletes by
        name, so a clean install followed by reinstall was fine; an upgrade was not. }
      Exec(ExpandConstant('{sys}\netsh.exe'),
        'advfirewall firewall delete rule name="ESI Remote PC (LAN)"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(ExpandConstant('{sys}\netsh.exe'),
        'advfirewall firewall delete rule name="ESI Remote PC discovery (LAN)"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

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

      WarnIfNetworkIsPublic;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Before the files are removed, not in [UninstallRun] which runs afterwards: a running
      agent and a running helper hold the very files being deleted, and the directory is
      then left behind - a complete application tree, on a machine where the user has
      uninstalled the product. }
    StopAgentService;
    StopSessionAgent;

    Exec(ExpandConstant('{sys}\netsh.exe'),
      'advfirewall firewall delete rule name="ESI Remote PC (LAN)"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\netsh.exe'),
      'advfirewall firewall delete rule name="ESI Remote PC discovery (LAN)"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
