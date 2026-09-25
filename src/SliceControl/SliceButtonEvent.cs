namespace SliceControl;

public enum SliceButtonEventKind
{
    Down,
    Up,
    Triggered
}

public sealed record SliceButtonEvent(
    SliceButton Button,
    SliceButtonEventKind Kind,
    byte ReportId,
    byte RawValue)
{
    public override string ToString()
    {
        return $"{Button} {Kind} " +
               $"[report=0x{ReportId:X2}, value=0x{RawValue:X2}]";
    }
}