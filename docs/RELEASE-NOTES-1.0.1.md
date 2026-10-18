# ESI Remote PC 1.0.1 — release notes

Everything below was a bug someone could see. Each entry says what it looked like from the
phone, because that is the only description a user has when they report it; the commit that
fixed it is named at the end of each one so it can be followed back to the reasoning and to
the measurement that proved it.

Nothing here is a refactor, a tidy-up, or a performance change. If a fix was only internal,
it is not in this list.

---

## It would not install

**The APK could not be installed on any 32-bit phone.** A previous build left `armeabi-v7a`
partly present — a folder that advertised the architecture without carrying the engine that
made it work. Google Play and the package manager both saw a phone it claimed to support, and
that phone could not install it. The whole 32-bit ABI is removed now, so a 32-bit phone is
told plainly that it is not supported rather than being handed something that fails halfway
through installing. *(D52)*

## Pairing

**The certificate pin was not on the screen that told you to read it from.** The app pointed
you at one place and printed the pin somewhere else, so the one thing you were told to copy
was not where you were told to look. The pin is now read from this phone's own saved records
of the PC it has already paired with — never fetched over the network, because a pin that
arrives over the connection it is supposed to authenticate is not a pin. *(D53, D48)*

**And a paired PC could never be removed from the phone.** A pairing is stored on both ends and
one of them is a phone where the store grows forever. Row two of the PC list now carries a
forget action — the same confirm-first shape every destructive control in this app takes — and
forgetting the PC this phone is on clears the active device too, because a phone pointed at a
PC it no longer remembers must not keep pretending to be pointed at one. The PC side of the
pair is untouched; a pairing only dies when its other end is told to forget it as well. *(D84)*

**And the QR scanner said nothing no matter what it saw.** Every decision the camera screen
made was a silent `continue`: a code that decoded but did not parse left the camera open with
no message anywhere, a decode that produced no text said the same thing nothing says, and a
camera that could not open (a permission refusal, another app holding the device) drew a black
box one line of package-default white text with no next step. All of that is one screen from
the user's side — "nothing happens" — and none of it said which. All branches speak now: a
code that is not a pairing code reads *That code decoded fine, but it is not a pairing code*
(and it is counted, so a second refusal is a new sentence, not the old one unconfirmed); a
decode that comes out empty says so and asks for closer, better light; twenty seconds of true
silence — nothing read at all — has its own line; and the camera fault screen is the app's own
words with a permission refusals' one real instruction (grant it in the settings, then Try
again, which rebuilds the controller because a transient camera fault often clears on a fresh
open). The deciding call itself is a pinned pure function now — three branches, a test each —
so a future editor cannot re-argue one of them into silence. And the pairing panel's copy no
longer promises what D39 removed: the QR has carried no one-time secret since the phone
started inventing its own, and the old text pointing you at that secret on the prompt's QR was
sending people hunting for a thing that is not there. *(D88)*

## The screen

**The screen stream was completely black, and reported that it had worked.** Two separate
causes, both of which succeeded silently:

- The capture wrote into a bitmap that GDI will not draw into on this machine. BitBlt
  returned `TRUE`, set no error, and produced a black rectangle. Capturing into a DIB section
  returns the same `TRUE` and a real picture. Both were measured in one process, at the same
  moment, from the same device context: mean brightness 0.00 against 25.93. *(D49)*
- The capture provider deleted its own cached bitmap on every single frame, so it spent the
  whole session allocating a surface and never reused one. *(D46)*

**Downscaled capture produced a sheared crop instead of a smaller picture.** On a display
wider than 2560 pixels the frame is resized, and the resize was a byte copy sized to the
destination rather than an actual resize — so it never faulted and always reported a good
frame, while filling the image with successive chunks of its first rows. It also allocated a
full-resolution buffer per frame: 33 MB at 4K, every frame. *(D60)*

**The socket gave up permanently and the screen never said why.** A dropped connection was
treated as final, so after one network hiccup the stream stayed dead until you restarted the
app, with no message to explain it. *(D47)*

## Mouse and keyboard

**The remote mouse jumped.** Two separate causes, both of which put the cursor somewhere it
had not been asked to go. *(D50)*

**Input did nothing at all.** The phone could not put a required field on the wire, so every
input event reached the PC missing what it needed to be acted on. *(D51)*

**Keyboard modifiers were pressed and never released.** Each key carried its modifiers down
and none carried them up, so one Ctrl+C left the machine holding Ctrl — and the next
keystroke became a shortcut, with nothing on the phone able to undo it. Shift stayed down and
typed capitals. The release is sent now, in the reverse order of the press. *(D60)*

**The test tap held the right button down forever.** The Screen panel's "test the tap" control
sent the PC a right-button *press* and no release — the phone-side click test called
`button('right', true, …)` with no matching `up`. The PC arrived at its own default for a stuck
button: the cursor began a right-drag wherever it happened to be, and no context menu ever
opened, because Windows opens a context menu on a completed click, not on a held one. So the
control whose one job is proving the touch link works left every PC it touched holding a button
nobody would release until something else on the phone sent a right-click-shaped message.

