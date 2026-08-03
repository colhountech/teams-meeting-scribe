# MeetingScribe

A Windows system-tray app that notices when you're on a Microsoft Teams call, records it,
transcribes it **locally** with Whisper, and files the transcript as a markdown note in your
Obsidian vault.

Everything runs offline. No cloud service, no account, no API key, no per-minute limits.

```
Teams call detected  →  record 2 tracks  →  Whisper (local)  →  markdown note in your vault
                        mic + system         speaker-labelled
```

## Why two audio tracks

The microphone track is you; the system-output track is everyone else. Transcribing them
separately gives you speaker attribution (`Me:` / `Participants:`) without running a
diarization model, and it is far more accurate than trying to split a single mixed track.

If you listen on speakers rather than a headset, your microphone also picks up everyone else,
so the same sentence lands on both tracks. MeetingScribe compares the two transcripts and drops
the microphone copy, keeping the clean loopback one — see `Transcription.SuppressMicrophoneEcho`.

## Requirements

- Windows 10/11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build. The published exe is
  self-contained, so target machines need nothing installed.
- ~500 MB of disk for the Whisper model, downloaded automatically on first use

## Build and run

For development:

```powershell
git clone <this repo>
cd meeting-scribe
dotnet build
dotnet run --project src\MeetingScribe
```

To produce the shippable executable:

```powershell
.\build.ps1
```

This writes a single self-contained `publish\MeetingScribe.exe` (~84 MB) and smoke-tests it
by running it from a temporary directory. Copy that one file anywhere on any Windows x64
machine and double-click it — no .NET install, no DLLs beside it. The .NET runtime, NAudio
and both Whisper.net native runtimes (CPU and Vulkan) are all bundled inside and unpacked
to a cache under `%TEMP%` on first launch.

Useful switches:

```powershell
.\build.ps1 -OutputPath D:\tools   # build straight into another folder
.\build.ps1 -Configuration Debug   # debug symbols, no optimisation
.\build.ps1 -SkipTest              # skip the post-build smoke test
```

The build is deliberately always self-contained. A framework-dependent single-file build
comes out *larger* (~122 MB), because single-file compression only applies to
self-contained publishes.

Once it is running, use **Start with Windows** in the tray menu so it launches at sign-in.

## Releases

Tagging a version builds the executable on a Windows runner and publishes it as a GitHub
Release:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

The workflow (`.github/workflows/release.yml`) stamps the tag version into the assembly,
verifies the published output really is a single correctly-versioned file, and attaches it
with its SHA-256 in the release notes. You can also run it manually from the **Actions** tab
to produce a draft release for testing.

The binary is not code-signed, so Windows SmartScreen will warn about an unknown publisher
on first run.

## First run

1. The app creates `%APPDATA%\MeetingScribe\config.json` and tries to find your Obsidian vault.
2. Open the tray menu → **Open settings file** and check `Vault.Root` points at the right vault.
3. Join a Teams call. The tray icon turns red while recording.
4. When the call ends, the icon turns blue while transcribing, then a notification appears with
   the note name. Click it to open the note.

## Tray menu

| Item | What it does |
|---|---|
| *(status line)* | Current state — watching, recording, transcribing |
| Start / Stop recording now | Manual override, independent of detection |
| Pause monitoring | Stops automatic detection until you re-enable it |
| Transcribe an audio file… | Import any existing recording (wav/mp3/m4a) and file it as a note |
| Open last note / vault / recordings / settings / log | Shortcuts |
| Start with Windows | Per-user registry `Run` entry |

## How detection works

Two independent signals, either of which counts as "in a call":

1. **WASAPI audio sessions** — asks the audio engine whether a Teams process holds an *active*
   session on a microphone endpoint.
2. **Microphone privacy markers** — the registry keys behind Windows' "app is using your
   microphone" indicator (`…\CapabilityAccessManager\ConsentStore\microphone`). A
   `LastUsedTimeStop` of `0` means in use right now.

Both are debounced (`StartAfterConsecutiveHits` / `StopAfterConsecutiveMisses`) so a brief
notification sound or a moment of device switching cannot start or stop a recording.

Neither approach depends on Teams' window layout or UI, so Teams updates will not break it.
Because detection is microphone-based it also works for Teams in a browser, Slack huddles,
Zoom, and anything else you add to `Detection.ProcessNamePattern`.

## Configuration

`%APPDATA%\MeetingScribe\config.json`. Unknown/missing keys are re-added with defaults on start.

### Vault

| Key | Default | Notes |
|---|---|---|
| `Vault.Root` | auto-detected | Folder containing `.obsidian` |
| `Vault.NotePathTemplate` | `Meetings\{date:yyyy-MM}\{date:yyyy-MM-dd} {title}.md` | Tokens: `{date:FMT}`, `{time:FMT}`, `{title}` |
| `Vault.AppendToExistingNote` | `true` | Append a section instead of creating `Note (2).md` |

### Detection

| Key | Default | Notes |
|---|---|---|
| `Detection.ProcessNamePattern` | `^(ms-teams\|Teams\|Teams1)$` | Regex over process names |
| `Detection.PollSeconds` | `2` | |
| `Detection.StartAfterConsecutiveHits` | `2` | ~4 s before recording starts |
| `Detection.StopAfterConsecutiveMisses` | `6` | ~12 s of silence before stopping |
| `Detection.UseAudioSessions` | `true` | |
| `Detection.UseMicrophoneConsentRegistry` | `true` | |
| `Detection.IgnoredWindowTitlePattern` | *(empty)* | Regex over each pipe-separated part of the Teams window caption; matches are boilerplate, not the meeting title. Empty = built-in list |

