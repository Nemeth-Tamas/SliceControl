namespace SliceControl;

public sealed record SlicePhysicalButtonEvent(
    SlicePhysicalButton Button,
    DateTimeOffset Timestamp,
    IReadOnlyList<SliceRawInputReport> Evidence)
{
    public override string ToString()
    {
        return Button.ToString();
    }
}
