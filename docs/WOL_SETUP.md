# ESI Remote PC — Wake-on-LAN Setup

Wake-on-LAN is the one feature where the honest answer is usually "it depends on hardware,
and the software can only tell you what Windows thinks". This document covers what has to
be true, how to check each part, and — most importantly — the Fast Startup caveat, which is
the single most common reason a correctly configured machine still does not wake.

---

## 1. The honest caveat: Fast Startup breaks "sleep"

**Read this before configuring anything.**

Windows Fast Startup (internally `hiberboot`) changes what a *shutdown* does. With it
enabled, "Shut down" is really a **hybrid shutdown**: Windows signs every user out and
closes the applications, then hibernates the kernel session to disk. The result is a
saved-session state, not a cold power-off.

That matters because of what a magic packet needs. A NIC in Wake-on-LAN state is held in
S5 (soft-off) — or in S3 on a machine that supports it — with enough power budget to run its
PHY, listen for the pattern, and assert PME to wake the rest of the system. A machine in
**S4**, the state a hybrid shutdown leaves behind, is not in that state. The network
interface is not armed the way the WoL specification requires, and the magic packet is not
seen at all.

The consequences:

| Scenario | With Fast Startup | Without Fast Startup |
|---|---|---|
| PC shut down, then magic packet sent | **Does not wake** | Wakes |
| PC restarted, then magic packet sent | **Does not wake** | Wakes |
| PC asleep (S3), then magic packet sent | Wakes | Wakes |
| PC in Modern Standby (S0 low power idling) | Usually does not — no S3 at all | Same |

This is why `powercfg /h off` is the first thing to run. It disables hibernation, which
removes Hybrid Boot along with it, and the machine goes back to a genuine S3 on Sleep and a
genuine S5 on Shut down. The cost is that the **Hibernate** power option disappears from
the Windows UI, and Fast Startup is gone with it. There is no way to keep hibernate and
have working shutdown-WoL.

The agent detects this and says so. `PowerStateProbe.IsHiberbootEnabled()` reads
`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power\HiberbootEnabled`, and
`GET /api/v1/power/wol/readiness` returns:

```json
{ "hiberbootEnabled": true,
  "issues": ["Fast Startup is enabled, so a Windows restart does not fully power-cycle the NIC and the PC will not wake from a restart. Disable it with: powercfg /h off"] }
```

That message names the *restart* case specifically, because the restart case is the one
people hit without noticing: Windows shows "Restarting…" and then the machine comes back
faster, which looks like a normal restart, but the machine is in S4 and will not answer a
magic packet afterwards.

### Turning it off

Requires administrator rights. From an elevated prompt:

```powershell
powercfg /h off
```

Confirm:

```powershell
powercfg /a
Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power" -Name HiberbootEnabled
```

`powercfg /a` should now list only the non-hibernate states, and `HiberbootEnabled` should
be absent or `0`. `powercfg /a` also reports the standby states, which tells you whether
you have a real S3 or only Modern Standby:

```
The following sleep states are available on this system:
    Standby (S3)
    Hibernate
    Hybrid Boot
```

`Standby (S0 Low Power Idle)` instead of `Standby (S3)` means this is a Modern Standby
machine. **WoL from sleep does not work there**, and no firmware or registry change will
make it. The only supported state to wake from on such a machine is full shutdown, and only
with Fast Startup off.

To turn it back on: `powercfg /h on`.

---

## 2. Firmware: the part software cannot fix

The NIC's ability to assert PME during S5 is a firmware and driver capability. Windows
exposes it as a driver checkbox; if the firmware does not support it, the checkbox either
does not exist or the setting has no effect.

### BIOS/UEFI

The setting is usually called one of **Wake on LAN**, **Wake on PME**, **Power On by
PCI-E/PCI**, **Deep Sleep** (inverted — *disable* it) or **ErP / EuP** (see below).

