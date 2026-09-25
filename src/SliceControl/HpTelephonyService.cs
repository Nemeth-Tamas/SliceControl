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