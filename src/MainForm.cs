using System.Text.Json;
using NAudio.CoreAudioApi;

namespace VoiceFx;

sealed class AppSettings
{
    public string? InputId { get; set; }
    public string? OutputId { get; set; }
    public string? MonitorId { get; set; }
    public bool MonitorEnabled { get; set; }
    public string? TtsVoice { get; set; }
    public bool TtsMuteMic { get; set; } = true;
    public string? Preset { get; set; }
    public List<string> CollapsedSections { get; set; } = new();
    public bool PresetModified { get; set; }
    // Null until first run, when it is filled with the starter custom presets.
    public List<CustomPreset>? CustomPresets { get; set; }
    public EffectSettings Effects { get; set; } = new();

    // Stored in a "settings" folder in the app's root folder.
    static string FilePath => Path.Combine(FindRootFolder(), "settings", "settings.json");

    // Where older versions kept it; read once if there is no settings file yet.
    static readonly string[] OldFilePaths =
    {
        Path.Combine(AppContext.BaseDirectory, "settings.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceFx", "settings.json"),
    };

    // The root folder is the one holding "src" (the exe is built into src\bin\...).
    // An exe copied somewhere else on its own uses the folder it is in.
    static string FindRootFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "VoiceFx.csproj")))
                return dir.FullName;
        return AppContext.BaseDirectory;
    }

    public static AppSettings Load()
    {
        foreach (var path in OldFilePaths.Prepend(FilePath))
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
            }
            catch { }
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public sealed class MainForm : Form
{
    const int ContentWidth = 470;

    readonly AppSettings app = AppSettings.Load();
    readonly AudioEngine engine;
    readonly ComboBox inputBox = NewCombo(), outputBox = NewCombo(), monitorBox = NewCombo();
    readonly CheckBox monitorCheck = new() { Text = "Hear myself on", AutoSize = true, Anchor = AnchorStyles.Left };
    readonly CheckBox bypassCheck = new() { Text = "Bypass all effects", AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Button startButton = new() { Text = "Enable", Width = 110, Height = 32 };
    readonly ProgressBar meter = new() { Width = 300, Height = 14, Maximum = 100, Anchor = AnchorStyles.Left };
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(ContentWidth - 30, 0) };
    readonly List<Action> refreshers = new();
    readonly List<Button> presetButtons = new();
    readonly Button deletePresetButton = new() { Text = "Delete preset", AutoSize = true, Height = 26 };
    GlassFlow customRow = null!;
    readonly TextBox ttsText = new()
    {
        Multiline = true, Width = ContentWidth - 30, Height = 60, ScrollBars = ScrollBars.Vertical,
        PlaceholderText = "Type what you want to say, then press Speak (or Ctrl+Enter)",
    };
    readonly ComboBox ttsVoice = NewCombo();
    readonly CheckBox ttsMute = new() { Text = "Mute mic while speaking", AutoSize = true };
    readonly Button speakButton = new() { Text = "Speak", Width = 90, Height = 30 };
    readonly Button stopSpeechButton = new() { Text = "Stop", Width = 70, Height = 30 };
    readonly Button jargonButton = new() { Text = "Add corporate jargon", AutoSize = true, Height = 30 };
    readonly string[] jargon = LoadJargon();
    int lastJargon = -1;
    readonly System.Windows.Forms.Timer meterTimer = new() { Interval = 50 };
    bool refreshing;

    EffectSettings Fx => app.Effects;

    public MainForm()
    {
        Text = "VoiceFx – voice effects for Teams";
        ClientSize = new Size(ContentWidth + 40, 860);
        MinimumSize = new Size(ContentWidth + 40, 400);
        MaximizeBox = false;
        // Window and taskbar icon: the one built into VoiceFx.exe.
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = new Font("Segoe UI", 9f);

        engine = new AudioEngine(Fx);
        engine.Failed += ex => BeginInvoke(() =>
        {
            StopEngine();
            status.Text = "Audio stopped: " + ex.Message;
        });

        var root = new BackgroundPanel(Color.FromArgb(255, 224, 178)) // light orange
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(10),
        };
        Controls.Add(root);

        root.Controls.Add(BuildDevices());
        root.Controls.Add(BuildWaveform());
        root.Controls.Add(BuildPresets());
        root.Controls.Add(BuildSpeech());
        root.Controls.Add(Section("Pitch", () => Fx.PitchEnabled, v => Fx.PitchEnabled = v, t =>
            AddSlider(t, "Semitones", -24, 24, 0.5f, "{0:+0.0;-0.0;0}", () => Fx.PitchSemitones, v => Fx.PitchSemitones = v)));
        root.Controls.Add(Section("Echo", () => Fx.EchoEnabled, v => Fx.EchoEnabled = v, t =>
        {
            AddSlider(t, "Delay", 2, 100, 10f, "{0} ms", () => Fx.EchoDelayMs, v => Fx.EchoDelayMs = v);
            AddSlider(t, "Feedback", 0, 90, 0.01f, "{0:P0}", () => Fx.EchoFeedback, v => Fx.EchoFeedback = v);
            AddSlider(t, "Mix", 0, 100, 0.01f, "{0:P0}", () => Fx.EchoMix, v => Fx.EchoMix = v);
        }));
        root.Controls.Add(Section("Reverb", () => Fx.ReverbEnabled, v => Fx.ReverbEnabled = v, t =>
        {
            AddSlider(t, "Room size", 0, 100, 0.01f, "{0:P0}", () => Fx.ReverbRoom, v => Fx.ReverbRoom = v);
            AddSlider(t, "Mix", 0, 100, 0.01f, "{0:P0}", () => Fx.ReverbMix, v => Fx.ReverbMix = v);
        }));
        root.Controls.Add(Section("Robot", () => Fx.RobotEnabled, v => Fx.RobotEnabled = v, t =>
        {
            AddSlider(t, "Frequency", 0, 500, 1f, "{0} Hz", () => Fx.RobotFreq, v => Fx.RobotFreq = v);
            AddSlider(t, "Metallic", 0, 100, 0.01f, "{0:P0}", () => Fx.RobotMetal, v => Fx.RobotMetal = v);
            AddSlider(t, "Tone", 2, 40, 0.5f, "{0:0.0} ms", () => Fx.RobotToneMs, v => Fx.RobotToneMs = v);
        }));
        root.Controls.Add(Section("Output", null, null, t =>
            AddSlider(t, "Volume", -20, 20, 1f, "{0:+0;-0;0} dB", () => Fx.GainDb, v => Fx.GainDb = v)));

        bypassCheck.CheckedChanged += (_, _) => Fx.Bypass = bypassCheck.Checked;
        refreshers.Add(() => bypassCheck.Checked = Fx.Bypass);
        startButton.Click += (_, _) => { if (engine.IsRunning) StopEngine(); else StartEngine(); };
        monitorCheck.Checked = app.MonitorEnabled;
        monitorCheck.CheckedChanged += (_, _) => monitorBox.Enabled = monitorCheck.Checked;
        monitorBox.Enabled = monitorCheck.Checked;

        meterTimer.Tick += (_, _) =>
        {
            // Show level in dB: -60 dB .. 0 dB maps to 0 .. 100.
            float peak = engine.IsRunning ? engine.Peak : 0;
            double db = peak > 0 ? 20 * Math.Log10(peak) : -60;
            meter.Value = (int)Math.Clamp((db + 60) / 60 * 100, 0, 100);
        };
        meterTimer.Start();

        LoadDevices();
        RefreshAll();
        ShowActivePreset();
        ApplyPanelStyle(root);

        // Restore which sections were folded, and remember changes.
        foreach (var section in root.Controls.OfType<SectionBox>().Where(s => s.Collapsible))
        {
            section.Collapsed = app.CollapsedSections.Contains(section.Title);
            section.CollapsedChanged += (_, _) =>
            {
                app.CollapsedSections.Remove(section.Title);
                if (section.Collapsed) app.CollapsedSections.Add(section.Title);
            };
        }
    }

    // Let the background (image + see-through white sections) show behind text.
    // The containers themselves are SectionBox/GlassTable/GlassFlow, which paint it.
    static void ApplyPanelStyle(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case Label or CheckBox:
                    c.BackColor = Color.Transparent;
                    break;
            }
            ApplyPanelStyle(c);
        }
    }

    // Lock the width (only the height can be changed). Done here, after Windows has
    // scaled the window for the screen's DPI, so the locked width is the real one.
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        MinimumSize = new Size(Width, MinimumSize.Height);
        MaximumSize = new Size(Width, SystemInformation.VirtualScreen.Height);
    }

    static ComboBox NewCombo() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Anchor = AnchorStyles.Left };

    static bool IsCable(DeviceItem d) => d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase);

    Control BuildDevices()
    {
        var g = NewGroup("Devices", collapsible: false);
        var t = NewTable(2);
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 310));

        t.Controls.Add(RowLabel("Microphone"));
        t.Controls.Add(inputBox);
        t.Controls.Add(RowLabel("Send to (Teams)"));
        t.Controls.Add(outputBox);
        t.Controls.Add(monitorCheck);
        t.Controls.Add(monitorBox);
        t.Controls.Add(startButton);
        t.Controls.Add(meter);
        t.Controls.Add(bypassCheck);
        t.SetColumnSpan(bypassCheck, 2);
        t.Controls.Add(status);
        t.SetColumnSpan(status, 2);

        g.Controls.Add(t);
        return g;
    }

    Control BuildWaveform()
    {
        var g = NewGroup("Waveform");
        var t = NewTable(1);
        t.Controls.Add(new WaveformView(() => engine.Chain)
        {
            Size = new Size(ContentWidth - 26, 130),
            Font = new Font(Font.FontFamily, 8f),
            Margin = new Padding(3, 3, 3, 6),
        });
        g.Controls.Add(t);
        return g;
    }

    Control BuildPresets()
    {
        var g = NewGroup("Presets");
        // Two groups of buttons, each under a small heading, with a divider between them.
        var layout = new GlassTable { ColumnCount = 1, AutoSize = true, Location = new Point(8, 22) };
        GlassFlow NewRow() => new() { AutoSize = true, MaximumSize = new Size(ContentWidth - 20, 0), Margin = Padding.Empty };
        Label Heading(string text) => new()
        {
            Text = text, AutoSize = true, ForeColor = SystemColors.GrayText,
            Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 4, 3, 0),
        };
        var defaults = NewRow();
        var custom = NewRow();
        var divider = new Panel
        {
            Height = 1, Width = ContentWidth - 30, BackColor = Color.FromArgb(215, 200, 180),
            Margin = new Padding(3, 8, 3, 4),
        };
        var manage = NewRow();
        manage.Margin = new Padding(0, 2, 0, 0);
        var addButton = new Button { Text = "＋ Save current as preset…", AutoSize = true, Height = 26 };
        addButton.Click += (_, _) => AddCustomPreset();
        deletePresetButton.Click += (_, _) => DeleteCustomPreset();
        manage.Controls.AddRange(new Control[] { addButton, deletePresetButton });
        layout.Controls.AddRange(new Control[] { Heading("Default"), defaults, divider, Heading("Custom"), custom, manage });
        customRow = custom;

        void Add(string name, Action<EffectSettings> apply)
        {
            var b = NewPresetButton(name, () =>
            {
                Fx.PitchEnabled = Fx.EchoEnabled = Fx.ReverbEnabled = Fx.RobotEnabled = false;
                Fx.GainDb = 0;
                apply(Fx);
            });
            defaults.Controls.Add(b);
        }

        Add("Normal", _ => { });
        Add("Chipmunk", f => { f.PitchEnabled = true; f.PitchSemitones = 8; });
        Add("Deep voice", f => { f.PitchEnabled = true; f.PitchSemitones = -5; });
        Add("Robot", f => { f.RobotEnabled = true; f.RobotFreq = 50; f.RobotMetal = 0.85f; f.RobotToneMs = 5; });

        app.CustomPresets ??= StarterCustomPresets();
        RebuildCustomButtons();

        g.Controls.Add(layout);
        return g;
    }

    Button NewPresetButton(string name, Action apply)
    {
        var b = new Button { Text = name, Tag = name, AutoSize = true, Height = 28 };
        b.Click += (_, _) =>
        {
            Fx.Bypass = false;
            apply();
            RefreshAll();
            app.Preset = name;
            app.PresetModified = false;
            ShowActivePreset();
        };
        presetButtons.Add(b);
        return b;
    }

    void RebuildCustomButtons()
    {
        foreach (var b in customRow.Controls.OfType<Button>().ToList())
        {
            presetButtons.Remove(b);
            b.Dispose();
        }
        foreach (var p in app.CustomPresets!)
            customRow.Controls.Add(NewPresetButton(p.Name, () => Fx.CopyFrom(p.Effects)));
        ShowActivePreset();
    }

    bool IsCustom(string? name) => app.CustomPresets!.Any(p => p.Name == name);

    void AddCustomPreset()
    {
        string suggestion = app.Preset != null && app.PresetModified && IsCustom(app.Preset) ? app.Preset : "";
        string? name = AskPresetName(suggestion);
        if (name == null) return;

        var existing = app.CustomPresets!.FirstOrDefault(p => p.Name == name);
        if (existing != null)
        {
            if (MessageBox.Show(this, $"Replace the preset \"{name}\" with the current settings?", "VoiceFx",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            existing.Effects.CopyFrom(Fx);
        }
        else
        {
            var p = new CustomPreset { Name = name };
            p.Effects.CopyFrom(Fx);
            app.CustomPresets!.Add(p);
        }
        app.Preset = name;
        app.PresetModified = false;
        RebuildCustomButtons();
        app.Save();
    }

    void DeleteCustomPreset()
    {
        string? name = app.Preset;
        if (!IsCustom(name)) return;
        if (MessageBox.Show(this, $"Delete the preset \"{name}\"?", "VoiceFx",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        app.CustomPresets!.RemoveAll(p => p.Name == name);
        // The current sound stays as it is; it just no longer belongs to a preset.
        app.Preset = null;
        app.PresetModified = false;
        RebuildCustomButtons();
        app.Save();
    }

    // Asks for a preset name; null if cancelled. Names of the Default presets are refused.
    string? AskPresetName(string initial)
    {
        using var dialog = new Form
        {
            Text = "Save preset", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(320, 110), Font = Font,
        };
        var label = new Label { Text = "Preset name:", AutoSize = true, Location = new Point(12, 12) };
        var box = new TextBox { Text = initial, Location = new Point(12, 34), Width = 296, MaxLength = 30 };
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(152, 72), Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(233, 72), Width = 75 };
        dialog.Controls.AddRange(new Control[] { label, box, ok, cancel });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            string name = box.Text.Trim();
            if (name.Length == 0) continue;
            if (presetButtons.Any(b => (string)b.Tag! == name) && !IsCustom(name))
            {
                MessageBox.Show(this, $"\"{name}\" is a default preset. Please choose another name.", "VoiceFx");
                continue;
            }
            return name;
        }
        return null;
    }

    // The custom presets a new installation starts with; they can be deleted like any other.
    static List<CustomPreset> StarterCustomPresets()
    {
        CustomPreset Make(string name, Action<EffectSettings> apply)
        {
            var p = new CustomPreset { Name = name };
            apply(p.Effects);
            return p;
        }

        return new()
        {
            Make("Donald Duck", f =>
            {
                // High pitch, a fast flutter for the gargly "quack", and a very short
                // resonance (1 ms = peaks every 1 kHz) for the nasal tone.
                f.PitchEnabled = true; f.PitchSemitones = 7;
                f.RobotEnabled = true; f.RobotFreq = 12; f.RobotMetal = 0.55f; f.RobotToneMs = 1;
            }),
            Make("Darth Vader", f =>
            {
                // Deep voice through a mask: lower pitch, a light robotic buzz with a little
                // metal, a small room, and a lot more volume. Tuned by ear by the user.
                f.PitchEnabled = true; f.PitchSemitones = -5.5f;
                f.RobotEnabled = true; f.RobotFreq = 83; f.RobotMetal = 0.19f; f.RobotToneMs = 3;
                f.ReverbEnabled = true; f.ReverbRoom = 0.28f; f.ReverbMix = 0.32f;
                f.GainDb = 18;
            }),
            Make("Cylon", f =>
            {
                // Dark metallic robot: lowered pitch, slow ring mod, deep ringing resonance.
                f.PitchEnabled = true; f.PitchSemitones = -4;
                f.RobotEnabled = true; f.RobotFreq = 30; f.RobotMetal = 0.9f; f.RobotToneMs = 10;
                f.ReverbEnabled = true; f.ReverbRoom = 0.3f; f.ReverbMix = 0.15f;
            }),
            Make("Yoda", f =>
            {
                // Small, old and nasal: somewhat higher pitch and a short, gentle
                // resonance for the pinched tone, without robotic chopping.
                f.PitchEnabled = true; f.PitchSemitones = 4;
                f.RobotEnabled = true; f.RobotFreq = 0; f.RobotMetal = 0.35f; f.RobotToneMs = 1.5f;
            }),
        };
    }

    Control BuildSpeech()
    {
        var g = NewGroup("Text to speech");
        var t = NewTable(2);
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));

        t.Controls.Add(ttsText);
        t.SetColumnSpan(ttsText, 2);
        t.Controls.Add(RowLabel("Voice"));
        t.Controls.Add(ttsVoice);
        var options = new GlassFlow { AutoSize = true, Margin = Padding.Empty };
        options.Controls.Add(ttsMute);
        t.Controls.Add(options);
        t.SetColumnSpan(options, 2);
        var buttons = new GlassFlow { AutoSize = true, Margin = Padding.Empty };
        buttons.Controls.AddRange(new Control[] { speakButton, stopSpeechButton, jargonButton });
        t.Controls.Add(buttons);
        t.SetColumnSpan(buttons, 2);
        g.Controls.Add(t);

        try
        {
            var voices = TextToSpeech.EnglishVoices();
            foreach (var v in voices) ttsVoice.Items.Add(v);
            ttsVoice.SelectedItem = voices.FirstOrDefault(v => v.Name == app.TtsVoice) ?? voices.FirstOrDefault();
        }
        catch { }
        if (ttsVoice.Items.Count == 0)
        {
            ttsVoice.Items.Add("(no English voices installed)");
            ttsVoice.SelectedIndex = 0;
            ttsVoice.Enabled = speakButton.Enabled = false;
        }
        ttsMute.Checked = app.TtsMuteMic;

        speakButton.Click += (_, _) => Speak();
        stopSpeechButton.Click += (_, _) => engine.Speech.Stop();
        jargonButton.Enabled = jargon.Length > 0;
        jargonButton.Click += (_, _) =>
        {
            // A random phrase, never the same one twice in a row.
            int i = Random.Shared.Next(jargon.Length);
            if (i == lastJargon && jargon.Length > 1) i = (i + 1) % jargon.Length;
            lastJargon = i;
            ttsText.Text = jargon[i];
            ttsText.Focus();
            ttsText.SelectionStart = ttsText.TextLength;
        };
        ttsText.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Speak();
            }
        };
        return g;
    }

    // Phrases built into the exe from Assets\corporate_jargon.txt, without the "12. " numbering.
    static string[] LoadJargon()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("VoiceFx.corporate_jargon.txt");
        if (stream == null) return Array.Empty<string>();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split('\n')
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line.Trim(), @"^\d+\.\s*", ""))
            .Where(line => line.Length > 0)
            .ToArray();
    }

    async void Speak()
    {
        string text = ttsText.Text.Trim();
        if (text.Length == 0 || !speakButton.Enabled) return;
        if (!engine.IsRunning)
        {
            status.Text = "Press Enable first, then Speak.";
            return;
        }

        speakButton.Enabled = false;
        try
        {
            string? voice = (ttsVoice.SelectedItem as VoiceItem)?.Name;
            int rate = engine.SampleRate;
            var samples = await Task.Run(() => TextToSpeech.Synthesize(text, voice, rate));
            // Speech always goes through the current preset ("Normal" = clean voice).
            engine.Speech.Play(samples, throughEffects: true, ttsMute.Checked);
        }
        catch (Exception ex)
        {
            status.Text = "Speech failed: " + ex.Message;
        }
        finally
        {
            speakButton.Enabled = true;
        }
    }

    GroupBox Section(string title,Func<bool>? getOn, Action<bool>? setOn, Action<TableLayoutPanel> build)
    {
        var g = NewGroup(title);
        var t = NewTable(3);
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

        if (getOn != null && setOn != null)
        {
            var on = new CheckBox { Text = "Enabled", AutoSize = true };
            on.CheckedChanged += (_, _) => { setOn(on.Checked); OnUserTweak(); };
            refreshers.Add(() => on.Checked = getOn());
            t.Controls.Add(on);
            t.SetColumnSpan(on, 3);
        }
        build(t);
        g.Controls.Add(t);
        return g;
    }

    void AddSlider(TableLayoutPanel t, string name, int min, int max, float scale, string format,
        Func<float> get, Action<float> set)
    {
        var bar = new TrackBar
        {
            Minimum = min, Maximum = max, TickStyle = TickStyle.None, AutoSize = false, Width = 265, Height = 28,
            Anchor = AnchorStyles.Left,
        };
        var value = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        void Show() => value.Text = string.Format(format, bar.Value * scale);
        bar.ValueChanged += (_, _) => { set(bar.Value * scale); Show(); OnUserTweak(); };
        refreshers.Add(() =>
        {
            bar.Value = Math.Clamp((int)MathF.Round(get() / scale), min, max);
            Show();
        });
        t.Controls.Add(RowLabel(name));
        t.Controls.Add(bar);
        t.Controls.Add(value);
    }

    static GroupBox NewGroup(string title, bool collapsible = true) => new SectionBox
    {
        Collapsible = collapsible,
        Title = title,
        Width = ContentWidth,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        // Same width for every section; only the height follows the content.
        // The minimum height keeps a folded section's title visible.
        MinimumSize = new Size(ContentWidth, 30),
        MaximumSize = new Size(ContentWidth, 0),
        Padding = new Padding(8),
        Margin = new Padding(0, 0, 0, 8),
    };

    static TableLayoutPanel NewTable(int columns) => new GlassTable
    {
        ColumnCount = columns,
        AutoSize = true,
        Location = new Point(8, 22),
    };

    static Label RowLabel(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };

    void RefreshAll()
    {
        refreshing = true;
        foreach (var r in refreshers) r();
        refreshing = false;
    }

    // A slider or checkbox moved by hand: the preset stays highlighted but gets a "*".
    void OnUserTweak()
    {
        if (refreshing || app.Preset == null || app.PresetModified) return;
        app.PresetModified = true;
        ShowActivePreset();
    }

    void ShowActivePreset()
    {
        // Only a selected custom preset can be deleted.
        deletePresetButton.Enabled = app.CustomPresets != null && IsCustom(app.Preset);
        foreach (var b in presetButtons)
        {
            var name = (string)b.Tag!;
            bool active = name == app.Preset;
            b.Text = active && app.PresetModified ? name + " *" : name;
            b.Font = active ? new Font(Font, FontStyle.Bold) : Font;
            if (active)
            {
                b.BackColor = SystemColors.Highlight;
                b.ForeColor = SystemColors.HighlightText;
            }
            else
            {
                b.UseVisualStyleBackColor = true;
                b.ForeColor = SystemColors.ControlText;
            }
        }
    }

    void LoadDevices()
    {
        var inputs = AudioEngine.ListDevices(DataFlow.Capture);
        var outputs = AudioEngine.ListDevices(DataFlow.Render);
        string? defIn = AudioEngine.DefaultDeviceId(DataFlow.Capture);
        string? defOut = AudioEngine.DefaultDeviceId(DataFlow.Render);

        // Never default the mic to the cable itself: that would loop our own output back in.
        Fill(inputBox, inputs, app.InputId,
            d => d.Id == defIn && !IsCable(d), d => !IsCable(d));
        Fill(outputBox, outputs, app.OutputId,
            d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase), IsCable);
        Fill(monitorBox, outputs, app.MonitorId,
            d => d.Id == defOut && !IsCable(d), d => !IsCable(d));

        if (!outputs.Any(IsCable))
            status.Text = "VB-Cable was not found. Install it from vb-audio.com/Cable, restart this app, " +
                          "then pick \"CABLE Input\" above.";
        else
            status.Text = "Press Enable, then in Teams choose \"CABLE Output\" as your microphone.";
    }

    static void Fill(ComboBox box, List<DeviceItem> items, string? savedId, params Func<DeviceItem, bool>[] preferences)
    {
        box.Items.Clear();
        foreach (var d in items) box.Items.Add(d);
        var pick = items.FirstOrDefault(d => d.Id == savedId);
        foreach (var p in preferences)
            pick ??= items.FirstOrDefault(p);
        pick ??= items.FirstOrDefault();
        box.SelectedItem = pick;
    }

    void StartEngine()
    {
        if (inputBox.SelectedItem is not DeviceItem input || outputBox.SelectedItem is not DeviceItem output)
        {
            status.Text = "Pick a microphone and an output device first.";
            return;
        }
        var mon = monitorCheck.Checked ? monitorBox.SelectedItem as DeviceItem : null;
        if (mon?.Id == output.Id) mon = null;

        try
        {
            engine.Start(input.Id, output.Id, mon?.Id);
        }
        catch (Exception ex)
        {
            engine.Stop();
            status.Text = "Could not start audio: " + ex.Message;
            return;
        }

        startButton.Text = "Disable";
        SetDevicePickersEnabled(false);
        status.Text = IsCable(output)
            ? "Enabled. In Teams: Settings → Devices → Microphone → \"CABLE Output\"."
            : $"Enabled. Sending processed voice to \"{output.Name}\".";
        if (IsCable(input))
            status.Text += " Warning: your microphone is set to a cable device, which can cause feedback.";
    }

    void StopEngine()
    {
        engine.Stop();
        startButton.Text = "Enable";
        SetDevicePickersEnabled(true);
        status.Text = "Disabled.";
    }

    void SetDevicePickersEnabled(bool enabled)
    {
        inputBox.Enabled = outputBox.Enabled = monitorCheck.Enabled = enabled;
        monitorBox.Enabled = enabled && monitorCheck.Checked;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        app.InputId = (inputBox.SelectedItem as DeviceItem)?.Id;
        app.OutputId = (outputBox.SelectedItem as DeviceItem)?.Id;
        app.MonitorId = (monitorBox.SelectedItem as DeviceItem)?.Id;
        app.MonitorEnabled = monitorCheck.Checked;
        if (ttsVoice.SelectedItem is VoiceItem voice) app.TtsVoice = voice.Name;
        app.TtsMuteMic = ttsMute.Checked;
        app.Save();
        meterTimer.Stop();
        engine.Dispose();
        base.OnFormClosing(e);
    }
}
