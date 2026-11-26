using System.Runtime.InteropServices;
using System.Speech.Synthesis;

namespace WinTtsAs.Core;

public static class SpeechSampler
{
    public const string DefaultSentence =
        "Hello from winTTSas. This is a sample of the selected Windows voice.";

    private const uint SndFilename = 0x00020000;
    private const uint SndNoDefault = 0x00000010;

    public static byte[] SynthesizeWav(string voiceName, string text, int rate, int volume)
    {
        return StaRunner.Run(() =>
        {
            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(voiceName);
            synth.Rate = Math.Clamp(rate, -10, 10);
            synth.Volume = Math.Clamp(volume, 0, 100);

            using var wave = new MemoryStream();
            synth.SetOutputToWaveStream(wave);
            try
            {
                synth.Speak(text);
            }
            finally
            {
                try
                {
                    synth.SetOutputToNull();
                }
                catch
                {
                    // best effort
                }
            }

            return wave.ToArray();
        });
    }

    public static void PlayWav(string path)
    {
        if (!PlaySound(path, IntPtr.Zero, SndFilename | SndNoDefault))
        {
            throw new InvalidOperationException("could not play the sample audio");
        }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);
}
