using Windows.Media.Control;

namespace SliceTranscribe;

internal static class MediaControlProbe
{
    public static async Task<int> ListAsync()
    {
        try
        {
            GlobalSystemMediaTransportControlsSessionManager manager =
                await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions =
                manager.GetSessions();

            if (sessions.Count == 0)
            {
                Console.WriteLine(
                    "No Windows global media-control sessions were found.");

                return 1;
            }

            GlobalSystemMediaTransportControlsSession? current =
                manager.GetCurrentSession();

            Console.WriteLine(
                $"Media sessions: {sessions.Count}");

            for (int i = 0; i < sessions.Count; i++)
            {
                GlobalSystemMediaTransportControlsSession session =
                    sessions[i];

                GlobalSystemMediaTransportControlsSessionPlaybackInfo playback =
                    session.GetPlaybackInfo();

                GlobalSystemMediaTransportControlsSessionPlaybackControls controls =
                    playback.Controls;

                string title =
                    string.Empty;

                string artist =
                    string.Empty;

                try
                {
                    GlobalSystemMediaTransportControlsSessionMediaProperties media =
                        await session.TryGetMediaPropertiesAsync();

                    title =
                        media.Title ?? string.Empty;

                    artist =
                        media.Artist ?? string.Empty;
                }
                catch
                {
                }

                bool isCurrent =
                    ReferenceEquals(
                        current,
                        session) ||
                    (
                        current is not null &&
                        string.Equals(
                            current.SourceAppUserModelId,
                            session.SourceAppUserModelId,
                            StringComparison.OrdinalIgnoreCase));

                Console.WriteLine(
                    $"  [{i}] {(isCurrent ? "* " : "  ")}{session.SourceAppUserModelId}");

                Console.WriteLine(
                    $"      Status : {playback.PlaybackStatus}");

                Console.WriteLine(
                    $"      Control: play={controls.IsPlayEnabled}, pause={controls.IsPauseEnabled}, toggle={controls.IsPlayPauseToggleEnabled}");

                if (!string.IsNullOrWhiteSpace(
                    title) ||
                    !string.IsNullOrWhiteSpace(
                    artist))
                {
                    Console.WriteLine(
                        $"      Media  : {artist} - {title}".TrimEnd());
                }
            }

            return 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine(
                $"Global media-control access was denied: {ex.Message}");

            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Could not enumerate global media sessions: {ex.GetType().Name}: {ex.Message}");

            return 1;
        }
    }
}
