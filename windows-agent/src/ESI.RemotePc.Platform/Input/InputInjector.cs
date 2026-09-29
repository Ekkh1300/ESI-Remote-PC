using System.Runtime.InteropServices;
using Esi.Protocol;
using Esi.Platform.Native;
using Esi.Platform.Session;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Input;

/// <summary>
/// Injects mouse and keyboard events with <c>SendInput</c>.
/// <para>
/// <b>This type may only be used from a process running on the interactive desktop.</b>
/// <c>SendInput</c> is subject to User Interface Privilege Isolation, so a service in
/// session 0 cannot inject into a user's session, and the desktop handle it would be
/// bound to is not the interactive one. The agent therefore instantiates this from
/// <c>ESI.RemotePc.Session</c>, not from the service.
/// </para>
/// <para>
/// Keyboard events use <c>KEYEVENTF_SCANCODE</c> with a scan code translated from the
/// virtual key, which produces correct keyboard layouts and is what real input drivers
/// do. When a <c>code</c> has no mapping, <c>KEYEVENTF_UNICODE</c> is used as a fallback
/// so text input still works for the majority of layouts.
/// </para>
/// </summary>
public sealed class InputInjector(ILogger<InputInjector> log)
{
    public int Inject(RemoteInputRequest input, int screenWidth, int screenHeight)
    {
        ArgumentNullException.ThrowIfNull(input);

        return input.Kind.ToLowerInvariant() switch
        {
            "mousemove" or "pointermove" => Move(input, screenWidth, screenHeight),
            "mousebutton" or "pointerbutton" => Button(input),
            "mousewheel" or "wheel" => Wheel(input),
            "key" => Key(input),
            "text" => Text(input.Text ?? string.Empty),
            _ => 0,
        };
    }

    private int Move(RemoteInputRequest input, int width, int height)
    {
        if (input.X is not { } nx || input.Y is not { } ny)
            return 0;

        // The client sends normalised 0..1 coordinates so a phone of any resolution can
        // drive any display. Absolute mouse moves need the virtual-desktop 0..65535 range.
        var clampedX = Math.Clamp(nx, 0, 1);
        var clampedY = Math.Clamp(ny, 0, 1);

        var scaledX = (int)Math.Round(clampedX * 65535.0 / Math.Max(1, width - 1));
        var scaledY = (int)Math.Round(clampedY * 65535.0 / Math.Max(1, height - 1));

        var mouse = new MOUSEINPUT
        {
            dx = scaledX,
            dy = scaledY,
            mouseData = 0,
            dwFlags = WinConst.MOUSEEVENTF_MOVE | WinConst.MOUSEEVENTF_ABSOLUTE |
                      WinConst.MOUSEEVENTF_VIRTUALDESK | WinConst.MOUSEEVENTF_MOVE_NOCOALESCE,
        };

        return Send([new INPUT { type = WinConst.INPUT_MOUSE, U = new InputUnionUnion { mi = mouse } }]);
    }

    private int Button(RemoteInputRequest input)
    {
        var down = input.Down ?? true;
        var button = (input.Button ?? "left").ToLowerInvariant();

        var (downFlag, upFlag, isExtended) = button switch
        {
            "right" => (WinConst.MOUSEEVENTF_RIGHTDOWN, WinConst.MOUSEEVENTF_RIGHTUP, false),
            "middle" => (WinConst.MOUSEEVENTF_MIDDLEDOWN, WinConst.MOUSEEVENTF_MIDDLEUP, false),
            "back" => (WinConst.MOUSEEVENTF_XDOWN, WinConst.MOUSEEVENTF_XUP, true),
            "forward" => (WinConst.MOUSEEVENTF_XDOWN, WinConst.MOUSEEVENTF_XUP, true),
            "left" => (WinConst.MOUSEEVENTF_LEFTDOWN, WinConst.MOUSEEVENTF_LEFTUP, false),
            _ => (WinConst.MOUSEEVENTF_LEFTDOWN, WinConst.MOUSEEVENTF_LEFTUP, false),
        };

        var flags = down ? downFlag : upFlag;
        var mouseData = button switch
        {
            "back" => WinConst.XBUTTON1,
            "forward" => WinConst.XBUTTON2,
            _ => 0u,
        };

        var mouse = new MOUSEINPUT { dx = 0, dy = 0, mouseData = mouseData, dwFlags = flags };
        return Send([new INPUT { type = WinConst.INPUT_MOUSE, U = new InputUnionUnion { mi = mouse } }]);
    }

