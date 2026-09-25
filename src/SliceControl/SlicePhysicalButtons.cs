using SliceControl.Hid;

namespace SliceControl;

/// <summary>
/// Decodes the real Collaboration Cover controls from the HP driver's private
/// key event plus the translated HID reports.
/// </summary>
public sealed class SlicePhysicalButtons
{
    private static readonly TimeSpan EvidenceLookBehind =
        TimeSpan.FromMilliseconds(120);

    private static readonly TimeSpan EvidenceSettleTime =
        TimeSpan.FromMilliseconds(45);

    private static readonly TimeSpan EvidenceRetention =
        TimeSpan.FromMilliseconds(300);

    private readonly SliceDevicePaths _paths;

    internal SlicePhysicalButtons(
        SliceDevicePaths paths)
    {
        _paths = paths;
    }

    /// <summary>
    /// Watches the five physical Collaboration Cover controls:
    /// Pickup, Hangup, Mute, VolumeUp, and VolumeDown.
    /// </summary>
    /// <remarks>
    /// The private HP PDO exposes a general "recognized key" event but does not
    /// expose the stored raw key identifier. This monitor correlates that event
    /// with Collection 01/02 reports. The stock HPSliceTelephonyService also
    /// uses the private event registration; stop it before taking ownership if
    /// registration is rejected or if exclusive behavior is required.
    /// </remarks>
    public async Task WatchAsync(
        Action<SlicePhysicalButtonEvent> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        using SlicePrivateDriver privateDriver =
            SlicePrivateDriver.Open();

        using var captureCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        object sync = new();
        var evidence = new List<TimedReport>();

        Task telephonyTask =
            CaptureAsync(
                _paths.Collection01,
                1,
                sync,
                evidence,
                captureCts.Token);

        Task consumerTask =
            CaptureAsync(
                _paths.Collection02,
                2,
                sync,
                evidence,
                captureCts.Token);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await privateDriver.WaitForKeyPressAsync(
                    cancellationToken);

                DateTimeOffset signalTime =
                    DateTimeOffset.UtcNow;

                await Task.Delay(
                    EvidenceSettleTime,
                    cancellationToken);

                DateTimeOffset now =
                    DateTimeOffset.UtcNow;

                List<SliceRawInputReport> reports;

                lock (sync)
                {
                    DateTimeOffset earliest =
                        signalTime - EvidenceLookBehind;

                    reports = evidence
                        .Where(item =>
                            item.Timestamp >= earliest &&
                            item.Timestamp <= now)
                        .Select(item => item.Report)
                        .ToList();

                    evidence.RemoveAll(
                        item =>
                            item.Timestamp <= now);
                }

                SlicePhysicalButton button =
                    Classify(reports);

                callback(
                    new SlicePhysicalButtonEvent(
                        button,
                        signalTime,
                        reports));
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            captureCts.Cancel();

            await IgnoreCancellationAsync(
                telephonyTask);

            await IgnoreCancellationAsync(
                consumerTask);
        }
    }

    private static SlicePhysicalButton Classify(
        IReadOnlyList<SliceRawInputReport> reports)
    {
        if (Contains(
            reports,
            collection: 2,
            reportId: 0x31,
            value: 0x01))
        {
            return SlicePhysicalButton.VolumeUp;
        }

        if (Contains(
            reports,
            collection: 2,
            reportId: 0x31,
            value: 0x02))
        {
            return SlicePhysicalButton.VolumeDown;
        }

        if (Contains(
            reports,
            collection: 1,
            reportId: 0x32,
            value: 0x10))
        {
            return SlicePhysicalButton.Mute;
        }

        if (Contains(
            reports,
            collection: 1,
            reportId: 0x32,
            value: 0x02))
        {
            return SlicePhysicalButton.Pickup;
        }

        if (Contains(
            reports,
            collection: 1,
            reportId: 0x32,
            value: 0x00))
        {
            return SlicePhysicalButton.Hangup;
        }

        return SlicePhysicalButton.Unknown;
    }

    private static bool Contains(
        IEnumerable<SliceRawInputReport> reports,
        int collection,
        byte reportId,
        byte value)
    {
        return reports.Any(
            report =>
                report.Collection == collection &&
                report.Data.Length >= 2 &&
                report.Data[0] == reportId &&
                report.Data[1] == value);
    }

    private static async Task CaptureAsync(
        string path,
        int collection,
        object sync,
        List<TimedReport> evidence,
        CancellationToken cancellationToken)
    {
        using FileStream stream =
            HidIo.OpenRead(path);

        byte[] buffer = new byte[64];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;

            try
            {
                read = await stream.ReadAsync(
                    buffer,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read <= 0)
            {
                continue;
            }

            DateTimeOffset timestamp =
                DateTimeOffset.UtcNow;

            byte[] data =
                buffer.Take(read).ToArray();

            lock (sync)
            {
                evidence.Add(
                    new TimedReport(
                        timestamp,
                        new SliceRawInputReport(
                            collection,
                            data)));

                DateTimeOffset cutoff =
                    timestamp - EvidenceRetention;

                evidence.RemoveAll(
                    item =>
                        item.Timestamp < cutoff);
            }
        }
    }

    private static async Task IgnoreCancellationAsync(
        Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record TimedReport(
        DateTimeOffset Timestamp,
        SliceRawInputReport Report);
}
