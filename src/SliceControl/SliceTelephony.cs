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

    public void EnableMuteTheme() =>
        _raw.SendCollection01(0x42, 0x01);

    public void SendMuteOffReport() =>
        _raw.SendCollection01(0x42, 0x00);

    /// <summary>
    /// Clears the observed muted/red theme and resumes the requested active state.
    /// The tested HP driver requires leaving the active 0x20/0x22 state first;
    /// sending 42 00 while active does not visibly clear the latched theme.
    /// </summary>
    public void ClearMuteTheme(SliceTelephonyState resumeState)
    {
        if (resumeState is not (
            SliceTelephonyState.ActiveDelayedExit or
            SliceTelephonyState.ActiveImmediateExit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(resumeState),
                "Resume state must be an active call state.");
        }

        EnterCall();
        SendMuteOffReport();
        SetState(resumeState);
    }

    public void SendStateReport(byte value) =>
        _raw.SendCollection01(0x41, value);

    public void SendMuteReport(byte value) =>
        _raw.SendCollection01(0x42, value);
}
