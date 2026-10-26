# VoiceFx

Real-time voice effects for Microsoft Teams and other meeting apps.

## How it works

```
Real mic → VoiceFx (effects) → "CABLE Input"  ⇒  "CABLE Output" → Teams microphone
```

Teams can only pick a microphone device, so VoiceFx plays your processed voice into
VB-Cable, a free virtual audio cable that Teams sees as a microphone.

## One-time setup

1. Download VB-Cable from https://vb-audio.com/Cable/, unzip it, right-click
   `VBCABLE_Setup_x64.exe` → **Run as administrator** → Install Driver. Reboot if asked.
2. Check that Windows did not make "CABLE Output" your default recording device
   (Settings → System → Sound → Input). Keep your real mic as the default.

## Use

1. Double-click the **VoiceFx** shortcut in this folder
   (it starts `src\bin\Release\net8.0-windows\VoiceFx.exe`). Requires the .NET 8 Desktop Runtime.
2. Microphone = your real mic, Send to (Teams) = **CABLE Input**. Press **Enable**.
3. In Teams: Settings → Devices → Microphone → **CABLE Output (VB-Audio Virtual Cable)**.
4. Set Teams noise suppression to **Low** or **Off**. Otherwise Teams may filter out the effects.
5. Tick **Hear myself on** to listen to the result. Use headphones to avoid feedback.

Settings are saved in `settings\settings.json` in this folder.

## Build

The source code is in the `src` folder. From there:

```
dotnet build -c Release
```

## Contact

anders.lejczak@colacola.se