Two halves were wrong. The phone's test now sends one complete click — move, down, up — because
a click is three messages and the test is the only one of the three the phone never measured.
And the session agent's input pump, which already released *keys* a dying session left held,
had a separate table for them and none for *buttons*: a session that ended between a down and
its up left every held mouse button held. Held buttons now ride the same cleanup as held keys —
one close-out, released in order, on session stop. *(D80)*

The typing panel now says one true line where it used to say nothing: whatever the PC has
focus on is where typed keys go. It does not focus a window for you — Windows decides that, and
a panel claiming it steers the PC would be the D80 lesson again — it just stops the "it's not
working" report whose answer is that Excel, not the desktop, is the window on top.

## Volume and audio

**The volume slider hung, then jumped to a wildly wrong value.** The phone asked the PC to
walk to a percentage by pressing the volume key that many times. Each press does not move one
percent — the endpoint reports 51 steps, so a press moves about 1.96% — and the walk pressed
as many times as there were *points* to travel, moving roughly twice what was asked: from 100
down to 10 when 55 was requested. The phone then read the volume back while the walk was
still running and showed 82 for a PC that had landed on 10. Volume is now set in one write to
the audio endpoint and read back, with the key walk kept only as a fallback for hardware that
refuses. *(D54–D58)*

**Mute reported the wrong state.** The mute key is a toggle, and a toggle cannot be used to
write a state — reading the flag and pressing once would leave it the opposite of what was
asked. Mute is now written directly. *(D58)*

**The volume slider moved one output and not the one you were hearing.** It changed *one*
device's volume and never noticed when that stopped being the device you were listening to.
Plug in a Bluetooth headset and Windows makes it the default — but the phone's slider kept
writing to the speakers it had found when it first connected, so you moved the speakers and
heard nothing happen. The volume *keys* were never wrong, which is why this looked like a
Bluetooth problem rather than a stale-handle one.

The endpoint now records which device it opened and re-opens the default whenever that changes.
The failure is latched only on a first attempt, so one unplugged cable cannot leave volume dead
until the PC is restarted. *(D71)*

## This phone as the PC's speaker

**New in this build, and shipped as a prerelease because it has not been heard on hardware.**
The PC's own sound can now be played on the phone. A switch on the Control screen turns it on;
the PC captures its default output device, and the phone plays it back at 24 kHz stereo.

Four things were stopping it, and none of them was the codec — that half was already written
and pinned to a shared fixture.

The pump never sent a single packet. It waits on a semaphore and then reads a flag with an
exchange that clears it, but the flag was only ever set to *zero*, so the exchange always said
"nothing to do" and the loop skipped the block that actually sends. Audio piled up in the
capture queue until it hit its cap and then dropped everything after — every log line said
audio was running, and the wire carried nothing. *(D63)*

Nothing could ask for it. The service had the methods to start and stop the loopback capture,
audited and ready, and no WebSocket frame type ever reached them. The phone had no way to
express the request. *(D63)*

Audio frames were routed to whoever was watching the *screen*, so they were dropped whenever
the picture was off, and would have been pushed to a device that only wanted the picture. They
are now routed by frame type. *(D63)*

And the phone could not recognise an audio frame at all: the shared 32-byte header carries the
audio format in the fields that hold picture dimensions, and the app only knew the picture
ones. *(D63)*

**And the phone never heard the PC say no.** The PC answers `audio.start` with an `audio.state`
frame saying whether it accepted — and nothing on the phone was listening for it. The switch
reported success the moment the *phone's* track opened, and the phone's track opens whether or
not the PC is capturing anything. So a PC that had refused — no session agent running, another
phone already holding the audio — played silence under a control that read **on**, which from
the phone is indistinguishable from a broken codec. It now waits for the answer, and if the PC
declines it says so and leaves the switch off. The listener is attached *before* the request is
written, because a reply can arrive on the next turn of the event loop and a listener attached
after the send would miss it and wait forever for a confirmation that had already been given.
*(D72)*

**And a stop's answer in flight was taken as the start's.** Found by re-reading that fix: the PC
answers `audio.stop` on the **same `audio.state` type**, with `active: false`, and the listener
above filtered on type alone. So tapping Stop and then Play quickly enough — which the label
changing to "Play the PC here" the instant Stop is pressed actively invites — left the stop's
answer still travelling when the start attached its listener, and `.first` took it as the answer
to the start. The phone reported *"The PC refused to capture its audio"* about a start the PC had
in fact just accepted, and left that capture running for a phone which, having been told the
opposite, now refuses to play. Both halves of that are false, and neither shows up anywhere else.

The request id was on the wire the whole time and the PC **echoes it back** on every answer; the
phone was generating it, sending it, and then throwing it away. The listener mints the id *before*
it subscribes — it cannot recognise an answer it has no id to match against — and `startAudio`
takes it so it goes out on the wire. A reply naming a *different* request is somebody else's
answer and is passed by; a reply naming **no** id is still taken, because an agent that does not
echo one must not lose the refusal it is trying to report. Only a reply that positively names
another request is disqualified. *(D76)*

