using NAudio.Wave;

namespace VoiceFx;

/// <summary>Effect parameters. Written by the UI thread, read by the audio thread.</summary>
public sealed class EffectSettings
{
    public bool Bypass { get; set; }

    public bool PitchEnabled { get; set; }
    public float PitchSemitones { get; set; } = 5;

    public bool EchoEnabled { get; set; }
    public float EchoDelayMs { get; set; } = 250;
    public float EchoFeedback { get; set; } = 0.35f;
    public float EchoMix { get; set; } = 0.4f;

    public bool ReverbEnabled { get; set; }
    public float ReverbRoom { get; set; } = 0.8f;
    public float ReverbMix { get; set; } = 0.3f;

    public bool RobotEnabled { get; set; }
    public float RobotFreq { get; set; } = 40;
    public float RobotMetal { get; set; } = 0.75f;
    public float RobotToneMs { get; set; } = 6;

    public float GainDb { get; set; }

    /// <summary>Copies every effect setting except Bypass.</summary>
    public void CopyFrom(EffectSettings o)
    {
        PitchEnabled = o.PitchEnabled; PitchSemitones = o.PitchSemitones;
        EchoEnabled = o.EchoEnabled; EchoDelayMs = o.EchoDelayMs; EchoFeedback = o.EchoFeedback; EchoMix = o.EchoMix;
        ReverbEnabled = o.ReverbEnabled; ReverbRoom = o.ReverbRoom; ReverbMix = o.ReverbMix;
        RobotEnabled = o.RobotEnabled; RobotFreq = o.RobotFreq; RobotMetal = o.RobotMetal; RobotToneMs = o.RobotToneMs;
        GainDb = o.GainDb;
    }
}

/// <summary>
/// Ring buffer of the latest samples. Written by the audio thread, read by the UI;
/// a read that overlaps a write only shifts the picture by a sample or two.
/// </summary>
public sealed class WaveTap
{
    readonly float[] buf = new float[16384];
    int write;

    public void Write(float x)
    {
        buf[write] = x;
        write = (write + 1) & (buf.Length - 1);
    }

    /// <summary>Copies the newest <paramref name="count"/> samples, oldest first.</summary>
    public void ReadLatest(float[] dst, int count)
    {
        int w = Volatile.Read(ref write);
        for (int i = 0; i < count; i++)
            dst[i] = buf[(w - count + i) & (buf.Length - 1)];
    }
}

/// <summary>A user-made preset, saved in the settings file.</summary>
public sealed class CustomPreset
{
    public string Name { get; set; } = "";
    public EffectSettings Effects { get; set; } = new();
}

/// <summary>Runs a mono signal through pitch -> robot -> echo -> reverb -> gain.</summary>
public sealed class EffectChain : ISampleProvider
{
    readonly ISampleProvider source;
    readonly EffectSettings s;
    readonly PitchShifter pitch;
    readonly RingModulator robot;
    readonly Echo echo;
    readonly Reverb reverb;
    bool wasPitch, wasEcho, wasReverb;

    /// <summary>Peak level (0..1) of the last processed block, for the level meter.</summary>
    public float Peak { get; private set; }

    /// <summary>Recent samples before and after the effects, for the waveform display.</summary>
    public WaveTap DryTap { get; } = new();
    public WaveTap WetTap { get; } = new();

    public EffectChain(ISampleProvider source, EffectSettings settings)
    {
        this.source = source;
        s = settings;
        int fs = source.WaveFormat.SampleRate;
        pitch = new PitchShifter(fs);
        robot = new RingModulator(fs);
        echo = new Echo(fs);
        reverb = new Reverb(fs);
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int n = source.Read(buffer, offset, count);

        // Snapshot settings once per block so the UI can't change them mid-loop.
        bool bypass = s.Bypass;
        bool doPitch = !bypass && s.PitchEnabled;
        bool doRobot = !bypass && s.RobotEnabled;
        bool doEcho = !bypass && s.EchoEnabled;
        bool doReverb = !bypass && s.ReverbEnabled;
        float ratio = MathF.Pow(2f, s.PitchSemitones / 12f);
        float robotFreq = s.RobotFreq, robotMetal = s.RobotMetal, robotTone = s.RobotToneMs;
        float echoDelay = s.EchoDelayMs, echoFb = s.EchoFeedback, echoMix = s.EchoMix;
        float room = s.ReverbRoom, reverbMix = s.ReverbMix;
        float gain = bypass ? 1f : MathF.Pow(10f, s.GainDb / 20f);

        // Drop stale audio from delay lines when an effect is switched back on.
        if (doPitch && !wasPitch) pitch.Clear();
        if (doEcho && !wasEcho) echo.Clear();
        if (doReverb && !wasReverb) reverb.Clear();
        wasPitch = doPitch; wasEcho = doEcho; wasReverb = doReverb;

        float peak = 0;
        for (int i = offset; i < offset + n; i++)
        {
            float x = buffer[i];
            DryTap.Write(x);
            if (doPitch) x = pitch.Process(x, ratio);
            if (doRobot) x = robot.Process(x, robotFreq, robotMetal, robotTone);
            if (doEcho) x = echo.Process(x, echoDelay, echoFb, echoMix);
            if (doReverb) x = reverb.Process(x, room, reverbMix);
            x = SoftClip(x * gain);
            WetTap.Write(x);
            buffer[i] = x;
            float a = MathF.Abs(x);
            if (a > peak) peak = a;
        }
        Peak = peak;
        return n;
    }

