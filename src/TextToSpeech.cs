using System.Speech.AudioFormat;
using System.Speech.Synthesis;

namespace VoiceFx;

/// <summary>Turns text into mono float samples using the voices built into Windows.</summary>
public static class TextToSpeech
{
    const int SynthRate = 24000;

    /// <summary>English voices labelled "Male"/"Female" (numbered if there are several).</summary>
    public static List<VoiceItem> EnglishVoices()
    {
        using var synth = new SpeechSynthesizer();
        var voices = synth.GetInstalledVoices()
            .Where(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en")
            .Select(v => (v.VoiceInfo.Name, Label: v.VoiceInfo.Gender switch
            {
                VoiceGender.Male => "Male",
                VoiceGender.Female => "Female",
                _ => "Voice",
            }))
            .OrderBy(v => v.Label switch { "Male" => 0, "Female" => 1, _ => 2 })
            .ToList();

        return voices.Select(v =>
        {
            int total = voices.Count(x => x.Label == v.Label);
            int index = voices.Where(x => x.Label == v.Label).ToList().IndexOf(v) + 1;
            return new VoiceItem(v.Name, total > 1 ? $"{v.Label} {index}" : v.Label);
        }).ToList();
    }

    public static float[] Synthesize(string text, string? voice, int sampleRate)
    {
        using var synth = new SpeechSynthesizer();
        if (voice != null) synth.SelectVoice(voice);
        using var stream = new MemoryStream();
        synth.SetOutputToAudioStream(stream,
            new SpeechAudioFormatInfo(SynthRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synth.Speak(text);

        var bytes = stream.ToArray();
        var samples = new float[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        return Resample(samples, SynthRate, sampleRate);
    }

    static float[] Resample(float[] src, int from, int to)
    {
        if (from == to || src.Length == 0) return src;
        var dst = new float[(int)((long)src.Length * to / from)];
        double step = (double)from / to;
        for (int i = 0; i < dst.Length; i++)
        {
            double p = i * step;
            int i0 = (int)p;
            float frac = (float)(p - i0);
            float a = src[i0];
            float b = i0 + 1 < src.Length ? src[i0 + 1] : a;
            dst[i] = a + (b - a) * frac;
        }
        return dst;
    }
}

public sealed record VoiceItem(string Name, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Holds the speech currently being played and mixes it into the audio stream,
/// either before the effects (so it gets the preset's voice) or after them.
/// </summary>
public sealed class SpeechQueue
{
    readonly object gate = new();
    float[] samples = Array.Empty<float>();
    int pos;
    bool throughEffects;
    bool muteMic;

    public bool IsSpeaking { get { lock (gate) return pos < samples.Length; } }

    public void Play(float[] speech, bool throughEffects, bool muteMic)
    {
        lock (gate)
        {
            samples = speech;
            pos = 0;
            this.throughEffects = throughEffects;
            this.muteMic = muteMic;
        }
    }

    public void Stop()
    {
        lock (gate) pos = samples.Length;
    }

    // Called on the audio thread for every block: once before and once after the effects.
    internal void Mix(float[] buffer, int offset, int count, bool beforeEffects)
    {
        lock (gate)
        {
            if (pos >= samples.Length) return;
            if (beforeEffects && muteMic) Array.Clear(buffer, offset, count);
            if (beforeEffects != throughEffects) return;
            int n = Math.Min(count, samples.Length - pos);
            for (int i = 0; i < n; i++) buffer[offset + i] += samples[pos + i];
            pos += n;
        }
    }
}
