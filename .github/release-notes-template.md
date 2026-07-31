## MeetingScribe {{VERSION}}

Download `MeetingScribe.exe` below, put it anywhere, and run it. It appears in the system
tray. No installer, no .NET runtime required and no supporting files — the .NET runtime and
the NAudio and Whisper.net native libraries are all bundled inside the one executable.

On first use it downloads the Whisper speech model (~500 MB by default) into
`%APPDATA%\MeetingScribe\models`. Settings, logs and recordings also live under
`%APPDATA%\MeetingScribe`.

Use **Start with Windows** in the tray menu to launch it at sign-in.

Windows will warn that the publisher is unknown, because this binary is not code-signed.

| | |
|---|---|
| Platform | Windows 10/11 x64 |
| Size | {{SIZE}} MB |
| SHA-256 | `{{SHA}}` |

Verify your download with:

```powershell
Get-FileHash .\MeetingScribe.exe -Algorithm SHA256
```

Recording a call may require the consent of everyone on it. Please check the rules that
apply where you are.
