namespace SliceTranscribe;

internal sealed class DisabledTranscriptionController :
    ITranscriptionController
{
    public bool Enabled => false;

    public string? TranscriptPath => null;

    public Task<string?> StartAsync(
        string wavPath,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(
            null);
    }

    public Task<string?> CommitAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(
            null);
    }

    public Task<string?> FinishAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(
            null);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