There is no longer an unqualified "every `audio.state`" getter beside the scoped one. It is the
thing being protected against, and a getter nobody is supposed to call is only still sitting
there to be called.

**And the switch kept saying "on" over a connection that had died.** The first overnight session
on a real phone (October 16) settled what actually happens: the phone asked the PC to capture,
the PC captured — 411 real packets, eight seconds of its own output device — and then the phone
put its screen to sleep. The socket drops when the phone dozes, the PC's disconnect handler stops
the capture on its side, and the phone's track keeps waiting on the socket's stream for real
frames that nobody is sending any more. Under the old state machine all of that left the switch
reading **on**, with the meter silent and the reason for the silence invisible — the same picture
whether the PC was playing something or nothing at all, which is precisely what made the
afternoon's "why don't I hear anything" report unanswerable from the phone.

The switch now tells the truth in that state. A connection dropping mid-play (or mid-start) is
not treated as a decision: the panel moves to **reconnecting** and says so — "the PC connection
dropped, so the PC stopped sending its sound. Waiting for it to come back." When the socket
returns, a speaker that was playing re-requests the capture by itself, without a tap, because
the drop was nobody's choice and the re-request is the same request continuing. Switching the
active PC revokes that promise — the re-request belonged to the previous machine's connection —
and leaves the switch off for the new one. The waiting state is itself tappable: tapping it
cancels the wait rather than tapping a switch that claims to be playing. *(D81)*

An answer that comes back after yet another drop, or after a stop, or after the PC changed, is
dropped unread rather than read as a refusal — a refusal of a request nobody owns any more, read
literally, stops the one state that still has somewhere to go.

**And the panel did not measure anything, so "silence" had no owner.** The new panel now counts
what it hears: packets arriving, packets dropped, how long since the last one. An incoming
stream of packets whose samples all sit under the smallest audible threshold for over half a
second is not a broken link — it is the PC capturing its own quiet output device, and the panel
now says exactly that: *"the PC is not playing anything"* with the instruction that was missing
from the afternoon session — sound has to be played on the *PC* for this to have anything to
carry. A count that stops climbing says the stream itself has stopped. Both lines appear above
the same numbers that used to be invisible, and both are measured on the phone, in numbers it
received, rather than inferred from states two machines away. *(D82)*

