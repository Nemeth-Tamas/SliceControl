using SliceControl.Hid;

namespace SliceControl;

public sealed class SliceRaw
{
    private readonly SliceDevicePaths _paths;

    internal SliceRaw(SliceDevicePaths paths)
    {
        _paths = paths;
    }

    public void SendCollection03(params byte[] report)
    {
        if (report.Length != 8)
        {
            throw new ArgumentException(
                "Collection 03 reports must contain exactly 8 bytes.",
                nameof(report));
        }

        if (report[0] != 0xFE)
        {
            throw new ArgumentException(
                "Collection 03 report ID must be 0xFE.",
                nameof(report));
        }

        using FileStream stream = HidIo.OpenWrite(_paths.Collection03);

        stream.Write(report, 0, report.Length);
        stream.Flush();
    }

    public void SendCollection04(byte value)
    {
        if (_paths.Collection04 is null)
        {
            throw new InvalidOperationException(
                "Slice Collection 04 was not found.");
        }

        byte[] report =
        {
            0xFF,
            value
        };

        using FileStream stream = HidIo.OpenWrite(_paths.Collection04);

        stream.Write(report, 0, report.Length);
        stream.Flush();
    }
}