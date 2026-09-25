namespace SliceControl;

public sealed record SliceRawInputReport(
    int Collection,
    byte[] Data)
{
    public override string ToString()
    {
        return $"COL{Collection:00} IN  " +
               string.Join(" ", Data.Select(b => $"{b:X2}"));
    }
}