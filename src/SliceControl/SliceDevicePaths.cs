namespace SliceControl;

public sealed record SliceDevicePaths(
    string Collection01,
    string Collection02,
    string Collection03,
    string? Collection04)
{
    public override string ToString()
    {
        return
            $"Col01: {Collection01}{Environment.NewLine}" +
            $"Col02: {Collection02}{Environment.NewLine}" +
            $"Col03: {Collection03}{Environment.NewLine}" +
            $"Col04: {Collection04 ?? "(not found)"}";
    }
}