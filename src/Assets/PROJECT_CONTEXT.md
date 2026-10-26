# VoiceFx – project context and hand-over notes

Written 2026-10-27, when the project was being moved to another computer.
Audience: the owner, or any AI assistant picking the project up later. Read this
first. It explains what the app does, how it is built, why it is built that way
and what the owner has asked for so far.

---

## 1. What VoiceFx is

VoiceFx is a small **Windows desktop app that changes your voice in real time
during online meetings** (Microsoft Teams first, but also Zoom, Google Meet,
Discord and so on). It can raise or lower your pitch, add echo, reverb and a
robot or metallic sound, apply presets such as "Darth Vader" or "Donald Duck",
and speak typed text in a computer voice through the same effects.

- **Owner:** Anders Lejczak (anders.lejczak@colacola.se). They aren't a
  developer; they direct the work in plain language and test by ear.
- **Built with:** C# / .NET 8, Windows Forms, NAudio 2.2.1, System.Speech 8.0.0.
- **Status:** working and in daily use. The owner has heard the effects in real
  calls and tuned presets by ear.

### The one key idea: a virtual audio cable

Teams can only use a **microphone device**, so an app can't "inject" audio
into Teams directly. VoiceFx works with **VB-Cable** (free, from
https://vb-audio.com/Cable/), a virtual audio driver that adds two devices:

```
Real mic ──► VoiceFx (effects) ──► "CABLE Input"  ═══►  "CABLE Output" ──► Teams uses this as its microphone
                     └──► (optional) your headphones, to hear yourself ("Hear myself on")
```

Writing our own virtual audio driver was rejected early, because it needs
kernel-mode code and driver signing. VB-Cable must be installed on every PC
that uses VoiceFx (needs admin rights, may need a reboot).

---

## 2. Folder layout

The owner wants **only the README, the shortcut and the settings folder at the
root**. Everything else lives in `src`.

```
VoiceFx\
├── README.md               End-user setup and use instructions (the owner edits this too)
├── VoiceFx.exe.lnk         Shortcut to src\bin\Release\net8.0-windows\VoiceFx.exe
├── settings\
│   └── settings.json       All saved settings (devices, sliders, presets, folded sections…)
└── src\
    ├── VoiceFx.csproj
    ├── Program.cs           Entry point
    ├── MainForm.cs          The window: all UI, presets, settings load/save (biggest file)
    ├── AudioEngine.cs       Mic capture → effects → output devices (WASAPI)
    ├── Effects.cs           EffectSettings, EffectChain and the DSP effects, CustomPreset, WaveTap
    ├── TextToSpeech.cs      Windows speech synthesis + SpeechQueue (mixes speech into the audio)
    ├── WaveformView.cs      Oscilloscope-style live waveform control
    ├── BackgroundPanel.cs   Window background painting + SectionBox (collapsible) + Glass* containers
    ├── Assets\
    │   ├── VoiceFx.ico            App icon (pink/red speaker, multi-size 16–256 px)
    │   ├── corporate_jargon.txt   200 numbered phrases, embedded in the exe
    │   └── PROJECT_CONTEXT.md     This file
    ├── bin\, obj\           Build output (can be deleted; rebuilt by dotnet build)
```

---

## 3. Requirements, build and run

- **Runtime:** Windows 10 (1607+) or 11, 64-bit. **.NET 8 Desktop Runtime** to run,
  **.NET 8 SDK** to build (installed on the first PC with
  `winget install Microsoft.DotNet.SDK.8`). Plus **VB-Cable**.
- **Build** (from `src`): `dotnet build -c Release`, which produces
  `src\bin\Release\net8.0-windows\VoiceFx.exe` (about 3 MB plus DLLs).
- **Run:** double-click `VoiceFx.exe.lnk` in the root, or the exe itself.
- **The exe is locked while VoiceFx runs.** Close the app before rebuilding
  (the build fails otherwise). The assistant always checked
  `Get-Process VoiceFx` before building.
- **Self-contained option (tried, then dropped by the owner):** adding
  `RuntimeIdentifier=win-x64`, `SelfContained=true`, `PublishSingleFile=true`,
  `IncludeNativeLibrariesForSelfExtract=true`, `EnableCompressionInSingleFile=true`
  and running `dotnet publish -c Release -o publish` gives one ~69 MB exe that
  needs no .NET install. That worked and was verified (it loads nothing from the
  installed .NET). The owner was surprised by the size, deleted that copy and
  chose to keep the normal build for now. WinForms can't be trimmed, so ~69 MB is
  about the minimum.
- **Code signing:** not done. Unsigned exes downloaded from the internet trigger
  SmartScreen ("More info → Run anyway"). Options were discussed (Azure Trusted
  Signing, a commercial certificate, a self-signed certificate inside a company), but nothing was chosen.

---

## 4. Architecture

### 4.1 Audio path (`AudioEngine.cs`)

```
WasapiCapture (mic, shared, event-driven, 10 ms)
   │  OnDataAvailable: converts any format (16/24/32-bit int, 32-bit float, any channel count) → mono float
   ▼
BufferedWaveProvider (mono float @ mic sample rate; cleared if > 120 ms buffered = clock-drift catch-up)
   ▼
SpeechMixProvider(before effects)   ← text-to-speech is mixed in here (mic optionally muted)
   ▼
EffectChain                         ← all effects; also feeds WaveTaps (dry/wet) for the waveform view
   ▼
SpeechMixProvider(after effects)    ← unused now (speech always goes through effects), kept for flexibility
   ▼
TeeSampleProvider (only if "Hear myself on") ──► second buffer ──► WasapiOut (monitor device)
   ▼
WasapiOut (output device, normally "CABLE Input", shared, 30 ms latency)
```

- Each output is converted to the device's **shared-mode mix format**: resampled
  with `WdlResamplingSampleProvider` when the rate differs, and copied to all
  channels with `MonoToMultiChannelProvider`.
- Device errors (unplugged and so on) raise `AudioEngine.Failed`. The form then
  stops and shows the error.
- Device lists are read **only at startup**. Headphones plugged in later need an
  app restart; auto-refresh was offered but not requested.
- Device defaults on first run: the **mic** is Windows' default communications
  mic, never a "CABLE" device (avoids feedback loops). The **output** is the first
  device whose name contains "CABLE Input". The **monitor** is the default
  playback device, never a cable. Saved device IDs win when they still exist.

### 4.2 Effects (`Effects.cs`)

`EffectSettings` holds all parameters. The UI thread writes them and the audio
thread reads them once per block, so no locks are used. `EffectChain` (an
`ISampleProvider`) processes mono float samples in this order:

1. **Pitch shift:** a low-latency delay-line pitch shifter with two read taps
   and sin² crossfade over 50 ms grains (`PitchShifter`). This is *not* FFT-based,
   so latency stays low. Ratio = 2^(semitones/12).
2. **Robot** (`RingModulator` class, which does two things):
   - a ring modulator (`x · sin(2π·f·t)`); **frequency 0 = off** (no chopping),
   - followed by a resonant **comb filter** ("Metallic" = feedback 0–0.92,
     "Tone" = delay in ms) that gives the tin-can or helmet ring. Its output is
     scaled by `(1 - feedback·0.75)` to keep the level steady.
3. **Echo:** a feedback delay line (delay, feedback, mix), up to 2 s buffer.
4. **Reverb:** small Freeverb-style: 8 damped combs in parallel, then 4 allpasses.
   "Room size" maps to comb feedback 0.70–0.98.
5. **Output gain** (dB), then a **soft clip** (linear up to 0.9, tanh knee above).

Delay lines are cleared when an effect is switched back on, so stale audio
doesn't burst out. **Bypass** skips everything (gain = 1). `Peak` drives the
level meter. `DryTap` and `WetTap` are lock-free ring buffers (16 k samples) that
the waveform view reads.

### 4.3 Text to speech (`TextToSpeech.cs`)

- Uses **System.Speech (SAPI)** with the installed **English** voices only. On
  the first PC those were "Microsoft David Desktop" and "Microsoft Zira Desktop".
  In the UI they're shown as **"Male" / "Female"** (numbered if there are
  several), ordered Male first. The real voice name is saved in settings.
