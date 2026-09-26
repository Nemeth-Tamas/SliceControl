using System.Diagnostics;
using System.Text;

namespace SliceTranscribe;

internal static class WindowsTtsSpeaker
{
    public static async Task SpeakAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(
            text))
        {
            return;
        }

        string textBase64 =
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    text));

        string script =
            "$text=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
            textBase64 +
            "')); " +
            "$voice=New-Object -ComObject SAPI.SpVoice; " +
            "$voice.Rate=0; $voice.Volume=100; " +
            "[void]$voice.Speak($text);";

        string encodedCommand =
            Convert.ToBase64String(
                Encoding.Unicode.GetBytes(
                    script));

        var startInfo =
            new ProcessStartInfo
            {
                FileName =
                    "powershell.exe",

                UseShellExecute =
                    false,

                CreateNoWindow =
                    true
            };

        startInfo.ArgumentList.Add(
            "-NoProfile");

        startInfo.ArgumentList.Add(
            "-NonInteractive");

        startInfo.ArgumentList.Add(
            "-EncodedCommand");

        startInfo.ArgumentList.Add(
            encodedCommand);

        using Process process =
            Process.Start(
                startInfo)
            ?? throw new InvalidOperationException(
                "Could not start Windows TTS.");

        try
        {
            await process.WaitForExitAsync(
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(
                        entireProcessTree: true);
                }
            }
            catch
            {
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Windows TTS exited with code {process.ExitCode}.");
        }
    }
}
