<#
.SYNOPSIS
    Builds MeetingScribe as a single, portable MeetingScribe.exe.

.DESCRIPTION
    Publishes one self-contained win-x64 executable to .\publish. Everything the app
    needs is bundled inside it: the managed assemblies, the .NET runtime, and the
    NAudio and Whisper.net native DLLs. Copy the exe anywhere on any Windows x64
    machine and run it - no .NET install and no supporting files required.

    The result is roughly 84 MB. Most of that is the Whisper.net CPU and Vulkan
    native runtimes, not the app itself.

    The build is always self-contained. A framework-dependent single file build is
    actually *larger*, because single-file compression is only supported for
    self-contained publishes.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER OutputPath
    Where to write the exe. Defaults to .\publish.

.PARAMETER Version
    Version to stamp into the assembly, e.g. 1.2.0. Defaults to the value in the csproj.
    CI passes the release tag here so MeetingScribe.exe --version matches the release.

.PARAMETER SkipTest
    Skip the post-build smoke test that runs the exe from a temporary directory.

.EXAMPLE
    .\build.ps1
    Builds publish\MeetingScribe.exe.

.EXAMPLE
    .\build.ps1 -OutputPath D:\tools
    Builds straight into another folder.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$OutputPath,
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,
    [switch]$SkipTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$project = Join-Path $root 'src\MeetingScribe\MeetingScribe.csproj'

if (-not $OutputPath) { $OutputPath = Join-Path $root 'publish' }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK not found on PATH. Install the .NET 10 SDK from https://dotnet.microsoft.com/download'
}

Write-Host ''
Write-Host 'Building MeetingScribe' -ForegroundColor Cyan
Write-Host "  configuration : $Configuration"
Write-Host "  runtime       : win-x64 (self-contained, single file)"
if ($Version) { Write-Host "  version       : $Version" }
Write-Host "  output        : $OutputPath"
Write-Host ''

# A stale publish folder makes it impossible to tell whether the build actually
# produced a fresh exe, so start clean.
#if (Test-Path $OutputPath) {
#    Remove-Item -Recurse -Force $OutputPath
#}

# Incremental builds happily reuse an assembly stamped with a previous -Version, which
# would silently ship a mislabelled binary. Dropping the compiled output forces a real
# recompile. obj\project.assets.json is left alone so restore is not repeated.
foreach ($dir in @("bin\$Configuration", "obj\$Configuration")) {
    $stale = Join-Path (Split-Path $project) $dir
    if (Test-Path $stale) { Remove-Item -Recurse -Force $stale }
}

$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', 'win-x64',
    '-o', $OutputPath,
    '--nologo',
    '-p:SelfContained=true',
    '-p:PublishSingleFile=true',
    '-p:EnableCompressionInSingleFile=true'
)

if ($Version) {
    $publishArgs += "-p:Version=$Version"
}

& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# Keep the optional recording controls beside the published executable.
foreach ($script in @('start-recording.bat', 'stop-recording.bat')) {
    Copy-Item (Join-Path $root $script) (Join-Path $OutputPath $script) -Force
}

$exe = Join-Path $OutputPath 'MeetingScribe.exe'
if (-not (Test-Path $exe)) {
    throw "Build reported success but $exe was not produced."
}

# The whole point of a single-file build is that no application dependencies are
# needed at runtime. The batch files are intentional companions, not publish strays.
$companionFiles = @(
    (Join-Path $OutputPath 'start-recording.bat'),
    (Join-Path $OutputPath 'stop-recording.bat')
)
$strays = @(Get-ChildItem $OutputPath -Recurse -File | Where-Object {
    $_.FullName -ne $exe -and $_.FullName -notin $companionFiles
})

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ''
Write-Host 'Build succeeded' -ForegroundColor Green
Write-Host "  $exe  ($sizeMb MB)"

if ($strays.Count -gt 0) {
    Write-Host ''
    Write-Warning "$($strays.Count) extra file(s) were emitted alongside the exe and must be kept with it:"
    $strays | ForEach-Object { Write-Host "    $($_.Name)" }
}

if (-not $SkipTest) {
    Write-Host ''
    Write-Host 'Smoke test: running --version from a different working directory...' -ForegroundColor Cyan

    # Run from a temp dir to prove the exe does not depend on its publish folder.
    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("meetingscribe-smoke-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch | Out-Null
    try {
        $stdout = Join-Path $scratch 'out.txt'
        $stderr = Join-Path $scratch 'err.txt'
        $proc = Start-Process -FilePath $exe -ArgumentList '--version' -WorkingDirectory $scratch `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -NoNewWindow
        $proc | Wait-Process -Timeout 60

        $output = (
            (Get-Content $stdout -Raw -ErrorAction SilentlyContinue) +
            (Get-Content $stderr -Raw -ErrorAction SilentlyContinue)
        ).Trim()

        if ($proc.ExitCode -ne 0) {
            Write-Warning "Smoke test exited with code $($proc.ExitCode). Output:`n$output"
        }
        else {
            Write-Host "  OK  $output" -ForegroundColor Green
        }
    }
    finally {
        Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host 'Copy MeetingScribe.exe and the recording batch files anywhere and run it. It lives in the system tray.' -ForegroundColor Cyan
Write-Host 'Config and logs are written to %APPDATA%\MeetingScribe.'
Write-Host ''
