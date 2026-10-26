using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceFx;

/// <summary>
/// Captures the microphone, runs it through the effect chain and plays the result
/// into an output device (normally VB-Cable's "CABLE Input", which Teams then sees
/// as the microphone "CABLE Output"). Optionally mirrors it to a monitor device.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
    static readonly TimeSpan MaxBuffered = TimeSpan.FromMilliseconds(120);

    readonly EffectSettings settings;
    WasapiCapture? capture;
    WasapiOut? output;
    WasapiOut? monitor;
    BufferedWaveProvider? inBuffer;
    EffectChain? chain;
    byte[] scratch = Array.Empty<byte>();

    /// <summary>Raised (on an audio thread) when a device fails while running.</summary>
    public event Action<Exception>? Failed;

    public AudioEngine(EffectSettings settings) => this.settings = settings;

    public bool IsRunning => capture != null;
    public float Peak => chain?.Peak ?? 0;
    public EffectChain? Chain => chain;
    public int SampleRate { get; private set; }
    public SpeechQueue Speech { get; } = new();

    public static List<DeviceItem> ListDevices(DataFlow flow)
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new DeviceItem(d.ID, d.FriendlyName))
            .ToList();
    }

    public static string? DefaultDeviceId(DataFlow flow)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.GetDefaultAudioEndpoint(flow, Role.Communications).ID;
        }
        catch { return null; }
    }

    public void Start(string inputId, string outputId, string? monitorId)
    {
        Stop();
        using var en = new MMDeviceEnumerator();

        capture = new WasapiCapture(en.GetDevice(inputId), true, 10);
        SampleRate = capture.WaveFormat.SampleRate;
        var monoFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);
        inBuffer = NewBuffer(monoFormat);
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += (_, e) => { if (e.Exception != null) Failed?.Invoke(e.Exception); };

        // mic -> speech (if through effects) -> effects -> speech (if not) -> outputs
        chain = new EffectChain(new SpeechMixProvider(inBuffer.ToSampleProvider(), Speech, true), settings);
        ISampleProvider processed = new SpeechMixProvider(chain, Speech, false);

        if (monitorId != null)
        {
            var monBuffer = NewBuffer(monoFormat);
            processed = new TeeSampleProvider(processed, monBuffer);
            monitor = CreateOutput(en.GetDevice(monitorId), monBuffer.ToSampleProvider());
        }
        output = CreateOutput(en.GetDevice(outputId), processed);

        capture.StartRecording();
        output.Play();
        monitor?.Play();
    }

    public void Stop()
    {
        var c = capture;
        capture = null;
        if (c != null)
        {
            c.DataAvailable -= OnDataAvailable;
            try { c.StopRecording(); } catch { }
            c.Dispose();
        }
        output?.Dispose();
        output = null;
        monitor?.Dispose();
        monitor = null;
        inBuffer = null;
        chain = null;
        Speech.Stop();
    }

    public void Dispose() => Stop();

    static BufferedWaveProvider NewBuffer(WaveFormat format) => new(format)
    {
        BufferDuration = TimeSpan.FromSeconds(1),
        DiscardOnBufferOverflow = true,
        ReadFully = true,
    };

    WasapiOut CreateOutput(MMDevice device, ISampleProvider mono)
    {
        // Match the device's shared-mode mix format so Windows accepts the stream as-is.
        var mix = device.AudioClient.MixFormat;
        ISampleProvider p = mono;
        if (mix.SampleRate != p.WaveFormat.SampleRate)
            p = new WdlResamplingSampleProvider(p, mix.SampleRate);
        p = new MonoToMultiChannelProvider(p, mix.Channels);

        var o = new WasapiOut(device, AudioClientShareMode.Shared, true, 30);
        o.PlaybackStopped += (_, e) => { if (e.Exception != null) Failed?.Invoke(e.Exception); };
        o.Init(p);
        return o;
    }

    // Convert whatever the mic delivers (16/24/32-bit, any channel count) to mono float.
    void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var c = capture;
        var buffer = inBuffer;
        if (c == null || buffer == null) return;

        var f = c.WaveFormat;
        int ch = f.Channels;
        int bytesPerSample = f.BitsPerSample / 8;
        int frameBytes = bytesPerSample * ch;
        int frames = e.BytesRecorded / frameBytes;
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat
            || (f is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);

        if (scratch.Length < frames * 4) scratch = new byte[frames * 4];
        var src = e.Buffer;
        for (int frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            int pos = frame * frameBytes;
            for (int k = 0; k < ch; k++, pos += bytesPerSample)
            {
                sum += bytesPerSample switch
                {
                    2 => BitConverter.ToInt16(src, pos) / 32768f,
                    3 => ((src[pos] | (src[pos + 1] << 8) | ((sbyte)src[pos + 2] << 16))) / 8388608f,
                    4 when isFloat => BitConverter.ToSingle(src, pos),
                    4 => BitConverter.ToInt32(src, pos) / 2147483648f,
                    _ => 0f,
                };
            }
            BitConverter.TryWriteBytes(scratch.AsSpan(frame * 4), sum / ch);
        }

        // Mic and output run on different clocks; if we drift too far behind, catch up.
        if (buffer.BufferedDuration > MaxBuffered) buffer.ClearBuffer();
        buffer.AddSamples(scratch, 0, frames * 4);
    }

    sealed class SpeechMixProvider(ISampleProvider source, SpeechQueue speech, bool beforeEffects) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = source.Read(buffer, offset, count);
            speech.Mix(buffer, offset, n, beforeEffects);
            return n;
        }
    }

    /// <summary>Copies everything read through it into a second buffer (for monitoring).</summary>
    sealed class TeeSampleProvider(ISampleProvider source, BufferedWaveProvider target) : ISampleProvider
    {
        byte[] bytes = Array.Empty<byte>();

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = source.Read(buffer, offset, count);
            if (bytes.Length < n * 4) bytes = new byte[n * 4];
            Buffer.BlockCopy(buffer, offset * 4, bytes, 0, n * 4);
            if (target.BufferedDuration > MaxBuffered) target.ClearBuffer();
            target.AddSamples(bytes, 0, n * 4);
            return n;
        }
    }

    sealed class MonoToMultiChannelProvider : ISampleProvider
    {
        readonly ISampleProvider source;
        readonly int channels;
        float[] mono = Array.Empty<float>();

        public MonoToMultiChannelProvider(ISampleProvider source, int channels)
        {
            this.source = source;
            this.channels = channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / channels;
            if (mono.Length < frames) mono = new float[frames];
            int n = source.Read(mono, 0, frames);
            for (int i = 0; i < n; i++)
                for (int c = 0; c < channels; c++)
                    buffer[offset + i * channels + c] = mono[i];
            return n * channels;
        }
    }
}

public sealed record DeviceItem(string Id, string Name)
{
    public override string ToString() => Name;
}
