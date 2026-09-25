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

    public void SendStateReport(byte value) =>
        _raw.SendCollection01(0x41, value);

    public void SendMuteReport(byte value) =>
        _raw.SendCollection01(0x42, value);
}