**And the track that was opened was never fed a single byte.** The October 17 session finally
named the remaining silence by subtraction. The PC's loopback capture was measured directly on
the machine that reported hearing nothing — a 440 Hz probe tone played through the default
output while the exact WASAPI loopback configuration `AudioPump` uses captured it at a peak of
8 704 of 32 768 — so the PC half captures real signal and forwards it (2 281 frames reached the
phone's socket; the meter counted them; the track reported open). What remained was the phone's
own playback, and the defect was a wire contract read differently at each end. Dart sends the
PCM buffer as the *arguments themselves* of the `write` call — pinned by
`test/audio/audio_wire_test.dart` — while the MainActivity host looked the bytes up as
`call.argument<ByteArray>("data")`, a lookup that requires the arguments to be a Map and throws
ClassCastException on a bare array. Every one of those 2 281 writes died before its bytes
reached the queue, and the exception surfaced nowhere: the write futures are not awaited on the
Dart side, so there was no error and no log line reaching the user — an open track, a climbing
packet meter, and total silence. The host now normalises the argument: a bare ByteArray is
accepted as the payload (the shape that ships), and a Map spelling is accepted beside it for
symmetry with `start`. *(D86)*

**And the room still had two mouths on it.** Once the phone is the PC's speaker, the PC's own
speakers keep talking — the confirmation of the feature ("I hear the PC through the phone")
arrived together with its first defect report ("so why is the PC still playing?"). The request to
close the room's other mouth is now explicit, and it lives where the thing it governs lives: a
**"Silence the PC's own speakers"** switch under the speaker button, on by default — default
because the phone-as-speaker reading is the feature the way a wireless one is. While the stream
runs, the session agent mutes the PC's default output endpoint, and lifts the mute on every path
that closes the capture: a stop, the panel switch, the socket dropping, the session agent
exiting.

The answer itself is a preference, not a state: it rides the phone's own protected storage, is
read at the moment of every request (so nothing can fork from a cached copy, and an
auto-resumed stream after a dropped socket re-asks it exactly as it re-asks the PC), and
survives a reinstall the way the pairing does. Flipping it mid-stream closes the capture and
re-opens it — around three hundred milliseconds of dip the meter shows — because the PC reads
the flag only when the capture opens.

The mechanism was measured before it was trusted, on the machine that asked for the switch: a
440 Hz probe tone through the default output, tapped by the exact WASAPI loopback setup the
pump uses, at a peak of **5 143 muted against 5 144 unmuted** — so on this machine the endpoint
mute takes effect *after* the loopback tap and silences the room without silencing the stream.
That is a measured fact about one device, not a law of audio: a driver that applies volume
inside the audio engine mutes both the room and the tap. Nothing hides that case — the phone's
silence meter says "the PC is capturing correctly but not playing anything right now", which is
exactly what it looks like — and the switch is one flip away.

The edge that actually mattered is a run dying between mute and unmute. A ledger in the
registry (`HKCU\…\RemotePc`, value `SessionLocalOutputSilenced`) is written only after our mute
lands on the endpoint — a mute the endpoint refuses leaves no trace at all — and the session
agent, at every startup, reads the ledger and lifts upright whatever a dead run left tuned over.
One silence the agent never touches is already established: an endpoint the *user* muted
themselves is engaged into zero times, its ledger untouched, and released into nothing — the
user's speakers obey the user, and the agent claims only the mute it actually took.

Pinned the same way as everything else: 9 C# tests hold the silencer's contract against fakes —
engaging, releasing, double-release, refusal leaving no ledger trace, a failed-unmute surviving
for the next run, recovery lifting only what the ledger names — and the phone pins the wire
shape: `silenceLocal` rides `audio.start` at the top level, and a toggle that is off sends the
flag's *absence*, byte-for-byte the frame an older phone sent. *(D87)*

**What is not claimed.** Verified in the same way as before — wire format pinned to the PC
encoder's constants, the native player present in the built APK — plus what earlier sessions on
two real phones established mechanically: `audio.start` reaches the PC and the PC captures and
streams its output device (411 packets in eight seconds on the first phone), the phone plays and
stops without crashing, and the disconnect path ends the capture on the PC side. And, since the
October 17 session, the one thing this feature exists for is verified the only way it can be:
the PC's sound *heard* on the user's phone while the PC plays, on the user's own hardware
through their own ears. What remains device-dependent is named rather than hidden: a driver
that applies volume inside the audio engine silences the loopback tap together with the room
(measured case above, D87), and that case announces itself as the meter's silence reading — not
as a question nobody can answer.

**The screen could not be shown full screen.** On a phone the stream was one column of five
inside a panel — about a hundred pixels tall, with the rest of the display given over to chrome
nobody was looking at. Nothing on a remote desktop is readable at that size, so reading a PC
from a phone was not really possible; the picture was there, and it was a fifth of the screen.
There is now a control on the Stream panel that hands the whole phone to the desktop: the bars
at the top and bottom are hidden rather than made transparent, because a transparent bar still
covers the top of the picture, and the phone turns to match a desktop that is landscape — a
16:9 picture on an upright phone is a band with black above and below it, which is not the full
screen that was asked for. Whatever leaves the mode — the button, the back gesture, the back
button — the phone is handed back: orientation restored, bars restored.

The one row of controls that has to sit over the picture is skipped during hit testing, because
every pixel of that view is a pixel of the PC, and a status line quietly taking the first click
of every session is worse than no status line. A test lays the view out in landscape, where the
bar genuinely overlaps the desktop, and checks which widget answers the tap. *(D64)*

## Running applications

**The Apps screen said no processes were running, on a machine running hundreds.** The agent
answered `/system/processes` and `/devices` with a bare JSON array. The app reads
`json['processes']` — and the helper it uses to read an object returns an *empty* object rather
than an error when it is handed an array, so the request never failed, it simply always
produced an empty list, and the screen said so. The agent now wraps both responses the way
every other one already was, and the app-side helper refuses a JSON array instead of quietly
pretending it was `{}`.

The four screens that read those lists also swallowed the failure: a request that failed came
back as an empty list, which is the same picture as a machine with nothing to report. They now
say what happened. *(D65)*

**And the CPU column was always zero.** The agent never sent `cpuPercent` at all — the field
exists in the contract and nothing filled it in. The phone parsed the absent value as `0`, so
every process on a machine running them flat out read `0.0% CPU`, the bar beside it stayed
empty, and sorting by CPU did nothing because every value was equal. A usage column that always
reads zero is worse than no usage column: it reads as an answer instead of as a missing one.

CPU per process is a *rate* — the change in a process's own processor time over the change in
wall time — so one listing cannot produce one, and the first listing of a process legitimately
carries no figure. It now carries **no figure** rather than a zero, and the app shows
"measuring…" instead of claiming idleness. The second listing has real numbers, because by then
there is a previous observation to subtract from. *(D70)*

**And two listings at once destroyed each other's readings.** `cpuPercent` is a difference
between two samples, so the sampler holds a small table of previous ones — and it read the new
sample *before* taking the lock the table lives behind, then compared it against what another
thread had written *inside* that lock. `/system/processes` and `/system/apps/running` walk the same
singleton, so two requests can overlap. Whichever walk reached a pid first stored the fresher
numbers; the walk behind then measured its own older numbers against them and found time running
backwards and a processor total that had gone down — so it reported no reading, and, worse,
overwrote the good sample it had just been handed with the stale one of its own. The reading and
the comparison now happen in the same critical section, where the clock can only have moved
forward and a process's own processor time can only have risen. *(D75)*

**What is also true, and not a defect:** a listing that follows another within a single Windows
tick — about 15.6 ms — has no interval to divide by, and every process correctly reports no
reading yet. Two requests fired back to back can do exactly that. It is why the phone's column
says "measuring…" rather than inventing a number, and why the test that pins this now waits a
fifth of a second between its two listings instead of asserting that the clock is faster than it
is. *(D75)*

**The sort chips did nothing.** `CPU`, `Memory` and `Name` were drawn as status tags, not
buttons: there was no tap handler on any of them, and the highlighted one was hard-coded rather
than derived from anything. They reorder the list now. *(D70)*

**Every timestamp the PC sent was 1,970 years out.** A process's start time arrived as
`started in 17259887 h` — and that figure is not a random overflow, it is the distance between
the two epochs: 62,135,596,800 seconds. Seven places on the agent divided .NET ticks by
`TicksPerSecond`, which counts from 0001-01-01, while the wire has always said Unix epoch and
every other timestamp in the agent — the power state, the boot time, the audit trail, every
WebSocket frame — already used it. The wrong number is a positive `long` that deserialises
happily, so nothing failed; it was simply not a timestamp. Fixed in one place that the rest now
call, and pinned by asking the running agent for a real timestamp and comparing it to the clock
it was built with — reading the arithmetic three times over does not say which epoch is meant.
The file screen's dates and the certificate's validity window carried the same defect and are
fixed with it. *(D69)*

## The dashboard's usage graph

**The graph was there, and said nothing about why it was blank.** The Load panel's trace always
paints its grid and its CPU/GPU/RAM legend, so a trace holding no samples looks exactly like a
trace whose lines are missing — a frame, three labels, and nothing between them. It now says
which one it is: "No load data yet" when nothing has arrived, and "Taking the first readings"
when only one has, which is the state a person actually meets in the second after connecting
and the one that most looks like a broken graph.

The chain that draws it — the PC's own history, the snapshot that carries it, the window the
phone accumulates, the three channels and the painter that needs two points — had no tests at
all. Every link was individually correct and the order they run in had never been measured.
*(D66)*

**And the GPU channel read zero on every Windows 10 2004+ machine, because of one string
assumption.** The per-engine aggregate groups the `\GPU Engine(*)\Utilization Percentage`
instances by the adapter's LUID, extracted from names the documentation spells
`luid_0x00000000_0x0000ABCD_engtype_3D` — leading with the adapter. Windows 10 2004+ leads with
the owning process instead: measured directly on this machine's counters, all 541 engine rows
are spelled `pid_10864_luid_0x00000000_0x00012997_phys_0_eng_0_engtype_3D`, and the extractor
that required the name to *start* with `luid_` rejected every one of them. The aggregate came
back an empty list, GPU load read zero, and the phone's GPU channel had nothing to draw —
forever, on a healthy machine with a working counter. The probe that decided whether the
counter existed at all leaned on the same family of evidence and lied the same way: the
wildcard *expand* reported one instance where the same counter's read enumerates 541.
Availability is now the add call itself — `PdhAddEnglishCounter` succeeds exactly where the
counter object exists, in any locale — and the extractor finds the `luid_` marker wherever it
stands, so both spellings, and the adapter-memory rows that the memory pairing keys by, land on
one adapter key. Measured after the fix on the same machine: two adapters aggregate, the
primary reading 14.79 % while a window moved. *(D85)*

**And one live read died the instant it was subscribed.** The PC's hub answers a
`gaming.watch` request with a `gaming` frame whose data field is **null** — a receipt for the
request, not a reading. The phone's typed tap on that stream read everything of that type as
state, handed `null` to the parser as an empty map, and the parser turns every missing number
into a zero: every time the gaming screen opened its subscription, a false **GPU 0 %** /
CPU 0 % / ping 0 ms landed on whatever real numbers were already there, and a real gaming
frame arriving later overwrote it while nothing pushed the true state down meanwhile. The tap
now drops a data-less `gaming` frame as what it is — an ack, not a state — and the pin holds
both halves: a null receipt lands as nothing, and a state-bearing frame still lands. *(D89)*

## Navigation

**The left rail is gone; the buttons are a bar across the foot of the phone.** The rail bought
five labels their vertical room by giving up a strip of the phone's width on *every* screen —
74 logical pixels on a 360-wide phone, a fifth of the reading area, on the tab whose job is the
readings — and it sat on the left edge, which is the part of the phone a thumb has to travel to.
The destinations are now five equal slots across the bottom, where the hand already is, and
every screen gets its full width back.

Control used to be pinned to the foot of the rail, which bought it a statement about "the other
thing" in exchange for a large dead gap between it and its four peers. In a horizontal bar the
peers are contiguous by construction, so Control simply sits last and the gap costs nothing.

A test measures the bar itself — not a reconstruction of it — at six window heights and four
font sizes: five destinations on one row, contiguous, in order, and labelled; and the area
above it spanning the full width of a 360-pixel phone. *(D59, D55, D67)*

**The wordmark ran off the right edge on the first screen.** "ESI Remote PC" at 26px measures
about 330px, and a 360px phone gave the row 328. The title now takes the space that is
actually there and shrinks only as far as it has to. *(D60)*

**And once paired, there was exactly one road to the PC list: none.** The pairing screen is
reachable from the first-run state, and never again — a paired phone that wants to switch
machines, or forget one, has no control that gets there; the screen exists, the routing exists,
and the two were joined by nothing. Four entry points now, chosen for where the want actually
occurs: the machine name in the status bar — the one strip every screen carries — is tappable,
with a swap glyph saying the row does something; the Settings screen carries a "find or switch
PC" action beside it; and the pairing screen still opens itself for the phone that has no PC
at all. *(D83)*

## Files

**Rename and Delete in the Files screen did nothing, and said nothing.** Each menu item
popped its own sheet and then handed the action *the sheet's* context. The action awaits a
confirmation dialog, and by the time it returned, that context had been unmounted by the pop
that started it — so it returned before sending the request. You tapped Delete, confirmed
"Delete", and the file was still there with no message. The screen's context is passed now,
and the parameter is named so the two cannot be confused again. Move ran but dropped its
result the same way, and both "add to this folder" actions ran and never said whether they
had worked. *(D60)*

**Every upload of a file 8 MB or smaller was refused, and larger ones succeeded.** The agent's
`/files/upload` is authenticated by a single-use ticket, and the ticket is also the only place
the destination path is carried. The phone had a second path for files under the signed-body
cap that PUT the bytes straight there without asking for one, to "keep one code path for the
common case" — it produced two paths, and the smaller was the only one anyone would ever take.
The agent requires a ticket on that route *exactly*, so an ordinary photo came back `401` "An
upload ticket is required" while an 80 MB video went through. There is one path now, and it is
the one the endpoint accepts. A test drives both sides of the old 8 MiB boundary and fails
against the code as it shipped. *(D68)*

---

## The tray

**Exit did nothing.** The item was wired, the agent really did shut down, and then the tray icon
was back. The session agent is *supervised*: the service polls every three seconds and, whenever
a live session has no agent, starts one. A crash and a person choosing Exit arrive at the service
as the same event — a named pipe that stopped working — so the supervisor answered an Exit by
bringing the agent straight back. From the tray, an item that closes the process and immediately
reopens it is indistinguishable from an item wired to nothing.

The agent now says which of the two it is: it writes an `agent.stop` frame *before* the pipe goes,
and the supervisor holds that as a decision about this session rather than as damage. It is held
per session id and in memory, so the next sign-in, a different person signing in, or a restart of
the service each get a fresh start — none of which leaves a machine quietly unreachable. The menu
item says what it does, **"Exit until the next sign-in"**, because exiting is not for the session
and the old label did not say so. *(D74)*

The write is given two seconds and no more. It has to reach the pipe before the process does, but
waiting without a limit would turn an exit into a process that never leaves, which is a worse
version of the same defect.

**And the icon in the tray was not this product's.** It was `SystemIcons.Application` — the blank
page Windows draws for a program with no icon of its own — while the shortcuts to that very same
binary, three menus away on the same desktop, carried the product's mark. Two places disagreed
about what the user was looking at, and the one they looked at most was the wrong one.

The agent now reads its tray icon back out of its own executable, which carries the one image in
the repository: the same `release\esi.ico` the installer gives to its shortcuts. One file, so the
tray, Explorer and the Start menu cannot drift apart, and a test pins that the project file and the
installer still name the same one. *(D74)*

**Two ways an exit could still have looked broken**, both present after the two fixes above and
both caught while writing them: a `NotifyIcon` whose process ends while it is still visible leaves
a ghost icon behind that Windows keeps drawing until the mouse passes over it — so a working exit
left the mark sitting there — and `Main` ran the runtime without ever disposing it, so nothing hid
the icon on any path. The icon is taken off the screen before the message loop is asked to end, and
`Main` disposes the runtime. *(D74)*

## The status command

**It said the session helper was not installed, on machines that had it installed and running.**
The Start Menu ships an entry called **Agent status (console)**, and it exists for exactly one
question — is this actually installed and running. It answered with:

```
ESIRemotePcAgent         Running      Automatic
ESIRemotePcSession       not installed
```

which is the one screen whose entire job is to report reality, reporting something false. It is
also the first thing anybody is told to run when something looks wrong, so the false answer
arrives at the moment it does most damage: a person debugging a problem is told the component
they are about to debug is not there. It printed this while the helper was running, with its tray
icon, in the taskbar, three lines above a log file that shows the helper connecting.

The helper used to be a per-user Windows service. It cannot be one: a per-user service is
registered with an empty `obj=`, which the SCM rejects — `esirc task --help` says so in as many
words — so the helper became a **logon task** instead. The status line was never told, and kept
asking the SCM about a service that no longer exists. Every answer to that question is "no such
service", so it reached "not installed" on its own, about itself. *(D77)*

It now asks three questions in order: is there still a legacy per-user service, is the logon task
registered, and is the helper running.

```
ESIRemotePcAgent         Running      Automatic
ESIRemotePcSession       running (pid 14328)
```

The first question is not tidiness. `esirc install` still creates that service, and a machine
installed by it should be reported as a service — reporting *it* as absent would be the same
defect facing the other way, which is the half of the rule the old code got wrong: the absence of
that service says nothing at all about whether the helper is installed.

The existence check reads `schtasks`' **exit code** and never its text, because `schtasks` prints
that text in the operating system's own language. A report that is only right on an English
machine is a report that is wrong.

The decision is a pure function, internal and free of the SCM, `schtasks` and the process list —
the same way the tray's Exit rule was pinned above. Removing the one condition that made it ask
about the task at all, which *is* the old rule, makes three tests fail with `Actual: "not
installed"`, the defect exactly.

It was then checked on the machine that reported it: the installer from the previous prerelease
still prints `not installed`, the new CLI prints `running (pid 14328)`.

**What is not claimed:** the status line reports the helper as running by finding a process of
that name, which is evidence rather than a guarantee — a process could exist without the task
having started it. It is the same signal a person would use by eye, and a stronger one than the
SCM was giving.

**Two more paths through the installer were still walking the shape the status command
diagnosed.** `esirc install --include-session-agent` created the helper as a per-user service —
the same `obj=.` shape the SCM rejects, and that D77 migrated the installer away from. So the
CLI could produce exactly the machine the status command reports correctly but can never fix
from that state: a warning, and no working helper. And `esirc uninstall` removed the services
and left the logon task registered, so the helper — whose whole purpose is to be relaunched at
sign-in — survived an uninstall and came back at the next boot anyway. An uninstall that leaves
the thing running is the D74 exit defect wearing a different coat.

Both now go through the task path as their one path: install registers the helper as a logon
task, the same code the installer itself calls, and uninstall ends the running task first and
then deletes it. Both are quiet when there is nothing to remove — the second uninstall is
idempotent, not an error — and both were measured on the machine that reported them: install by
CLI, task present, helper relaunched at sign-in; uninstall by CLI, task gone, helper gone, a
second uninstall reporting nothing. *(D79)*

## The delivery document

**`release/README.md` had 124 mangled characters in it, and had had them since it was written.**
Every non-ASCII character in it was a UTF-8 sequence that had been read as Windows-1252 and
written back out as UTF-8, so one character had become three. The install instructions read
`Start menu â†' *Uninstall ESI Remote PC*`, and the verification section read `a real install â†'
serve â†' uninstall cycle`. `docs/ROADMAP.md` had the same thing twice over, inside a second
encoding layer. *(D78)*

Two things made the repair worth doing carefully rather than quickly. A plain "undo the double
encoding" pass fixes the ordinary runs and **silently drops** the ones whose trailing quote byte
had been normalised along the way — it turned `install → serve` into `install  serve`, an arrow
with nothing in it, which is a worse version of the same defect. And the one multiplication sign
in the roadmap reads, to the eye, like a stray letter in front of a dash; deleting that letter —
the obvious repair — yields "a fixed window gives a client 2- the budget" where the text means
*twice* the budget.

The whole tree was scanned, not just the two files; the only other hit was a compressed byte pair
inside a PNG. The repaired text was read back as English rather than merely checked to be free of
bad bytes, and both files kept their line counts, their CRLF endings and their lack of a BOM.

**This ships in the next prerelease, not this one.** The tag for this release points at the commit
before the fix, because the two binaries on it are byte-identical either way and moving a
published tag to fix a documentation file is a worse trade than leaving it. `master` is correct.

---

## The build

**The release script checked an APK that did not have the fixes in it.** `build.ps1` never builds
the APK — it finds one at the flutter output path and runs the nine installability checks over it,
and those check the package's *shape*: well-formed, every advertised ABI present and real, signed
v2/v3, minSdk 24. None of them can tell what code is inside, and `flutter analyze` and
`flutter test` being green says nothing about the package either.

It nearly shipped that way. The phone-side fixes in this release changed four Dart files, and the
script then reported nine green PASSes for a package built the previous morning containing none of
them. The installer carried the agent's half correctly, so the release would have looked complete
while the phone half was the build before it. The step now refuses an APK older than the newest
file under `lib/` and says what to run — this is the installer problem already fixed further down
the same file, arrived at from the other side. *(D73)*

---

## Not in this release

**Two fields on every process row are still empty.** `/system/processes` also carries `user` and
`isElevated`, and nothing fills either in — reading a process's owner needs an access token opened
against it, and the agent lists processes it is not privileged to open. So the `admin` tag on the
Running tab never appears and no username is shown. They are honest empties rather than wrong
answers, and they are unchanged from 1.0.0. Left alone deliberately: the CPU figure is what that
screen exists to show, and populating the other two needs a different privilege than the one the
service holds.

**An agent older than this release cannot play the PC's audio.** The loopback capture lives in the
per-user session agent, and the PC's own output device is only reachable from the interactive
session. An agent built before the audio transport existed has no `audio.start` frame type at all,
so the switch on the Control screen has nothing to talk to. This is the phone refusing to claim
success it did not get, not a new capability — installing the agent from this release is what
enables it.

**The agent's settings, device list and audit trail need a scope that is never granted.**
Those endpoints require `Admin`, and nothing grants `Admin` to a paired device. They return
403. This is unchanged from 1.0.0 and is called out here only so it is not mistaken for a new
regression.

**The MJPEG camera stream demands a signature its intended viewers cannot produce.**
`GET /camera/stream.mjpg` exists for OBS, VLC and ffmpeg, and both of this product's documents
say reading the stored frames is unauthenticated. `SignatureAuthenticationMiddleware.IsPublic`
does not list that path, so it has always required a per-request signature and no external
program has ever been able to open the stream. The conservative of the two readings is what
ships and what `ScopeTableTests` pins; making it genuinely public is a decision to take or
refuse, not a detail to correct quietly. The three camera routes were missing from
`PROTOCOL.md` entirely until now — they are §6.12.

**Three rows of the specification disagreed with the code, and the code was right each time.**
`PROTOCOL.md` §6.10 called `/notifications/rules` `admin` where the code and `SECURITY.md` §7
both say `read` (the app ships a rules editor every paired device can open); §6.6 called
`/files/roots` `read` where the file manager requires `files`, a discrepancy `SECURITY.md` had
already noted in passing; and §6.6 described `POST /files/upload` as taking a `path`, when the
route has only ever taken `?ticket=` — §7.4 of the same document said so. That last row is the
misunderstanding the broken branch in D68 was built on. All three are corrected, and the whole
scope table is now pinned by `ScopeTableTests`, so the next drift fails a build rather than a
handset.

## Verification

- 453 C# tests, 315 Dart tests, all passing
- `flutter analyze` clean
- APK passes all nine installability checks: well-formed, every advertised ABI real, both
  32-bit libraries handled, signed with APK Signature Scheme v2 and v3, reported intact by
  `apksigner`, minSdkVersion 24
- Continuous integration runs the C# build and tests on Windows and the Flutter analyze, test
  and release-APK build on every push — see `.github/workflows/ci.yml`
- The audio wire format is pinned to the PC encoder's constants by `test/audio/audio_wire_test.dart`,
  and the native player is confirmed present in the built APK's dex
- The speaker switch is measured end to end by `test/state/speaker_state_test.dart`, including the
  stop's answer left in flight under the start's — removing the id filter from D76 makes it fail
  with `Expected: on / Actual: failed`, the false refusal exactly
- The reconnection supervision of D81 is measured by five tests in `test/state/speaker_state_test.dart`:
  a live connection dropping mid-play moves the switch to *reconnecting* and names the reason; the
  socket returning resumes the play without a tap; switching the active PC ends the request instead
  of replaying it into the new one; an off speaker stays off when the socket cycles; and an answer
  arriving after a second drop is dropped unread — removing the supervision listener from
  `audio.dart` makes the first fail with `Expected: on`, the lie D81 closes
- The build-order guard itself was found by this round's audio work to misread Riverpod's
  parameterised listener closures — `(previous, next) { … }` — and to pop its scope stack on
  closes it had never pushed opens for. Both are fixed in `test/provider_build_order_test.dart`,
  and the guard now sees the same deference every other `ref.listen` body gets
- `esirc install --include-session-agent` creating the logon task, `esirc uninstall` removing it
  with the helper gone, and a second uninstall reporting nothing — measured on the machine that
  reported D79; the installer's cycle (29 checks) runs the same task path through
  install, reinstall, uninstall and reinstall
- The tray's Exit decision and the order it shuts the icon down in are pinned by ten tests in
  `SessionAgentStopTests.cs`, each run against the code as it shipped and failing there
- What `esirc status` reports about the session helper is pinned by eight tests in
  `SessionAgentStatusTests.cs`; removing the task check makes three of them fail with
  `Actual: "not installed"`
- The usage graph's whole path — PC history, snapshot, accumulated window, three channels,
  painter — is measured end to end by `test/state/metrics_history_test.dart`
- The GPU instance-name parser is pinned by six tests in `GpuInstanceNameTests.cs` against the
  two spellings the field actually writes — the process-leading shape measured on this machine
  (541 rows, all `pid_…_luid_…`) and the documented adapter-leading one — plus the
  adapter-memory row that must key identically for the memory pairing, the no-pair fallback,
  and a name without a LUID; the first test fails against the pre-D85 extractor with
  `Expected: luid_0x00000000_0x00012997 / Actual: <null>`
- The audio `write` contract is pinned on the Dart side by `test/audio/audio_wire_test.dart`
  (the arguments are the bytes themselves), and the MainActivity host now accepts both
  spellings — the ClassCastException the old host raised on every write is the D86 story above
- Full screen is measured by `test/ui/fullscreen_test.dart`, including which widget answers a
  tap that lands on the status bar

Both files, their sizes and their SHA-256 hashes are in `release/README.md`, which is the
authoritative record of what is in this delivery.
