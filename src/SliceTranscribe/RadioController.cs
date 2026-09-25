using System.Net.Sockets;
using System.Text;

namespace SliceTranscribe;

internal static class RadioController
{
    private const string Host = "127.0.0.1";
    private const int Port = 4212;

    private static readonly SemaphoreSlim Gate =
        new(
            1,
            1);

    private static readonly HashSet<string> PauseReasons =
        new(
            StringComparer.OrdinalIgnoreCase);

    private static bool _paused;

    public static async Task<bool> RequestPauseAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(
            cancellationToken);

        try
        {
            PauseReasons.Add(
                reason);

            if (_paused)
            {
                return true;
            }

            bool sent =
                await SendAsync(
                    "pause",
                    cancellationToken);

            if (!sent)
            {
                PauseReasons.Remove(
                    reason);

                return false;
            }

            _paused =
                true;

            Console.WriteLine(
                $"RADIO -> paused ({string.Join(", ", PauseReasons)})");

            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<bool> ReleasePauseAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(
            cancellationToken);

        try
        {
            PauseReasons.Remove(
                reason);

            if (!_paused ||
                PauseReasons.Count != 0)
            {
                return true;
            }

            // VLC RC's pause command is a state toggle. Use it again to resume.
            bool sent =
                await SendAsync(
                    "pause",
                    cancellationToken);

            if (!sent)
            {
                return false;
            }

            _paused =
                false;

            Console.WriteLine(
                "RADIO -> resumed");

            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> SendAsync(
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client =
                new TcpClient();

            await client.ConnectAsync(
                Host,
                Port,
                cancellationToken);

            await using NetworkStream stream =
                client.GetStream();

            byte[] bytes =
                Encoding.ASCII.GetBytes(
                    command + "\n");

            await stream.WriteAsync(
                bytes,
                cancellationToken);

            await stream.FlushAsync(
                cancellationToken);

            return true;
        }
        catch (
            Exception ex)
            when (
                ex is SocketException or
                IOException or
                OperationCanceledException)
        {
            if (ex is OperationCanceledException &&
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            Console.Error.WriteLine(
                $"Radio control unavailable: {ex.Message}");

            return false;
        }
    }
}
