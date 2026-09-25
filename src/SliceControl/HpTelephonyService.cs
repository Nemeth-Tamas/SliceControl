using System.Diagnostics;

namespace SliceControl;

public static class HpTelephonyService
{
    public const string ServiceName = "HPSliceTelephonyService";

    public static string Stop()
    {
        return RunSc($"stop {ServiceName}");
    }

    public static string Start()
    {
        return RunSc($"start {ServiceName}");
    }

    public static string Query()
    {
        return RunSc($"query {ServiceName}");
    }

    public static bool IsRunning()
    {
        return HasStateCode(
            Query(),
            4);
    }

    public static void StopAndWait(
        TimeSpan? timeout = null)
    {
        if (HasStateCode(Query(), 1))
        {
            return;
        }

        Stop();

        WaitForState(
            1,
            timeout ?? TimeSpan.FromSeconds(10));
    }

    public static void StartAndWait(
        TimeSpan? timeout = null)
    {
        if (IsRunning())
        {
            return;
        }

        Start();

        WaitForState(
            4,
            timeout ?? TimeSpan.FromSeconds(10));
    }

    private static void WaitForState(
        int stateCode,
        TimeSpan timeout)
    {
        DateTime deadline =
            DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (HasStateCode(
                Query(),
                stateCode))
            {
                return;
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException(
            $"Timed out waiting for {ServiceName} state {stateCode}.");
    }

    private static bool HasStateCode(
        string queryOutput,
        int stateCode)
    {
        string marker =
            $": {stateCode} ";

        return queryOutput
            .Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries)
            .Any(line =>
                line.Contains(
                    "STATE",
                    StringComparison.OrdinalIgnoreCase) &&
                line.Contains(
                    marker,
                    StringComparison.Ordinal));
    }

    private static string RunSc(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using Process process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Could not start sc.exe.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        process.WaitForExit();

        string combined =
            string.Join(
                Environment.NewLine,
                new[] { stdout, stderr }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"sc.exe exited with code {process.ExitCode}:{Environment.NewLine}{combined}");
        }

        return combined.Trim();
    }
}