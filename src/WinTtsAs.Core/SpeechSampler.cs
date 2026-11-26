using System.Speech.Synthesis;

namespace WinTtsAs.Core;

public static class SpeechSampler
{
    public const string DefaultSentence =
        "Hello from winTTSas. This is a sample of the selected Windows voice.";

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
}
