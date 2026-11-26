using System.Speech.Synthesis;

namespace WinTtsAs.Core;

public static class SpeechSampler
{
    public const string DefaultSentence =
        "Hello from winTTSas. This is a sample of the selected Windows voice.";

    public static void Speak(string voiceName, string text, int rate, int volume)
    {
        StaRunner.Run(() =>
        {
            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(voiceName);
            synth.Rate = Math.Clamp(rate, -10, 10);
            synth.Volume = Math.Clamp(volume, 0, 100);
            synth.SetOutputToDefaultAudioDevice();
            synth.Speak(text);
            return true;
        });
    }
}
