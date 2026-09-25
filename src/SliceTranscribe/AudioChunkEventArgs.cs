namespace SliceTranscribe;

internal sealed class AudioChunkEventArgs : EventArgs
{
    public AudioChunkEventArgs(byte[] data)
    {
        Data = data;
    }

    public byte[] Data { get; }
}
