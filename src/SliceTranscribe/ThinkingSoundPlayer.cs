using NAudio.Wave;

namespace SliceTranscribe;

internal sealed class ThinkingSoundPlayer :
    IDisposable
{
    private readonly WaveOutEvent _output;

    private ThinkingSoundPlayer()
    {
        _output =
            new WaveOutEvent();

        _output.Init(
            new LoopingThinkingProvider());

        _output.Play();
    }

    public static ThinkingSoundPlayer Start()
    {
        return new ThinkingSoundPlayer();
    }

    public void Dispose()
    {
        try
        {
            _output.Stop();
        }
        catch
        {
        }

        _output.Dispose();
    }

    private sealed class LoopingThinkingProvider :
        IWaveProvider
    {
        private const int SampleRate =
            48000;

        private static readonly byte[] Loop =
            BuildLoop();

        private int _offset;

        public WaveFormat WaveFormat { get; } =
            new(
                SampleRate,
                16,
                1);

        public int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            int written =
                0;

            while (written <
                   count)
            {
                int available =
                    Loop.Length -
                    _offset;

                int take =
                    Math.Min(
                        available,
                        count -
                        written);

                Buffer.BlockCopy(
                    Loop,
                    _offset,
                    buffer,
                    offset +
                        written,
                    take);

                written +=
                    take;

                _offset +=
                    take;

                if (_offset >=
                    Loop.Length)
                {
                    _offset =
                        0;
                }
            }

            return written;
        }

        private static byte[] BuildLoop()
        {
            const double loopSeconds =
                1.8;

            const double pulseSeconds =
                0.16;

            int samples =
                (int)(
                    SampleRate *
                    loopSeconds);

            byte[] bytes =
                new byte[
                    samples *
                    2];

            for (int i = 0;
                 i < samples;
                 i++)
            {
                double t =
                    i /
                    (double)SampleRate;

                double sample =
                    0;

                if (t <
                    pulseSeconds)
                {
                    double phase =
                        t /
                        pulseSeconds;

                    double envelope =
                        Math.Sin(
                            Math.PI *
                            phase);

                    envelope *=
                        envelope;

                    double tone =
                        Math.Sin(
                            2 *
                            Math.PI *
                            523.25 *
                            t) +
                        0.35 *
                        Math.Sin(
                            2 *
                            Math.PI *
                            659.25 *
                            t);

                    sample =
                        0.10 *
                        envelope *
                        tone;
                }

                short value =
                    (short)Math.Clamp(
                        Math.Round(
                            sample *
                            short.MaxValue),
                        short.MinValue,
                        short.MaxValue);

                bytes[i * 2] =
                    (byte)(
                        value &
                        0xff);

                bytes[(i * 2) + 1] =
                    (byte)(
                        (value >>
                         8) &
                        0xff);
            }

            return bytes;
        }
    }
}
