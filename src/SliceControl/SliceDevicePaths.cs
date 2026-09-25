namespace SliceControl;

public sealed record SliceDevicePaths(
    string Collection01,
    string Collection02,
    string Collection03,
    string? Collection04,
    string? Collection05)
{
    public string? GetCollection(int collection)
    {
        return collection switch
        {
            1 => Collection01,
            2 => Collection02,
            3 => Collection03,
            4 => Collection04,
            5 => Collection05,
            _ => null
        };
    }

    public override string ToString()
    {
        return
            $"Col01: {Collection01}{Environment.NewLine}" +
            $"Col02: {Collection02}{Environment.NewLine}" +
            $"Col03: {Collection03}{Environment.NewLine}" +
            $"Col04: {Collection04 ?? "(not found)"}{Environment.NewLine}" +
            $"Col05: {Collection05 ?? "(not found)"}";
    }
}
