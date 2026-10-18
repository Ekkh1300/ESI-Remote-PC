using Microsoft.Win32;

using NAudio.CoreAudioApi;

using Esi.Platform.Audio;

namespace Esi.Session;

/// <summary>
/// Binds <see cref="IEndpointMute"/> to the endpoint the loopback capture actually taps, and
/// <see cref="ISilencerLedger"/> to per-user storage the next run can read back. The decisions
/// never touch this file; see <see cref="LocalOutputSilencer"/>.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is the *console-role default render device*, the same device shape
/// <c>WasapiLoopbackCapture</c>'s parameterless constructor taps, so the thing being silenced
/// is exactly the thing being captured regardless of which drive letter or hot-plug slot the
/// device sits behind.
/// </para>
/// <para>
/// The ledger rides HKCU rather than a file: the session agent runs as the user, HKCU is
/// already the user's to write, and a mute the user could hear is a user-machine fact, not a
/// system fact. ProgramData would need an ACL the installer would have to arrange for one
/// DWORD.
/// </para>
/// </remarks>
internal sealed class MMDeviceEndpointMute : IEndpointMute
{
    public bool Mute
    {
        get => DefaultDevice().AudioEndpointVolume.Mute;
        set => DefaultDevice().AudioEndpointVolume.Mute = value;
    }

    private static MMDevice DefaultDevice()
        => new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
}

internal sealed class RegistrySilencerLedger : ISilencerLedger
{
    private const string KeyPath = @"Software\ESI\RemotePc";
    private const string ValueName = "SessionLocalOutputSilenced";

    public bool? Read()
    {
        try
        {
            var value = Registry.CurrentUser.OpenSubKey(KeyPath)?.GetValue(ValueName);
            return value is int i ? i != 0 : null;
        }
        catch (Exception)
        {
            // A history that cannot be read is a history with nothing to answer for; the
            // caller treats null as "nothing outstanding".
            return null;
        }
    }

    public void Write(bool value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key.SetValue(ValueName, value ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception)
        {
            // Losing a ledger write is losing crash recovery, not losing the mute itself -
            // the mute is already on or being taken off directly. A read that answers null
            // later means the worst case is a stuck mute the user can lift with their volume
            // keys, which the release notes say out loud.
        }
    }
}