### Recording

| Key | Default | Notes |
|---|---|---|
| `Recording.KeepAudioFiles` | `false` | Delete the WAVs once the note is written |
| `Recording.MinimumMeetingSeconds` | `60` | Shorter recordings are discarded |
| `Recording.MaxMeetingHours` | `6` | Safety cut-off |
| `Recording.SystemAudioDeviceName` | `""` | Substring match; empty = default device |
| `Recording.MicrophoneDeviceName` | `""` | Substring match; empty = default comms device |

### Transcription

| Key | Default | Notes |
|---|---|---|
| `Transcription.Model` | `SmallEn` | `TinyEn`, `BaseEn`, `SmallEn`, `Medium`, `LargeV3Turbo`… |
| `Transcription.Language` | `en` | `auto` to detect |
| `Transcription.Threads` | `0` | 0 = half your logical processors |
| `Transcription.SelfSpeakerLabel` | `Me` | |
| `Transcription.OthersSpeakerLabel` | `Participants` | |
| `Transcription.MergeGapSeconds` | `4` | Joins consecutive lines from one speaker |
| `Transcription.SilenceRmsThreshold` | `0.004` | Drops Whisper's silence hallucinations |
| `Transcription.MinimumProbability` | `0.25` | Drops low-confidence segments |
| `Transcription.SuppressMicrophoneEcho` | `true` | Drops mic lines that repeat the participants track (speaker bleed) |
| `Transcription.EchoToleranceSeconds` | `2.5` | How far the two tracks' segment boundaries may differ |
| `Transcription.EchoSimilarityThreshold` | `0.6` | Fraction of words that must match to count as echo |

Model trade-off, roughly, on a modern laptop CPU:

| Model | Size | Speed | Use when |
|---|---|---|---|
| `BaseEn` | 150 MB | very fast | Quick gist, clear audio |
| `SmallEn` | 500 MB | ~15× realtime | **Default** — good balance |
| `Medium` | 1.5 GB | ~4× realtime | Accents, poor audio |
| `LargeV3Turbo` | 1.6 GB | ~6× realtime | Best accuracy |

## Output

```markdown
---
type: meeting
title: "Weekly project sync"
date: 2026-07-31
start: 10:00
end: 10:42
duration: 42m
source: meeting-scribe
tags:
  - meeting
  - teams
  - auto-transcribed
---

# Weekly project sync

> [!info] Recorded 2026-07-31 10:00–10:42 (42m) · transcribed locally with `SmallEn`

## Transcript

**[00:00:03] Participants:** Good morning everyone, thanks for joining the weekly project sync.

**[00:00:14] Me:** Morning. I've got the migration timeline ready to walk through.
```

## Diagnostics

```powershell
# Version, .NET runtime and architecture — the quickest "does this exe work here?" check
MeetingScribe.exe --version

# Full command list
MeetingScribe.exe --help

# Devices, detection probes, a short two-track test recording, and conversion stats
MeetingScribe.exe --selftest 10

# Run a file through conversion + Whisper and preview the generated note
MeetingScribe.exe --transcribe "C:\path\to\audio.wav"
```

`--selftest` and `--transcribe` write a report to `%TEMP%\meetingscribe-selftest.txt`.
Runtime logs live in `%APPDATA%\MeetingScribe\logs\`.

## Troubleshooting

**Recording never starts.** Run `--selftest` during a call and check the two probe lines. If
both are `False`, your Teams process name may differ — check Task Manager and update
`Detection.ProcessNamePattern`.

**The system track is silent.** Teams may be playing to a different device than the Windows
default. Set `Recording.SystemAudioDeviceName` to part of the right device name; `--selftest`
lists them.

**Transcript full of "Thank you." / "[BLANK_AUDIO]".** That is Whisper hallucinating on silence.
Raise `Transcription.SilenceRmsThreshold` (try `0.01`).

**Transcription is slow.** Drop to `BaseEn`, or raise `Transcription.Threads`.

**Everything is said twice, once by "Participants" and once by "Me".** You are listening on
speakers, so your microphone re-records the meeting. Echo suppression removes these duplicates
automatically by comparing the two tracks; if some slip through, lower
`Transcription.EchoSimilarityThreshold` (try `0.45`) or raise `Transcription.EchoToleranceSeconds`.
If it is over-eager and eats things you actually said, raise the threshold instead. A headset
avoids the problem entirely and always gives the cleanest speaker attribution.

## A note on consent

Recording a conversation without telling the other participants may be unlawful where you are,
and is very likely against your employer's policy. The tray icon is deliberately red and obvious
while recording. Tell people you are recording.

## Project layout

```
src/MeetingScribe/
  Program.cs                  Entry point, single-instance guard, CLI switches
  TrayApplicationContext.cs   Tray icon, menu, wiring
  Configuration/              Settings model and JSON store
  Detection/                  Audio-session + registry probes, debounced monitor, title resolver
  Recording/                  Dual-track WASAPI capture, device selection
  Transcription/              16 kHz mono conversion, loudness envelope, Whisper wrapper
  Notes/                      Markdown/frontmatter rendering
  Pipeline/                   record → transcribe → write orchestration and queue
  Diagnostics/                --selftest and --transcribe
  Infrastructure/             Paths, logging, tray icons, startup registration
```

Built with [NAudio](https://github.com/naudio/NAudio) and
[Whisper.net](https://github.com/sandrohanea/whisper.net) (whisper.cpp).
