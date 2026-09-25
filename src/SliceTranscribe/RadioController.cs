using System.Net.Sockets;
using System.Text;

namespace SliceTranscribe;

internal static class RadioController
{
    private const string Host = "127.0.0.1";
    private const int Port = 4212;

    public static Task<bool> PauseAsync(
        CancellationToken cancellationToken = default)
    {
        return SendAsync(
            "pause",
            cancellationToken);
    }

    public static Task<bool> ResumeAsync(
        CancellationToken cancellationToken = default)
    {
        // VLC RC's "pause" command is a state toggle. Using "play" after
        // pausing a network stream did not reliably resume it on VLC 3.0.24.
        return SendAsync(
            "pause",
            cancellationToken);
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
