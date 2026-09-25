namespace SliceControl;

public enum SliceTelephonyState : byte
{
    Idle = 0x00,
    EnterCall = 0x02,
    Attention = 0x04,
    ActiveDelayedExit = 0x20,
    ActiveImmediateExit = 0x22
}

public sealed class SliceTelephony
{
    private readonly SliceRaw _raw;

    internal SliceTelephony(SliceRaw raw)
    {
        _raw = raw;
    }

    public void SetState(SliceTelephonyState state) =>
        _raw.SendCollection01(0x41, (byte)state);

    public void EnterCall() =>
        SetState(SliceTelephonyState.EnterCall);

    public void Attention() =>
        SetState(SliceTelephonyState.Attention);

    public void ActiveDelayedExit() =>
        SetState(SliceTelephonyState.ActiveDelayedExit);

    public void ActiveImmediateExit() =>
        SetState(SliceTelephonyState.ActiveImmediateExit);

    public void EndCall() =>
        SetState(SliceTelephonyState.Idle);

    private const int ThemeTransitionDelayMs = 25;

    /// <summary>
    /// Sends the raw mute/theme-on report (42 01). While an active breathing
    /// renderer is already running, this can latch the muted theme without
    /// immediately changing the visible LEDs.
    /// </summary>
    public void SendMuteOnReport() =>
        _raw.SendCollection01(0x42, 0x01);

    /// <summary>
    /// Backward-compatible alias for the raw mute/theme-on report.
    /// Prefer ApplyMuteTheme when an immediate visible transition is wanted.
    /// </summary>
    public void EnableMuteTheme() =>
        SendMuteOnReport();

    public void SendMuteOffReport() =>
        _raw.SendCollection01(0x42, 0x00);

    /// <summary>
    /// Applies the red/yellow muted theme and resumes the requested active
    /// state. The driver/firmware needs a short lifecycle transition for an
    /// already-running active renderer to pick up the new theme immediately.
    /// </summary>
    public void ApplyMuteTheme(SliceTelephonyState resumeState)
    {
        ValidateActiveState(resumeState);

        EnterCall();
        Thread.Sleep(ThemeTransitionDelayMs);

        SendMuteOnReport();
        Thread.Sleep(ThemeTransitionDelayMs);

        SetState(resumeState);
    }

    /// <summary>
    /// Clears the observed muted/red theme and resumes the requested active state.
    /// The tested HP driver requires leaving the active 0x20/0x22 state first;
    /// short delays are intentional so the firmware commits each state change.
    /// </summary>
    public void ClearMuteTheme(SliceTelephonyState resumeState)
    {
        ValidateActiveState(resumeState);

        EnterCall();
        Thread.Sleep(ThemeTransitionDelayMs);

        SendMuteOffReport();
        Thread.Sleep(ThemeTransitionDelayMs);

        SetState(resumeState);
    }

    private static void ValidateActiveState(
        SliceTelephonyState state)
    {
        if (state is not (
            SliceTelephonyState.ActiveDelayedExit or
            SliceTelephonyState.ActiveImmediateExit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                "Resume state must be an active call state.");
        }
    }

    public void SendStateReport(byte value) =>
        _raw.SendCollection01(0x41, value);

    public void SendMuteReport(byte value) =>
        _raw.SendCollection01(0x42, value);
}
