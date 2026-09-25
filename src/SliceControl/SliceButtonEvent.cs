namespace SliceControl;

public sealed record SliceButtonEvent(
    SliceButton Button,
    bool Pressed,
    byte ReportId,
    byte RawValue)
{
    public override string ToString()
    {
        return $"{Button} {(Pressed ? "Down" : "Up")} " +
               $"[report=0x{ReportId:X2}, value=0x{RawValue:X2}]";
    }
}