| Firmware setting | Where | What to do |
|---|---|---|
| Wake on LAN | Advanced / Onboard Devices, or Advanced / Wake Event | **Enabled** |
| Wake on PME / PCI Express | Advanced / Power Management | **Enabled** |
| Deep Sleep Control | Chipset / Advanced | **Disabled** — Deep Sleep cuts power to the NIC, so it cannot listen |
| ErP Ready / ErP / EuP | Chipset / Advanced / Power | **Disabled** on many boards. ErP cuts standby power and disables WoL entirely. It is a European power-consumption regime, not a sleep feature |
| S5 / Soft-Off Power Behavior | Advanced / ACPI | Not WoL-related on most boards, but some older ones gate PME here |
| Restore on AC Power Loss | Advanced | Irrelevant to WoL; do not confuse the two |

On a desktop with a discrete NIC, also confirm the NIC is not powered down when the
integrated one is in use — some boards disable the add-in card unless a specific
`PCIe Wake` or `ErP` option is set.

### Device Manager

For the wired adapter:

1. **Device Manager → Network adapters → *your Ethernet adapter* → Properties → Advanced**
2. **Wake on Magic Packet** → `Enabled`
3. **Shutdown Wake-On-Lan** → `Enabled` on Intel adapters, where it is a separate option
4. Apply, and reboot

Some drivers also expose **Wake on pattern match** and **Wake on Magic Packet from power
off state**. The last one is what makes shutdown-WoL work on an Intel NIC, and it is
disabled by default on many laptops.

If there is a **Power Management** tab, tick *Allow this device to wake the computer* and
*Only allow a magic packet to wake the computer*. The second box is worth ticking: without
it, any pattern match can wake the machine, which is both less predictable and a larger
attack surface on a shared network.

### Wi-Fi

**Wake-on-LAN does not work over Wi-Fi in any practical sense.** There are
`Wake on Wireless LAN` / WoWLAN drivers for a handful of Intel chipsets, they are
manufacturer-specific, they suspend rather than power off, and the machine still has to be
in a state that retains the radio.

The agent says this itself. `GET /api/v1/power/wol/readiness` adds this issue whenever no
adapter reports `connectionType == "ethernet"`:

> Wake-on-LAN only works over a wired Ethernet connection in practice. A Wi-Fi adapter cannot
> wake the machine.

Wi-Fi is a *good enough* wake mechanism in practice — a phone can send a TCP connection to
the port and the OS will bring the radio up — but that is a different mechanism from a
magic packet, it needs the machine to be in a radio-retaining state, and it is not what
`POST /api/v1/power/wake` does.

---

## 3. Windows: arm the device

Windows maintains its own per-device wake permission list, independent of the driver
checkbox. A device can be armed in the driver and disabled here, or the reverse.

### What Windows currently considers armed

```powershell
powercfg /devicequery wake_armed
```

Expected output is one adapter name per line:

```
HDA Intel PCH
Realtek PCIe GbE Family Controller
```

The agent runs exactly this command (`PowerStateProbe.GetWakeArmedAdapters`).

### Arming a device

The device name is the one from `powercfg /devicequery wake_armed`, not the friendly name
from `ipconfig` — they often differ, and that difference is the reason for a check in §4.

```powershell
powercfg /deviceenablewake "Realtek PCIe GbE Family Controller"
powercfg /devicedisablewake "HDA Intel PCH"
```

`PowerStateProbe.EnableWakeForDevice` is the same call, exposed to the agent, though no
endpoint currently triggers it — `GET /api/v1/power/wol/readiness` only reports.

Also worth setting, though the agent does not check it:

```powershell
powercfg /lastwake
```

After a successful wake this names the event that caused it. `Wake Source: ...` with a
network controller, or a `Power Button` entry, tells you whether the magic packet was
actually the trigger.

---

## 4. Verifying with the agent

### Readiness

```powershell
curl.exe -k https://localhost:48220/api/v1/power/wol/readiness -H "X-ESI-…: …"
```

The route requires a signed request (`read` scope), so use the app or a signing client. A
signed readiness response, with every field the endpoint produces:

```json
{
  "ready": true,
  "armedDevices": [
    { "name": "Realtek PCIe GbE Family Controller", "macAddress": "AA:BB:CC:DD:EE:FF",
      "connectionType": "ethernet", "wakeMagicPacket": true, "wakePattern": null }
  ],
  "macAddresses": ["AA:BB:CC:DD:EE:FF", "11:22:33:44:55:66"],
  "broadcastAddresses": ["255.255.255.255", "192.168.1.255"],
  "hiberbootEnabled": false,
  "issues": []
}
```