- Synthesizes at 24 kHz/16-bit mono on a background task, then resamples
  linearly to the engine's rate. `SpeechQueue` mixes it into the stream *before*
  the effects (so speech gets the current preset) and can mute the mic while
  speaking.
- Only works while the engine is **enabled** (it needs the sample rate and the
  output).
- Swedish voices would need the Windows Swedish speech pack. The owner chose
  "English only for now".

### 4.4 UI (`MainForm.cs`, `BackgroundPanel.cs`, `WaveformView.cs`)

The window is built in code; there is no designer file. It is a fixed-width
window (width locked in `OnLoad` after DPI scaling, Maximize disabled, height
resizable, scrolls vertically), and the title is "VoiceFx – voice effects for
Teams". The background is **solid light orange `RGB(255,224,178)`**, and each
section is drawn as **white at alpha 215** over it (a pale cream). All sections
are exactly `ContentWidth = 470` px wide.

Sections, top to bottom:

| Section | Collapsible | Contents |
|---|---|---|
| **Devices** | no | Microphone, "Send to (Teams)" (output), "Hear myself on" checkbox + device, **Enable/Disable** button, level meter, "Bypass all effects", status text with hints |
| **Waveform** | yes | Oscilloscope view: dark-blue grid, glowing **cyan = sent to Teams**, soft **white = plain mic**; ~30 fps while enabled, flat line + "Press Enable to see your voice" otherwise; zero-crossing trigger + auto-zoom; stops drawing when folded |
| **Presets** | yes | Group "Default" (Normal, Chipmunk, Deep voice, Robot), a divider line, group "Custom" (user presets), then buttons "＋ Save current as preset…" and "Delete preset" |
| **Text to speech** | yes | Multiline text box (Ctrl+Enter = speak), Voice (Male/Female), "Mute mic while speaking", buttons Speak / Stop / **Add corporate jargon** |
| **Pitch** | yes | Enabled, Semitones −12…+12 (0.5 steps) |
| **Echo** | yes | Enabled, Delay 20–1000 ms, Feedback 0–90 %, Mix 0–100 % |
| **Reverb** | yes | Enabled, Room size 0–100 %, Mix 0–100 % |
| **Robot** | yes | Enabled, Frequency 0–500 Hz (0 = only metallic ring), Metallic 0–100 %, Tone 1–20 ms |
| **Output** | yes | Volume −20…+20 dB |