    static float SoftClip(float x)
    {
        const float knee = 0.9f;
        float a = MathF.Abs(x);
        if (a <= knee) return x;
        return MathF.Sign(x) * (knee + (1 - knee) * MathF.Tanh((a - knee) / (1 - knee)));
    }
}

/// <summary>
/// Low-latency delay-line pitch shifter: two read taps sweep through a short window
/// at a speed set by the pitch ratio, crossfaded so the jumps are inaudible.
/// </summary>
sealed class PitchShifter
{
    readonly float[] buf;
    readonly int mask;
    readonly float window;
    int write;
    float phase;

    public PitchShifter(int sampleRate)
    {
        window = sampleRate * 0.05f; // 50 ms grains
        int size = 1;
        while (size < window * 2) size <<= 1;
        buf = new float[size];
        mask = size - 1;
    }

    public void Clear() => Array.Clear(buf);

    public float Process(float x, float ratio)
    {
        buf[write] = x;
        phase += (1f - ratio) / window;
        phase -= MathF.Floor(phase);
        float phase2 = phase + 0.5f;
        if (phase2 >= 1f) phase2 -= 1f;
        float y = Tap(phase) * Fade(phase) + Tap(phase2) * Fade(phase2);
        write = (write + 1) & mask;
        return y;
    }

    float Tap(float p)
    {
        float r = write - (p * window + 1f);
        if (r < 0) r += buf.Length;
        int i0 = (int)r;
        float frac = r - i0;
        return buf[i0 & mask] * (1 - frac) + buf[(i0 + 1) & mask] * frac;
    }

    // sin^2 fades of two taps half a window apart always sum to 1.
    static float Fade(float p)
    {
        float s = MathF.Sin(MathF.PI * p);
        return s * s;
    }
}

/// <summary>
/// Ring modulator followed by a resonant comb filter. The comb's short, high-feedback
/// delay rings like a metal tube, which gives the "tin can" robot sound.
/// </summary>
sealed class RingModulator
{
    readonly float twoPiOverFs;
    readonly int fs;
    readonly float[] comb;
    int write;
    float phase;

    public RingModulator(int sampleRate)
    {
        fs = sampleRate;
        twoPiOverFs = 2 * MathF.PI / sampleRate;
        comb = new float[sampleRate / 20]; // up to 50 ms
    }

    public float Process(float x, float freq, float metal, float toneMs)
    {
        phase += freq * twoPiOverFs;
        if (phase > 2 * MathF.PI) phase -= 2 * MathF.PI;
        // Frequency 0 = no chopping, only the metallic resonance.
        float y = freq > 0 ? x * MathF.Sin(phase) : x;

        float feedback = metal * 0.92f;
        int d = Math.Clamp((int)(toneMs * fs / 1000f), 1, comb.Length - 1);
        int r = write - d;
        if (r < 0) r += comb.Length;
        y += comb[r] * feedback;
        comb[write] = y;
        if (++write == comb.Length) write = 0;
        // Resonance boosts the level; pull it back so the meter stays similar.
        return y * (1 - feedback * 0.75f);
    }
}

sealed class Echo
{
    readonly float[] buf;
    readonly int fs;
    int write;

    public Echo(int sampleRate)
    {
        fs = sampleRate;
        buf = new float[sampleRate * 2];
    }

    public void Clear() => Array.Clear(buf);

    public float Process(float x, float delayMs, float feedback, float mix)
    {
        int d = Math.Clamp((int)(delayMs * fs / 1000f), 1, buf.Length - 1);
        int r = write - d;
        if (r < 0) r += buf.Length;
        float delayed = buf[r];
        buf[write] = x + delayed * feedback;
        if (++write == buf.Length) write = 0;
        return x + delayed * mix;
    }
}

/// <summary>Small Freeverb-style reverb: parallel damped combs into series allpasses.</summary>
sealed class Reverb
{
    readonly Comb[] combs;
    readonly AllPass[] allPasses;

    public Reverb(int sampleRate)
    {
        float k = sampleRate / 44100f;
        combs = new[] { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 }
            .Select(n => new Comb((int)(n * k))).ToArray();
        allPasses = new[] { 556, 441, 341, 225 }
            .Select(n => new AllPass((int)(n * k))).ToArray();
    }

    public void Clear()
    {
        foreach (var c in combs) c.Clear();
        foreach (var a in allPasses) a.Clear();
    }

    public float Process(float x, float room, float mix)
    {
        float feedback = 0.7f + room * 0.28f;
        float input = x * 0.015f;
        float wet = 0;
        foreach (var c in combs) wet += c.Process(input, feedback, 0.25f);
        foreach (var a in allPasses) wet = a.Process(wet);
        return x * (1 - mix * 0.5f) + wet * 3f * mix;
    }

    sealed class Comb(int length)
    {
        readonly float[] buf = new float[length];
        int i;
        float store;

        public void Clear() { Array.Clear(buf); store = 0; }

        public float Process(float x, float feedback, float damp)
        {
            float y = buf[i];
            store = y * (1 - damp) + store * damp;
            buf[i] = x + store * feedback;
            if (++i == buf.Length) i = 0;
            return y;
        }
    }

    sealed class AllPass(int length)
    {
        readonly float[] buf = new float[length];
        int i;

        public void Clear() => Array.Clear(buf);

        public float Process(float x)
        {
            float b = buf[i];
            buf[i] = x + b * 0.5f;
            if (++i == buf.Length) i = 0;
            return b - x;
        }
    }
}
