namespace SliceControl;

public sealed class SliceLights
{
    private readonly SliceRaw _raw;

    internal SliceLights(SliceRaw raw)
    {
        _raw = raw;
    }

    public void Reset()
    {
        Send(0x00);
    }

    public void Ring()
    {
        Send(0x03);
    }

    public void Hello()
    {
        Send(0x08);
    }

    public void Goodbye()
    {
        Send(0x09);
    }

    public void ExitAnimation(int delayMilliseconds = 300)
    {
        Send(0x05);

        Thread.Sleep(delayMilliseconds);

        Reset();
    }

    public void SetBar(int value)
    {
        Send(
            command: 0x07,
            mode: 0x00,
            value: Clamp(value));
    }

    public void SetCall(int value = 50)
    {
        Send(
            command: 0x07,
            mode: 0x02,
            value: Clamp(value));
    }

    public void SetMutedCall(int value = 50)
    {
        Send(
            command: 0x07,
            mode: 0x03,
            value: Clamp(value));
    }

    private void Send(
        byte command,
        byte mode = 0,
        byte value = 0)
    {
        _raw.SendCollection03(
            0xFE,
            0x00,
            command,
            mode,
            value,
            0x00,
            0x00,
            0x00);
    }

    private static byte Clamp(int value)
    {
        return (byte)Math.Clamp(value, 0, 100);
    }
}