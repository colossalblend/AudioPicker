# AudioPicker

Windows audio router with a UI: capture system-wide or per-app audio
(WASAPI process loopback, Win10 2004+) and route it to any output device —
typically **CABLE Input (VB-Cable)** so Discord / any voice chat sees it as a
microphone.

Features:

- pick several apps at once (ctrl/shift), they get mixed into one stream
- live "who is playing sound right now" panel with per-process level meters
- self-monitor (hear the stream yourself) — own audio is excluded from the
  capture so it can never feed back
- stream gain, dark animated UI
- single self-contained exe built by GitHub Actions

## Build

```bat
dotnet publish src/AudioPicker -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Requires .NET 8 SDK and Windows 10 2004+.

## Use

1. launch, pick source(s) in the list (or click a name in the live panel)
2. output defaults to CABLE Input
3. start — in Discord select microphone **CABLE Output**

Engine: WASAPI process loopback via `ActivateAudioInterfaceAsync`,
per-session peak meters via `IAudioMeterInformation`, playback via NAudio.
