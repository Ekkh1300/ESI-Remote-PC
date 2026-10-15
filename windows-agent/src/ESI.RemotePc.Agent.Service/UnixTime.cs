namespace Esi.Service;

/// <summary>Unix epoch seconds, which is what every timestamp on the wire is.</summary>
///
/// <para>
/// This exists to stop one specific mistake: <c>ticks / TimeSpan.TicksPerSecond</c>. .NET
/// counts ticks from 0001-01-01, so that expression produces a number 62,135,596,800 seconds
/// larger than the Unix timestamp it looks like. The error is 1,970 years wide, which is past
/// what any display can express: the phone rendered one of these as
/// <c>started in 17259887 h</c> instead of the time the process began, and the file screen
/// would have done the same with a file's date.
/// </para>
/// <para>
/// The rest of the agent already writes timestamps the other way - <c>PowerService</c>,
/// <c>SystemInfoProvider</c>, <c>AgentDatabase.NowUnix</c> and every WebSocket frame all call
/// <see cref="DateTimeOffset.ToUnixTimeSeconds"/>. These call sites are the ones that took a
/// <see cref="DateTime"/> rather than asking for the clock directly, and so reached for the
/// arithmetic instead.
/// </para>
internal static class UnixTime
{
    /// <summary>Seconds since 1970-01-01T00:00:00Z for <paramref name="value"/>, or null.</summary>
    public static long? Seconds(DateTime? value)
    {
        if (value is null)
        {
            return null;
        }

        var instant = value.Value;

        // Process.StartTime and certificate dates arrive as Local or Unspecified. An
        // Unspecified one is read as local time, which is what the previous
        // `ToUniversalTime()` assumed before it went on to divide the wrong epoch.
        if (instant.Kind == DateTimeKind.Unspecified)
        {
            instant = DateTime.SpecifyKind(instant, DateTimeKind.Local);
        }

        return new DateTimeOffset(instant).ToUnixTimeSeconds();
    }
}
