using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SliceTranscribe;

internal static class DiagnosticLog
{
    private const long MaxBytes =
        10L *
        1024L *
        1024L;

    private const int ArchiveCount =
        3;

    private static readonly object Gate =
        new();

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented =
                false
        };

    private static readonly string RunId =
        $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}";

    private static readonly string DirectoryPath =
        System.IO.Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "SliceAppliance",
            "Logs");

    private static readonly string FilePath =
        System.IO.Path.Combine(
            DirectoryPath,
            "diagnostics.jsonl");

    private static bool _initialized;

    public static string Path =>
        FilePath;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(
                    DirectoryPath);

                RotateIfNeeded();

                _initialized =
                    true;
            }
            catch
            {
                return;
            }
        }

        using Process process =
            Process.GetCurrentProcess();

        Event(
            "process",
            "started",
            new
            {
                run_id =
                    RunId,
                pid =
                    Environment.ProcessId,
                framework =
                    RuntimeInformation.FrameworkDescription,
                os =
                    RuntimeInformation.OSDescription,
                architecture =
                    RuntimeInformation.ProcessArchitecture.ToString(),
                working_directory =
                    Environment.CurrentDirectory,
                working_set_mb =
                    Math.Round(
                        process.WorkingSet64 /
                        1024d /
                        1024d,
                        1),
                private_mb =
                    Math.Round(
                        process.PrivateMemorySize64 /
                        1024d /
                        1024d,
                        1),
                handles =
                    SafeHandleCount(
                        process),
                threads =
                    process.Threads.Count
            });
    }

    public static void Event(
        string category,
        string name,
        object? data = null)
    {
        Write(
            "info",
            category,
            name,
            data,
            exception: null);
    }

    public static void Warning(
        string category,
        string name,
        object? data = null)
    {
        Write(
            "warning",
            category,
            name,
            data,
            exception: null);
    }

    public static void Error(
        string category,
        string name,
        Exception exception,
        object? data = null)
    {
        Write(
            "error",
            category,
            name,
            data,
            exception);
    }

    private static void Write(
        string level,
        string category,
        string name,
        object? data,
        Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                if (!_initialized)
                {
                    try
                    {
                        Directory.CreateDirectory(
                            DirectoryPath);

                        RotateIfNeeded();

                        _initialized =
                            true;
                    }
                    catch
                    {
                        return;
                    }
                }

                var payload =
                    new
                    {
                        timestamp =
                            DateTimeOffset.Now.ToString(
                                "O"),
                        utc =
                            DateTimeOffset.UtcNow.ToString(
                                "O"),
                        run_id =
                            RunId,
                        pid =
                            Environment.ProcessId,
                        thread =
                            Environment.CurrentManagedThreadId,
                        level,
                        category,
                        @event =
                            name,
                        data,
                        exception =
                            exception is null
                                ? null
                                : DescribeException(
                                    exception)
                    };

                string line =
                    JsonSerializer.Serialize(
                        payload,
                        JsonOptions);

                File.AppendAllText(
                    FilePath,
                    line +
                    Environment.NewLine,
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false));

                if (
                    new FileInfo(
                        FilePath).Length >=
                    MaxBytes)
                {
                    Rotate();
                }
            }
        }
        catch
        {
            // Diagnostics must never be able to break the appliance.
        }
    }

    private static object DescribeException(
        Exception exception)
    {
        return new
        {
            type =
                exception.GetType().FullName,
            message =
                exception.Message,
            hresult =
                $"0x{exception.HResult:X8}",
            stack =
                exception.StackTrace,
            inner =
                exception.InnerException is null
                    ? null
                    : DescribeException(
                        exception.InnerException)
        };
    }

    private static int? SafeHandleCount(
        Process process)
    {
        try
        {
            return process.HandleCount;
        }
        catch
        {
            return null;
        }
    }

    private static void RotateIfNeeded()
    {
        if (
            File.Exists(
                FilePath) &&
            new FileInfo(
                FilePath).Length >=
                MaxBytes)
        {
            Rotate();
        }
    }

    private static void Rotate()
    {
        for (int i = ArchiveCount;
             i >= 1;
             i--)
        {
            string current =
                i == 1
                    ? FilePath
                    : FilePath +
                      "." +
                      (i - 1);

            string next =
                FilePath +
                "." +
                i;

            if (!File.Exists(
                current))
            {
                continue;
            }

            if (File.Exists(
                next))
            {
                File.Delete(
                    next);
            }

            File.Move(
                current,
                next);
        }
    }
}
