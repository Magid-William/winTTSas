using System.Text;

namespace WinTtsAs.Core;

public static class WavConcatenator
{
    public static byte[] Concat(IReadOnlyList<byte[]> wavFiles)
    {
        if (wavFiles.Count == 0)
        {
            throw new ArgumentException("At least one WAV file is required.", nameof(wavFiles));
        }

        if (wavFiles.Count == 1)
        {
            return wavFiles[0];
        }

        byte[]? format = null;
        using var data = new MemoryStream();

        foreach (var wav in wavFiles)
        {
            var (fmt, pcm) = Parse(wav);
            if (format is null)
            {
                format = fmt;
            }
            else if (!format.AsSpan().SequenceEqual(fmt))
            {
                throw new InvalidDataException("Cannot concatenate WAV chunks with different formats.");
            }

            data.Write(pcm);
        }

        return Build(format!, data.ToArray());
    }

    /// <summary>
    /// Adds silence before the audio. Useful when an output device (Bluetooth
    /// headsets in particular) takes a moment to wake and clips the first
    /// phonemes of a short clip.
    /// </summary>
    public static byte[] PrependSilence(byte[] wav, int milliseconds)
    {
        if (milliseconds <= 0 || wav.Length == 0)
        {
            return wav;
        }

        var (format, data) = Parse(wav);
        if (format.Length < 12)
        {
            return wav;
        }

        var bytesPerSecond = BitConverter.ToUInt32(format, 8);
        if (bytesPerSecond == 0)
        {
            return wav;
        }

        var silence = (int)Math.Min(int.MaxValue, (long)bytesPerSecond * milliseconds / 1000);
        if (silence <= 0)
        {
            return wav;
        }

        var combined = new byte[silence + data.Length];
        data.CopyTo(combined, silence);
        return Build(format, combined);
    }

    private static (byte[] Format, byte[] Data) Parse(byte[] wav)
    {
        if (wav.Length < 12 ||
            Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        byte[]? format = null;
        byte[]? data = null;
        var offset = 12;

        while (offset + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, offset, 4);
            var size = BitConverter.ToInt32(wav, offset + 4);
            if (size < 0)
            {
                throw new InvalidDataException("Invalid WAV chunk size.");
            }

            var start = offset + 8;
            if (start + size > wav.Length)
            {
                throw new InvalidDataException("Truncated WAV chunk.");
            }

            if (id == "fmt ")
            {
                format = wav.AsSpan(start, size).ToArray();
            }
            else if (id == "data")
            {
                data = wav.AsSpan(start, size).ToArray();
            }

            offset = start + size + (size & 1);
        }

        if (format is null || data is null)
        {
            throw new InvalidDataException("WAV file is missing fmt or data.");
        }

        return (format, data);
    }

    private static byte[] Build(byte[] format, byte[] data)
    {
        var padding = data.Length & 1;
        var riffSize = 4 + 8 + format.Length + 8 + data.Length + padding;

        using var stream = new MemoryStream(12 + riffSize);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(riffSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(format.Length);
        writer.Write(format);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(data.Length);
        writer.Write(data);

        if (padding != 0)
        {
            writer.Write((byte)0);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
