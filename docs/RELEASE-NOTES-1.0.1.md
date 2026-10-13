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

## Navigation

**Control did not sit at the foot of the left rail.** The rail chose between two layouts from
a height it predicted before the buttons were measured, and the buttons carry a *minimum*
height with text inside. When the prediction was wrong in one direction Control was pushed
past the bottom edge; in the other the fallback layout had no gap at all and left it sitting
directly under Screen. The threshold is gone: the four peers scroll inside an `Expanded` and
Control sits outside that region, so it is at the foot of a tall window and a short one alike.
A test renders the rail at six heights and four font sizes and measures where the box lands.
*(D59, D55)*

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

---

## Not in this release

**The phone-as-speaker audio is not finished.** The PC side captures and encodes it, but the
phone has no player yet. The transport, the framing and the µ-law codec are in place and the
volume work above is a prerequisite for it, but there is nothing to listen through. It is
deliberately not advertised.

**The agent's settings, device list and audit trail need a scope that is never granted.**
Those endpoints require `Admin`, and nothing grants `Admin` to a paired device. They return
403. This is unchanged from 1.0.0 and is called out here only so it is not mistaken for a new
regression.

## Verification

- 299 C# tests, 267 Dart tests, all passing
- `flutter analyze` clean
- APK passes all nine installability checks: well-formed, every advertised ABI real, both
  32-bit libraries handled, signed with APK Signature Scheme v2 and v3, reported intact by
  `apksigner`, minSdkVersion 24
- Continuous integration runs the C# build and tests on Windows and the Flutter analyze, test
  and release-APK build on every push — see `.github/workflows/ci.yml`

Both files, their sizes and their SHA-256 hashes are in `release/README.md`, which is the
authoritative record of what is in this delivery.
