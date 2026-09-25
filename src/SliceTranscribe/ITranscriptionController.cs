namespace SliceTranscribe;

internal interface ITranscriptionController :
    IAsyncDisposable
{
    bool Enabled { get; }

    string? TranscriptPath { get; }

    Task<string?> StartAsync(
        string wavPath,
        CancellationToken cancellationToken = default);

    Task<string?> CommitAsync(
        CancellationToken cancellationToken = default);

    Task<string?> FinishAsync(
        CancellationToken cancellationToken = default);
}