`ready` is `issues.length == 0`. Read the `issues` array literally; each entry names the
command or the firmware setting that clears it.

| Issue | Fix |
|---|---|
| `No adapter with a hardware address was found, so Wake-on-LAN cannot be sent.` | Virtual adapters only. Connect a real NIC. |
| `No adapter reports Wake on Magic Packet as armed. Enable it in the adapter's advanced properties, or run: powercfg /deviceenablewake "<adapter name>"` | §3 |
| `Wake-on-LAN only works over a wired Ethernet connection in practice…` | Use Ethernet. |
| `Fast Startup is enabled, … Disable it with: powercfg /h off` | §1 |

### How `wakeMagicPacket` is decided, and its known false negative

The endpoint matches `powercfg /devicequery wake_armed` output against the friendly adapter
name from `NetworkInterface.GetAllNetworkInterfaces()` with a **partial, case-insensitive
substring test in both directions**:

```csharp
device.WakeMagicPacket = armed.Any(a =>
    a.Contains(adapter.Name, StringComparison.OrdinalIgnoreCase) ||
    adapter.Name.Contains(a, StringComparison.OrdinalIgnoreCase));
```

The comment in the source is explicit about the intent: an adapter that does not match is
reported as **not armed** rather than optimistically assumed armed. That is the right
default, but it produces false negatives when the driver name and the friendly name share
nothing — `"Realtek PCIe GbE Family Controller"` against `"Ethernet"`, for instance.

`armedDevices` lists **every** non-loopback, non-tunnel adapter with a MAC address —
Wi-Fi, Hyper-V and VPN virtual adapters included. `NetworkProvider.GetWakeCapableAdapters`
carries a doc comment saying "adapters that are wired", but its implementation applies no
such filter. The endpoint only uses the list to raise the "no wired adapter" issue, so the
extra entries are noise rather than a defect; do not read a virtual adapter appearing there
as evidence that it is a wake target.

When `wakeMagicPacket` is `false` and `powercfg /devicequery wake_armed` clearly lists
your adapter, the discrepancy is a naming artefact, not a configuration error. Trust
`powercfg` over the endpoint.

### Sending the packet

```
POST /api/v1/power/wake        scope: control
```

```json
{ "macAddress": "AA:BB:CC:DD:EE:FF",
  "broadcastAddresses": ["255.255.255.255", "192.168.1.255"],
  "ports": [9, 7],
  "repeat": 3 }
```

```json
{ "sent": 12, "targets": ["255.255.255.255:9,7", "192.168.1.255:9,7"], "ports": [9, 7] }
```

Defaults if omitted: `ports` `[9, 7]`, `repeat` `3` (clamped 1–10, 100 ms apart),
`broadcastAddresses` the agent's recommended set — `255.255.255.255` plus the
subnet-directed broadcast of every up, non-loopback IPv4 interface. The `macAddress`
accepts `AA:BB:CC:DD:EE:FF`, `aa-bb-cc-dd-ee-ff` and `aabb.ccdd.eeff`.

Three repetitions is standard, not superstition: a NIC in WoL state runs minimal firmware
and can miss a single datagram, and switch MAC tables expire.

**`sent` counts datagrams handed to the socket. It is not a delivery confirmation and not a
confirmation that anything woke up.** `WakeOnLanSender` has no feedback path, by design —
its own comment says reporting "woken up" would be dishonest. `POST /api/v1/power/wake` is
scoped `control` rather than `read` precisely because it is an action with a real effect,
not a query.

Normally **the phone sends the magic packet itself** and never touches this route. The
endpoint exists so a relay node or a gateway can send on the PC's behalf.

### End-to-end verification

Because the response cannot confirm delivery, verify on the wire or at the NIC.

**On the wire.** Wireshark display filter, on the sending machine, before you put it to
sleep:

```
udp.port == 9 || udp.port == 7
```

The payload is 102 bytes: six `0xFF` followed by the target MAC repeated sixteen times. If
the capture shows the datagram leaving, the sender is working and any failure is on the
receiving side — the switch, the AP isolation setting, or the NIC.

