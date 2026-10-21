# v1.1.1 — fixed stats and identity reporting

> Quick list: four bugs were measured, fixed, and tested. Two of them
> showed wrong numbers on the dashboard; two broke the app's
> pairing/stream behavior.

## PC fixes (Windows installer)

1. **Memory "Committed" is now real (D97).** The committed / commit-limit
   fields were sent as a *count of pages* instead of bytes — on this
   machine ~25 GB of real commit read as "6.3 MB", and the ~27 GB limit
   read as "6.6 MB". Both are now multiplied by the page size (4 KB).
2. **The "Page file" row is no longer always zero (D98).** The wire's
   `swap` object was never filled in, so a machine with a 10.8 GB page
   file and 631 MB of live usage read "Page file 0 B". It now reads and
   sums `Win32_PageFileUsage` (through the cached WMI client).

## Android fixes (APK)

3. **The PC's pairing prompt no longer reports an old version (D93).**
   The pairing flow kept its own copy of the app version string
   ("1.0.1") and the 1.1.0 release never updated it, so the PC
   introduced the phone as "app 1.0.1" while running 1.1.0. Everything
   now reads one shared constant (`esiAppVersion`), pinned in tests.
4. **Per-frame stream logging is gone in release (D94).** A healthy
   screen stream logged a line per frame (~900 lines/min). It now logs
   only when no frames arrive or a tile fails to decode — the two cases
   the counter was built for.
5. **Core labels no longer shuffle when two cores share a load (D96).**
   Each bar's label was found with `indexOf(value)`, so two equally
   loaded cores both matched the first one and a row could read
   "0 1 0 3 4 1 6 7". Labels now come from the bar's own index.

## Tests

- C# suite: 474/474 green.
- Dart suite: 335/335 green (new pins: version const, core labels).
- APK installability checks: 9/9.
- On a real device: fresh pairing on the release build succeeded, the
  dashboard opened on the first tap, and pairing survived a full app
  restart (D92 did not reproduce; it still gets a real-phone pass).

## Downloads

- Windows: `ESI-RemotePC-Setup-1.1.1-x64.exe`
- Android: `ESI-RemotePC-1.1.1.apk`

## SHA-256

```
ESI-RemotePC-1.1.1.apk           72ac6202666196b4f4d81a3e010e94fafe6beca0dd398f41405b209e534211a7
ESI-RemotePC-Setup-1.1.1-x64.exe 91e3ead0acf524b3bb751dca47e680c889b10c8288571caf280c79081e8e7ba2
```