Behaviour details:

- **Collapsing:** click a section title (hand cursor) to fold or unfold it.
  ▼ = open, ▶ = folded. Folded section titles are saved in `CollapsedSections`.
- **Preset highlight:** the active preset button is filled with the Windows accent
  colour and has bold text. If a slider or checkbox is changed afterwards, the
  name gets a star (e.g. "Yoda *"), saved as `PresetModified`. Clicking a preset
  always restores its stored values and resets Volume.
- **Custom presets:** "Save current as preset…" asks for a name (max 30 characters).
  An existing custom name asks "Replace?", which is how a preset is updated. Default
  preset names are refused. "Delete preset" works only for the selected custom
  preset and asks for confirmation. Changes are saved to settings.json immediately.
- **Add corporate jargon:** replaces the text box with a random phrase from
  `Assets\corporate_jargon.txt` (embedded resource; the leading "12. " numbering is
  stripped; never the same phrase twice in a row). To change phrases, edit that
  file and rebuild; the Desktop original is no longer used.
- **Icon:** `Assets\VoiceFx.ico` is the exe icon (`ApplicationIcon`) and the window
  and taskbar icon (`Icon.ExtractAssociatedIcon`).

### 4.5 Settings (`AppSettings` in `MainForm.cs`)

- **Location:** `<root>\settings\settings.json`, where *root* is found by walking up
  from the exe folder to the first folder that contains `src\VoiceFx.csproj`. If
  the exe is copied somewhere on its own, a `settings` folder next to the exe is
  used. Older locations (`settings.json` next to the exe and
  `%APPDATA%\VoiceFx\settings.json`) are read once as fallbacks.