    private int Wheel(RemoteInputRequest input)
    {
        // WHEEL_DELTA is documented as 120 per notch. The client sends notches.
        const int WheelDelta = 120;

        var sent = 0;

        if (input.DeltaY is { } dy && dy != 0)
        {
            var notches = (int)Math.Round(dy / 120.0);
            if (notches == 0)
                notches = Math.Sign(dy);

            var mouse = new MOUSEINPUT
            {
                dx = 0,
                dy = 0,
                // Negative = away from the user (scroll up), matching browser conventions.
                mouseData = unchecked((uint)(notches * WheelDelta)),
                dwFlags = WinConst.MOUSEEVENTF_WHEEL,
            };
            sent += Send([new INPUT { type = WinConst.INPUT_MOUSE, U = new InputUnionUnion { mi = mouse } }]);
        }

        if (input.DeltaX is { } dxv && dxv != 0)
        {
            var notches = (int)Math.Round(dxv / 120.0);
            if (notches == 0)
                notches = Math.Sign(dxv);

            var mouse = new MOUSEINPUT
            {
                dx = 0,
                dy = 0,
                mouseData = unchecked((uint)(notches * WheelDelta)),
                dwFlags = WinConst.MOUSEEVENTF_HWHEEL,
            };
            sent += Send([new INPUT { type = WinConst.INPUT_MOUSE, U = new InputUnionUnion { mi = mouse } }]);
        }

        return sent;
    }

    private int Key(RemoteInputRequest input)
    {
        var vk = KeyMap.ToVirtualKey(input.Code);
        if (vk is null)
        {
            // Unknown physical key: fall back to typing the literal character, which at
            // least keeps text entry usable for exotic layouts.
            if (!string.IsNullOrEmpty(input.Text))
                return Text(input.Text);
            log.LogDebug("No virtual-key mapping for code {Code}", input.Code);
            return 0;
        }

        var down = input.Down ?? true;
        var isExtended = KeyMap.IsExtended(input.Code);
        var mods = input.Mods ?? [];

        var events = new List<INPUT>(8);

        // Modifiers first so the key is seen in context (Ctrl+C, not C then Ctrl).
        foreach (var mod in mods)
        {
            if (KeyMap.ToVirtualKey(mod) is not { } modVk)
                continue;
            events.Add(Keyboard(modVk, 0, down: true, isExtended: KeyMap.IsExtended(mod), unicode: false));
        }

        var vkValue = (ushort)vk.Value;
        var scan = (ushort)Native.USER32.MapVirtualKeyW(vkValue, WinConst.MAPVK_VK_TO_VSC);

        events.Add(Keyboard(vkValue, scan, down, isExtended, unicode: false));
        if (!down)
            events.Add(Keyboard(vkValue, scan, down: false, isExtended, unicode: false));

        return Send(events.ToArray());
    }

    private int Text(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var events = new List<INPUT>(text.Length * 2);

        // Enumerate as Rune so surrogate pairs (emoji, rare CJK, some symbols) are
        // delivered as a single code point rather than two broken glyphs.
        foreach (var rune in text.EnumerateRunes())
        {
            events.Add(Keyboard(0, (uint)rune.Value, down: true, isExtended: false, unicode: true));
            events.Add(Keyboard(0, (uint)rune.Value, down: false, isExtended: false, unicode: true));
        }

        return events.Count == 0 ? 0 : Send(events.ToArray());
    }

    private static INPUT Keyboard(ushort vk, uint scan, bool down, bool isExtended, bool unicode)
    {
        uint flags = 0;
        if (unicode)
            flags |= WinConst.KEYEVENTF_UNICODE;
        else if (isExtended)
            flags |= WinConst.KEYEVENTF_EXTENDEDKEY;
        if (!down)
            flags |= WinConst.KEYEVENTF_KEYUP;

        return new INPUT
        {
            type = WinConst.INPUT_KEYBOARD,
            U = new InputUnionUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = unicode ? (ushort)0 : vk,
                    wScan = unicode ? (ushort)scan : (ushort)0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
    }

    private int Send(INPUT[] inputs)
    {
        try
        {
            var sent = Native.USER32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length)
            {
                log.LogWarning("SendInput delivered {Sent} of {Requested} events (error {Error})",
                    sent, inputs.Length, Marshal.GetLastWin32Error());
            }
            return (int)sent;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "SendInput failed");
            return 0;
        }
    }

    /// <summary>
    /// Blocks physical input to the machine while a remote session is active. Released
    /// automatically if the process exits, since Windows restores input on process death.
    /// </summary>
    public static bool BlockPhysicalInput(bool block)
    {
        try
        {
            return Native.USER32.BlockInput(block);
        }
        catch
        {
            return false;
        }
    }
}