**At the NIC.** On most switches and routers the port LED flickers when a magic packet
arrives, because the PHY wakes to check it even if the packet is rejected. That is a
reliable, tool-free signal that the packet reached the machine.

**End to end.**

1. Run `GET /api/v1/power/wol/readiness` and confirm `ready: true` and
   `hiberbootEnabled: false`.
2. Confirm the address of the machine the phone will use:
   ```powershell
   powercfg /devicequery wake_armed
   Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.PrefixOrigin -ne 'WellKnown' }
   ```
   A **statically configured** address is strongly preferred. A DHCP lease that has expired
   while the machine was off means the magic packet goes to an address the NIC no longer
   holds. This is the second most common failure after Fast Startup.
3. `shutdown /s /t 0` from the machine itself.
4. Wait until the machine is off — 10 seconds is enough, but do not cut the mains.
5. Send the magic packet from the phone.
6. `powercfg /lastwake` after it boots.

Step 4's second sentence is not a joke. A surge protector or a switched power strip that
is switched off removes the standby power budget the NIC needs, and the machine can only be
woken by pressing the power button.

---

## 5. Checklist

| # | Check | How | Why it matters |
|---|---|---|---|
| 1 | Fast Startup is off | `powercfg /a`, then the `HiberbootEnabled` value | Without this, shutdown and restart leave the machine in S4 and a magic packet is not seen at all |
| 2 | The machine has a real S3 | `powercfg /a` lists `Standby (S3)` | Modern Standby machines cannot be woken from sleep by any means |
| 3 | Firmware: Wake on LAN / PME on, Deep Sleep off, ErP off | BIOS/UEFI | Software cannot grant a capability the firmware does not have |
| 4 | Driver: Wake on Magic Packet on | Device Manager → adapter → Advanced | This is the driver-level arm |
| 5 | Driver: Shutdown Wake-On-Lan on (Intel) | Device Manager → adapter → Advanced | Required for shutdown-WoL on many Intel NICs |
| 6 | Windows: adapter armed | `powercfg /devicequery wake_armed` lists it | Windows maintains its own list |
| 7 | The MAC in the app is the wired NIC's | `Get-NetAdapter` / `ipconfig /all` | Waking the Wi-Fi MAC does nothing |
| 8 | The IP is static, or the lease is long | `ipconfig /all` | An expired DHCP lease means the packet goes nowhere |
| 9 | Broadcast reaches the subnet | Wireshark on the sender, or the port LED | Guest networks and AP/client isolation drop layer-2 broadcast silently |
| 10 | The agent agrees | `GET /api/v1/power/wol/readiness` → `ready: true` | Catches 6, and reports 1 for you |

---

## 6. What will not work, and why

| Attempt | Why it fails |
|---|---|
| Magic packet to a machine in Modern Standby (S0 low power idling) | There is no S3 to wake from. The radio is not in a WoL-armed state |
| Magic packet to a machine shut down with Fast Startup on | S4, not S5. The NIC is not armed |
| Magic packet across a router or a VLAN boundary | WoL is a layer-2 broadcast. It does not route |
| Magic packet on a guest or isolated Wi-Fi network | AP isolation and client isolation drop the broadcast with no ICMP to indicate it |
| Magic packet with the switch physically off | No standby power budget for the PHY |
| Magic packet to a USB or Wi-Fi adapter | WoL is an Ethernet-NIC capability |
| Waking a machine whose DHCP lease expired while off | The packet is addressed to an address the NIC no longer has. Make the address static |
| Relying on `sent` in the response to mean the machine woke | It counts datagrams handed to a socket. There is no feedback path |

---

## 7. Related

| | |
|---|---|
| `/api/v1/power/wake`, `/api/v1/power/wol/readiness` | [`API.md`](API.md) §5 |
| Magic packet format and port constants | [`PROTOCOL.md`](PROTOCOL.md) §9 |
| `PowerStateProbe`, `WakeOnLanSender` | `windows-agent/src/ESI.RemotePc.Platform/Power/` |
| General troubleshooting | [`TROUBLESHOOTING.md`](TROUBLESHOOTING.md) |
