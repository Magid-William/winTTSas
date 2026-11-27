using System.Runtime.InteropServices;

namespace WinTtsAs.Core;

public sealed record WaveOutDevice(int Index, string Name);

public static class WaveOutPlayer
{
    private const uint WhdrDone = 0x00000001;
    private const uint CallbackNull = 0;
    private const int PlaybackTimeoutMs = 60_000;

    public static IReadOnlyList<WaveOutDevice> GetDevices()
    {
        var devices = new List<WaveOutDevice>();
        var count = waveOutGetNumDevs();

        for (uint index = 0; index < count; index++)
        {
            var result = waveOutGetDevCaps(
                new UIntPtr(index),
                out var caps,
                (uint)Marshal.SizeOf<WaveOutCaps>());

            if (result == 0)
            {
                devices.Add(new WaveOutDevice((int)index, caps.szPname));
            }
        }

        return devices;
    }

    public static void Play(
        byte[] wav,
        int? deviceIndex = null,
        CancellationToken cancellationToken = default)
    {
        var (format, data) = ParseWav(wav);
        if (format.wFormatTag != 1)
        {
            throw new InvalidOperationException(
                "Only PCM WAV playback is supported by the sampler.");
        }

        // Long selections can be minutes of audio; scale the guard with the clip.
        var durationMs = format.nAvgBytesPerSec == 0
            ? 0
            : (long)Math.Ceiling(data.Length * 1000.0 / format.nAvgBytesPerSec);
        var timeoutMs = Math.Max(PlaybackTimeoutMs, (durationMs * 2) + 30_000);

        var device = deviceIndex is null
            ? new UIntPtr(nuint.MaxValue)
            : new UIntPtr((uint)deviceIndex.Value);

        var result = waveOutOpen(
            out var handle,
            device,
            ref format,
            IntPtr.Zero,
            IntPtr.Zero,
            CallbackNull);

        if (result != 0)
        {
            throw new InvalidOperationException($"waveOutOpen failed ({result}).");
        }

        IntPtr dataPointer = IntPtr.Zero;
        IntPtr headerPointer = IntPtr.Zero;

        try
        {
            dataPointer = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, dataPointer, data.Length);

            var headerSize = (uint)Marshal.SizeOf<WaveHdr>();
            headerPointer = Marshal.AllocHGlobal((int)headerSize);
            Marshal.StructureToPtr(
                new WaveHdr
                {
                    lpData = dataPointer,
                    dwBufferLength = (uint)data.Length
                },
                headerPointer,
                fDeleteOld: false);

            result = waveOutPrepareHeader(handle, headerPointer, headerSize);
            if (result != 0)
            {
                throw new InvalidOperationException($"waveOutPrepareHeader failed ({result}).");
            }

            result = waveOutWrite(handle, headerPointer, headerSize);
            if (result != 0)
            {
                throw new InvalidOperationException($"waveOutWrite failed ({result}).");
            }

            var deadline = Environment.TickCount64 + timeoutMs;
            var cancelled = false;
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    waveOutReset(handle);
                    cancelled = true;
                    break;
                }

                var header = Marshal.PtrToStructure<WaveHdr>(headerPointer);
                if ((header.dwFlags & WhdrDone) != 0)
                {
                    break;
                }

                if (Environment.TickCount64 > deadline)
                {
                    waveOutReset(handle);
                    throw new TimeoutException("Timed out while playing the sample.");
                }

                Thread.Sleep(20);
            }

            waveOutUnprepareHeader(handle, headerPointer, headerSize);

            if (cancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        finally
        {
            waveOutClose(handle);

            if (headerPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(headerPointer);
            }

            if (dataPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(dataPointer);
            }
        }
    }

    private static (WaveFormatEx Format, byte[] Data) ParseWav(byte[] wav)
    {
        if (wav.Length < 12 ||
            wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' ||
            wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        byte[]? formatBytes = null;
        byte[]? data = null;
        var offset = 12;

        while (offset + 8 <= wav.Length)
        {
            var id = "" +
                     (char)wav[offset] +
                     (char)wav[offset + 1] +
                     (char)wav[offset + 2] +
                     (char)wav[offset + 3];
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
                formatBytes = wav.AsSpan(start, size).ToArray();
            }
            else if (id == "data")
            {
                data = wav.AsSpan(start, size).ToArray();
            }

            offset = start + size + (size & 1);
        }

        if (formatBytes is null || formatBytes.Length < 16 || data is null)
        {
            throw new InvalidDataException("WAV file is missing fmt or data.");
        }

        var format = new WaveFormatEx
        {
            wFormatTag = BitConverter.ToUInt16(formatBytes, 0),
            nChannels = BitConverter.ToUInt16(formatBytes, 2),
            nSamplesPerSec = BitConverter.ToUInt32(formatBytes, 4),
            nAvgBytesPerSec = BitConverter.ToUInt32(formatBytes, 8),
            nBlockAlign = BitConverter.ToUInt16(formatBytes, 12),
            wBitsPerSample = BitConverter.ToUInt16(formatBytes, 14),
            cbSize = formatBytes.Length >= 18
                ? BitConverter.ToUInt16(formatBytes, 16)
                : (ushort)0
        };

        return (format, data);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;

        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint waveOutGetDevCaps(
        UIntPtr uDeviceID,
        out WaveOutCaps pwoc,
        uint cbwoc);

    [DllImport("winmm.dll")]
    private static extern uint waveOutOpen(
        out IntPtr hWaveOut,
        UIntPtr uDeviceID,
        ref WaveFormatEx lpFormat,
        IntPtr dwCallback,
        IntPtr dwInstance,
        uint dwFlags);

    [DllImport("winmm.dll")]
    private static extern uint waveOutPrepareHeader(
        IntPtr hwo,
        IntPtr pwh,
        uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveOutWrite(
        IntPtr hwo,
        IntPtr pwh,
        uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveOutUnprepareHeader(
        IntPtr hwo,
        IntPtr pwh,
        uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll")]
    private static extern uint waveOutClose(IntPtr hwo);
}
