# Copilot instructions — MeetingScribe

A Windows system-tray app that detects Microsoft Teams calls, records system + microphone
audio, transcribes locally with Whisper, and writes the transcript as a Markdown note into
an Obsidian vault. Everything runs offline and free — no cloud services, no API keys.

## Repo shape

```
build.ps1                   Single-file publish + smoke test (the build entry point that matters)
MeetingScribe.slnx
src/MeetingScribe/
  Program.cs                Entry point: single-instance mutex, exception hooks, CLI dispatch
  TrayApplicationContext.cs NotifyIcon, menu, wiring
  Configuration/            AppConfig (settings model), ConfigStore (JSON load/save + vault guess)
  Detection/                Two independent call-detection probes + CallMonitor debouncer
  Recording/                Dual-track WASAPI capture
  Transcription/            Audio conversion to 16 kHz mono + Whisper.net wrapper
  Notes/                    Markdown + YAML frontmatter rendering
  Pipeline/                 MeetingPipeline orchestration, AppState
  Diagnostics/              SelfTest — the CLI verification harness
  Infrastructure/           Log, Paths, FileNames, TrayIcons, StartupRegistration
```

Runtime state lives in `%APPDATA%\MeetingScribe\` (`config.json`, `logs\`, `models\`,
`recordings\`). Nothing user-specific is stored in the repo.

## Build, run, verify

```powershell
dotnet build                                  # fast inner loop
dotnet run --project src\MeetingScribe        # launches the tray app
.\build.ps1                                   # single self-contained publish\MeetingScribe.exe
```

`build.ps1` is always self-contained on purpose. A framework-dependent single-file build is
*larger* (~122 MB vs ~84 MB) because `EnableCompressionInSingleFile` only applies to
self-contained publishes. Don't "optimise" that back.

There is **no test project**. Verification is done through the CLI harness in
`Diagnostics/SelfTest.cs`:

```powershell
MeetingScribe.exe --version              # cheap, no side effects (build.ps1 smoke test uses this)
MeetingScribe.exe --help
MeetingScribe.exe --selftest 10          # devices, both detection probes, a 10s two-track recording
MeetingScribe.exe --transcribe <file>    # conversion + Whisper + note preview into a sandbox vault
```

Any argument starting with `--` routes to `SelfTest.RunAsync` and attaches to the parent
console. Reports are written to `%TEMP%\meetingscribe-selftest.txt`.

**Always verify audio/transcription changes by actually running the harness.** Synthesising
test speech is easy and beats guessing:

```powershell
Add-Type -AssemblyName System.Speech
$s = New-Object System.Speech.Synthesis.SpeechSynthesizer
$s.SetOutputToWaveFile("$env:TEMP\t.wav"); $s.Speak("test sentence"); $s.Dispose()
```

## Hard-won gotchas — do not regress these

**Loopback capture goes silent.** `WasapiLoopbackCapture` delivers *no* buffers when nothing
is playing, which desyncs the two tracks. `MeetingRecorder.StartSilenceKeepAlive` fixes this
by playing a `SilenceProvider` through `WasapiOut`. It must use the *device mix format*, not
an arbitrary one, or `WasapiOut` rejects it.

**Two tracks, not one.** Mic = "Me", loopback = "Participants". This gives speaker
attribution with no diarization model. Don't collapse it into a single mixed track.

**Whisper hallucinates on silence** ("Thank you.", "[BLANK_AUDIO]"). Three independent
filters guard against this: an RMS loudness envelope computed during conversion
(`SilenceRmsThreshold`), `MinimumProbability`, and a literal/regex hallucination list. Keep
all three.

**Detection must not depend on the Teams UI.** Both probes are deliberately UI-independent so
Teams updates cannot break them:

1. WASAPI — active audio sessions on capture endpoints, PID matched against a process regex.
2. Registry — `HKCU\...\CapabilityAccessManager\ConsentStore\microphone`; a
   `LastUsedTimeStop` of `0` means in use *right now*. Packaged apps are direct subkeys;
   unpackaged live under `NonPackaged` with `#` substituted for `\`.

Either probe alone is enough to trigger. Widening `Detection.ProcessNamePattern` is how you
add Zoom/Slack/browser calls — no code change needed.

**Never dispose `Process` objects inside a LINQ predicate** that a later `.Select` still
reads from. This shipped as a real bug in `MeetingTitleResolver`.

**Build quirks:** `[LibraryImport]` requires `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`.
`AudioSessionState` lives in `NAudio.CoreAudioApi.Interfaces`, not `NAudio.CoreAudioApi`.
Don't name a lambda parameter `_` if the body also does `_ = SomeOutParamCall(...)`.

**Single-file publish needs `IncludeNativeLibrariesForSelfExtract`.** NAudio and Whisper.net
ship native DLLs; without it they land beside the exe instead of in the bundle. `build.ps1`
warns if any file is emitted next to the exe — treat that warning as a failure.

## Known constraints

**Phi Silica / Windows AI summarization is blocked.** `LanguageModel.GetReadyState()` throws
`UnauthorizedAccessException` from an unpackaged app. Per Microsoft Learn this needs MSIX
package identity declaring the `systemAIModels` capability, plus a Limited Access Feature
token. The model *is* provisioned on Copilot+ hardware and no policy blocks it — packaging is
the only gap. Phi Silica is also being replaced by Aion Instruct (Phi Silica removed
Nov 2026); Aion drops the LAF token requirement, so revisit then. Don't burn time
re-diagnosing this from scratch.

If summarization is wanted sooner, the unblocked route is an `ISummarizer` abstraction with a
local OpenAI-compatible backend (Foundry Local / Ollama / LM Studio). These models have small
context windows, so any implementation needs map-reduce chunking of long transcripts.

**Whisper backend is unverified.** Both `Whisper.net.Runtime` (CPU) and
`Whisper.net.Runtime.Vulkan` are referenced. Whisper.net auto-probes and falls back silently,
so which one actually loads has never been confirmed. `Whisper.net.LibraryLoader.RuntimeOptions`
exposes `RuntimeLibraryOrder` and `LoadedLibrary` as *static* members (not `.Instance`) if this
needs pinning down. Current measured throughput is ~15-17x realtime, which is already fine.

## Conventions

- Comment only what needs clarifying — the *why*, especially for the workarounds above.
  Don't narrate what the code plainly does.
- Config is the extension point. Prefer adding an `AppConfig` property with a sensible default
  over hard-coding behaviour; document new settings in the README's Configuration section.
- Keep the pipeline single-consumer. `MeetingPipeline` uses a `Channel<RecordingResult>` so
  transcription never runs concurrently with itself.
- Log through `Infrastructure.Log`, never `Console.WriteLine`, outside `SelfTest`.
- Update `README.md` alongside behaviour changes; it is the user-facing documentation.

## Ethics

Recording calls has legal and consent implications that vary by jurisdiction. The README has
a consent section — keep it, and don't add anything that hides or disguises that recording is
happening.