- **Saved** when the window closes (and right away after adding or deleting a custom
  preset). Killing the process doesn't save; the assistant used `Kill()` in tests
  on purpose so the owner's settings stayed untouched.
- **Fields:** `InputId`, `OutputId`, `MonitorId` (Windows device IDs, **machine-specific**),
  `MonitorEnabled`, `TtsVoice`, `TtsMuteMic`, `Preset`, `PresetModified`,
  `CollapsedSections`, `CustomPresets` (list of `{Name, Effects}`; **null = first run →
  filled with the starter custom presets**), `Effects` (current EffectSettings incl. `Bypass`).
  Unknown old keys (e.g. `TtsThroughEffects`) are ignored and drop out on the next save.

### 4.6 Painting: why it is done this way (important, hard-won)

- **Never add `WS_EX_COMPOSITED`** (an override of `CreateParams`) to the form. It
  was added once to reduce flicker and caused an endless repaint loop with
  TrackBars (one full CPU core, visible flicker even when idle, black areas).
  Removing it fixed everything. Idle CPU should be ~0 ms per few seconds; check with
  `TotalProcessorTime` if painting is changed.
- **WinForms transparency only works one level deep.** Nested
  `BackColor = Transparent` produced ghost copies of titles at offsets. The fix is
  in `BackgroundPanel.cs`: containers (`SectionBox`, `GlassTable`, `GlassFlow`)
  override `OnPaintBackground` and call `BackgroundPanel.PaintFor`, which paints
  the root background at the right offset. Only Labels and CheckBoxes use
  `Transparent` (set by `ApplyPanelStyle`).
- **TrackBars can't be transparent.** Their `BackColor` is set to the computed
  section colour in `BackgroundPanel.MatchTrackBarColors`.
