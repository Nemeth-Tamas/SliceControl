using NAudio.Wave;

namespace SliceTranscribe;

internal static class ChannelAnalyzer
{
    public static void Run(
        string wavPath)
    {
        if (!File.Exists(
            wavPath))
        {
            throw new FileNotFoundException(
                "WAV was not found.",
                wavPath);
        }

        using var reader =
            new WaveFileReader(
                wavPath);

        ISampleProvider source =
            reader.ToSampleProvider();

        int channels =
            source.WaveFormat.Channels;

        Console.WriteLine(
            $"Source: {reader.WaveFormat}");

        if (channels <= 1)
        {
            Console.WriteLine(
                "Only one channel is present.");
            return;
        }

        var sumSquares =
            new double[
                channels];

        var peaks =
            new double[
                channels];

        var sums =
            new double[
                channels];

        var pairProducts =
            new double[
                channels,
                channels];

        long frames =
            0;

        int blockFrames =
            4096;

        var buffer =
            new float[
                blockFrames *
                channels];

        while (true)
        {
            int read =
                source.Read(
                    buffer,
                    0,
                    buffer.Length);

            if (read <= 0)
            {
                break;
            }

            int frameCount =
                read /
                channels;

            for (int frame = 0;
                 frame < frameCount;
                 frame++)
            {
                int offset =
                    frame *
                    channels;

                for (int channel = 0;
                     channel < channels;
                     channel++)
                {
                    double sample =
                        buffer[
                            offset +
                            channel];

                    sums[channel] +=
                        sample;

                    sumSquares[channel] +=
                        sample *
                        sample;

                    peaks[channel] =
                        Math.Max(
                            peaks[channel],
                            Math.Abs(
                                sample));
                }

                for (int left = 0;
                     left < channels;
                     left++)
                {
                    double leftSample =
                        buffer[
                            offset +
                            left];

                    for (int right = left;
                         right < channels;
                         right++)
                    {
                        pairProducts[
                            left,
                            right] +=
                                leftSample *
                                buffer[
                                    offset +
                                    right];
                    }
                }
            }

            frames +=
                frameCount;
        }

        if (frames == 0)
        {
            Console.WriteLine(
                "No audio frames found.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Frames: {frames:N0}");

        Console.WriteLine();
        Console.WriteLine(
            "Per-channel level:");

        for (int channel = 0;
             channel < channels;
             channel++)
        {
            double rms =
                Math.Sqrt(
                    sumSquares[channel] /
                    frames);

            Console.WriteLine(
                $"CH{channel}: RMS={rms:0.000000}  peak={peaks[channel]:0.000000}");
        }

        Console.WriteLine();
        Console.WriteLine(
            "Pearson correlation:");

        Console.Write(
            "      ");

        for (int channel = 0;
             channel < channels;
             channel++)
        {
            Console.Write(
                $" CH{channel,7}");
        }

        Console.WriteLine();

        for (int left = 0;
             left < channels;
             left++)
        {
            Console.Write(
                $"CH{left,-3}  ");

            for (int right = 0;
                 right < channels;
                 right++)
            {
                int a =
                    Math.Min(
                        left,
                        right);

                int b =
                    Math.Max(
                        left,
                        right);

                double numerator =
                    pairProducts[
                        a,
                        b] -
                    (
                        sums[left] *
                        sums[right] /
                        frames
                    );

                double leftVariance =
                    sumSquares[left] -
                    (
                        sums[left] *
                        sums[left] /
                        frames
                    );

                double rightVariance =
                    sumSquares[right] -
                    (
                        sums[right] *
                        sums[right] /
                        frames
                    );

                double denominator =
                    Math.Sqrt(
                        Math.Max(
                            0,
                            leftVariance) *
                        Math.Max(
                            0,
                            rightVariance));

                double correlation =
                    denominator > 0
                        ? numerator /
                          denominator
                        : 0;

                Console.Write(
                    $" {correlation,8:0.000000}");
            }

            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine(
            "Interpretation:");

        Console.WriteLine(
            "  ~1.000 correlation = channels are effectively the same waveform.");

        Console.WriteLine(
            "  Lower correlation = useful independent spatial information may exist.");
    }
}
