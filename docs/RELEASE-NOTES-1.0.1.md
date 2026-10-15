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

**What is not claimed.** This has been verified by tests that pin the wire format to the PC
encoder's own constants, and by building the native player into the APK — but not by listening
to it. The emulator on the machine that built this will not boot, so the end-to-end path has
not been heard on a real device. That is why this is a prerelease rather than the 1.0.1 tag.

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

- 429 C# tests, 309 Dart tests, all passing
- `flutter analyze` clean
- APK passes all nine installability checks: well-formed, every advertised ABI real, both
  32-bit libraries handled, signed with APK Signature Scheme v2 and v3, reported intact by
  `apksigner`, minSdkVersion 24
- Continuous integration runs the C# build and tests on Windows and the Flutter analyze, test
  and release-APK build on every push — see `.github/workflows/ci.yml`
- The audio wire format is pinned to the PC encoder's constants by `test/audio/audio_wire_test.dart`,
  and the native player is confirmed present in the built APK's dex
- The usage graph's whole path — PC history, snapshot, accumulated window, three channels,
  painter — is measured end to end by `test/state/metrics_history_test.dart`
- Full screen is measured by `test/ui/fullscreen_test.dart`, including which widget answers a
  tap that lands on the status bar

Both files, their sizes and their SHA-256 hashes are in `release/README.md`, which is the
authoritative record of what is in this delivery.
