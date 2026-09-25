using Whisper.net.Ggml;

namespace SliceTranscribe;

internal static class LocalWhisperModel
{
    public const string FileName =
        "ggml-base.bin";

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "SliceTranscribe",
            "Models",
            FileName);

    public static async Task<string> EnsureBaseAsync(
        string? requestedPath = null,
        CancellationToken cancellationToken = default)
    {
        string path =
            string.IsNullOrWhiteSpace(
                requestedPath)
                ? DefaultPath
                : Path.GetFullPath(
                    requestedPath);

        if (File.Exists(path))
        {
            return path;
        }

        string? directory =
            Path.GetDirectoryName(
                path);

        if (!string.IsNullOrWhiteSpace(
            directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        string partialPath =
            path + ".download";

        TryDelete(
            partialPath);

        Console.WriteLine(
            "Whisper base model was not found.");

        Console.WriteLine(
            "Downloading multilingual ggml-base.bin (~148 MB) once...");

        try
        {
            using Stream modelStream =
                await WhisperGgmlDownloader
                    .Default
                    .GetGgmlModelAsync(
                        GgmlType.Base);

            await using var file =
                new FileStream(
                    partialPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    useAsync: true);

            await modelStream.CopyToAsync(
                file,
                cancellationToken);

            await file.FlushAsync(
                cancellationToken);

            File.Move(
                partialPath,
                path,
                overwrite: true);

            Console.WriteLine(
                $"Whisper base model ready: {path}");

            return path;
        }
        catch
        {
            TryDelete(
                partialPath);

            throw;
        }
    }

    private static void TryDelete(
        string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