- A **background image** was used for a while (first the owner's pastel painting),
  with fixed-position and then scrolling variants. It caused glitches and the owner
  replaced it with the solid orange colour. If an image comes back, the image must
  **scroll with the content** (Windows scrolls by moving pixels) and track bars need
  per-position colour matching.

---

## 5. Presets and their values

Default presets (hard-coded in `BuildPresets`, can't be deleted). Each starts from
"all effects off, volume 0 dB":

| Preset | Settings |
|---|---|
| Normal | everything off (clean voice) |
| Chipmunk | Pitch +8 |
| Deep voice | Pitch −5 |
| Robot | Robot 50 Hz, Metallic 85 %, Tone 5 ms |

Starter custom presets (`StarterCustomPresets`, written into settings.json on first
run; the owner can then change or delete them):

| Preset | Settings | Notes |
|---|---|---|
| Donald Duck | Pitch +7; Robot 12 Hz, Metallic 55 %, Tone 1 ms | flutter = "quack", 1 ms comb = nasal |
| Darth Vader | Pitch −5.5; Robot 83 Hz, Metallic 19 %, Tone 3 ms; Reverb room 28 %, mix 32 %; Volume +18 dB | **tuned by ear by the owner**; keep these exact values |
| Cylon | Pitch −4; Robot 30 Hz, Metallic 90 %, Tone 10 ms; Reverb room 30 %, mix 15 % | "dark metallic robot" |
| Yoda | Pitch +4; Robot 0 Hz (no chop), Metallic 35 %, Tone 1.5 ms | nasal, small |

Presets that were removed at the owner's request: Alien, Cave, Stadium.

---

## 6. How the owner wants things done (preferences learned)

- They make **small, concrete UI requests one at a time** and test them in real
  calls. Keep changes focused, rebuild, verify, and report plainly what changed and
  what couldn't be verified.
- They like **tidy folders** (only README, shortcut and settings at the root).
- They prefer **sensible defaults over many options**. For example, they removed
  "Apply effects" for speech because "Normal" already gives a clean voice.
- Wording they chose: **Enable / Disable** (not Start/Stop), "Hear myself on",
  "Send to (Teams)", "Add corporate jargon", "Default" / "Custom".
- When something is ambiguous (e.g. a preset missing from a list), ask rather
  than delete. Deep voice stayed in Default after asking.
- Ask before deleting files or installing software. They approved installing the
  .NET 8 SDK via winget.
- The owner sometimes edits README.md themselves (they added the Contact section).
  Keep their edits.

---

## 7. Testing approach used so far

- The owner tests sound by ear. The assistant can't hear audio, so it verified
  behaviour with **UI Automation (PowerShell, `System.Windows.Automation`)**:
  starting the exe, clicking buttons with `PostMessage(BM_CLICK)` (asynchronous, so
  modal dialogs don't block the script), reading text and enabled state, measuring
  control bounds and checking window size limits with `MoveWindow`.
- Screenshots used `Graphics.CopyFromScreen`. They fail with "The handle is
  invalid" when the screen is locked.
- **When the PC is used over Remote Desktop**, Windows swaps the audio devices for
  "Remote Audio" and there is no mic or VB-Cable, so the engine can't be enabled
  there. That isn't a bug. The waveform view was verified offline instead by a small
  harness that fed a test tone through `EffectChain` and rendered `WaveformView` to
  a PNG via reflection (`DrawToBitmap`).
- After UI changes, always check **idle CPU** (should be ~0) to catch repaint loops.

---

## 8. Migration checklist (moving to a new PC)

1. Copy the whole `VoiceFx` folder (`bin` and `obj` can be left out).
2. Install the **.NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`), or only
   the Desktop Runtime if you won't build.
3. Install **VB-Cable** (as administrator, reboot if asked). Make sure Windows
   didn't make "CABLE Output" the default recording device.
4. Rebuild: `dotnet build -c Release` in `src` (restores NAudio and System.Speech
   from NuGet, so it needs internet the first time).
5. **Recreate the shortcut** `VoiceFx.exe.lnk`. It stores an absolute path to the
   old location. Point it at `src\bin\Release\net8.0-windows\VoiceFx.exe` and set
   "Start in" to that folder.
6. In the app, **re-select the devices**. The saved device IDs belong to the old
   PC; the app falls back to defaults (CABLE Input for output) when they don't exist.
7. In Teams/Zoom: microphone = **CABLE Output**, noise suppression **Low/Off**
   (Zoom: turn off "Automatically adjust microphone volume", optionally enable
   "Original sound for musicians").
8. Check the English voices exist (Male/Female in the Voice list). If none are
   installed the Speak button is disabled.

---

## 9. Ideas and open items (not done, only discussed)

- Auto-refresh device lists when headphones are plugged in or out (offered, not requested yet).
- Global hotkey to toggle effects during a meeting (offered at the start).
- Background image picker / see-through panels (discussed; owner chose solid orange).
- Self-contained single-file exe and code signing (see section 3).
- Swedish TTS voices (needs the Windows Swedish speech pack plus maybe culture filtering changes).
- Version control: the project is **not in git**. Setting it up was offered.

---

## 10. Short history of the project

1. Built the WinForms + NAudio app with pitch, echo, reverb, robot, presets and a
   monitor option; installed the .NET 8 SDK; the owner installed VB-Cable.
2. Robot made more metallic (comb resonator, Metallic/Tone sliders). Added the
   Cylon, Donald Duck, Darth Vader and Yoda presets; removed Alien, Cave and Stadium.
   Robot frequency can be 0.
3. Active preset highlight with a "*" for modified presets.
4. Text to speech (English, Male/Female); later "Apply effects" removed (always applied).
5. Background image → glitches fixed → finally replaced by solid light orange.
6. Project moved from a temporary folder to `C:\AllFiles\SashaBrain\Local\VoiceFx`.
   Files rearranged into `src`. Settings moved from AppData to `settings\settings.json`
   at the root.
7. Self-contained build tried in a copy; the copy was deleted by the owner.
8. Sections aligned to equal width; window width locked; Start/Stop renamed Enable/Disable.
9. App icon added; sections made collapsible (except Devices); presets split into
   Default/Custom with a divider; the owner's tuned Darth Vader saved as the preset.
10. Add/delete custom presets; Waveform box; "Add corporate jargon" button.
