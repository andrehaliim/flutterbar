<#
.SYNOPSIS
  Builds the StatefulBar release folder (self-contained, English only) and its portable zip.

  Output, under artifacts\ at the repo root:
    publish\StatefulBar\                     StatefulBar.exe (widget) and sbar.exe (CLI) sharing one .NET runtime
    StatefulBar-<version>-<runtime>.zip      that folder, with a top-level "StatefulBar" folder inside

  The widget is published first and the CLI second, into the same folder: the widget project references the CLI, so
  its publish copies a framework-dependent sbar.runtimeconfig.json, which the CLI publish then replaces with the
  self-contained one. The folder name inside the zip has no version, so "Start with Windows" and the PATH entry keep
  pointing at the same place after an update.

  With -Installer it also compiles packaging\inno\StatefulBar.iss into StatefulBar-<version>-win-x64-setup.exe
  (needs Inno Setup 6: winget install --id JRSoftware.InnoSetup --exact --scope user).

  With -Msix it also builds the Microsoft Store package:
    msix\layout\                             the release folder plus packaging\msix (manifest and logos); register it
                                             for a local test with Add-AppxPackage -Register msix\layout\AppxManifest.xml
    StatefulBar-<version>-win-x64.msix       the layout packed by makeappx.exe from the Windows SDK. Without the SDK
                                             only the layout is built; on GitHub Actions a missing makeappx.exe is an error.
  The package is not signed: the Store signs it, and a local test registers the layout instead of installing the file.

  The version comes from <Version> in Directory.Build.props. Compatible with Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Runtime       .NET runtime identifier (default win-x64).
.PARAMETER Configuration Build configuration (default Release).
.PARAMETER Installer     Also build the Inno Setup installer (win-x64 only).
.PARAMETER Msix          Also build the Microsoft Store package layout and, with the Windows SDK, the .msix (win-x64 only).
#>
param(
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [switch]$Installer,
    [switch]$Msix
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..'))
$artifacts = Join-Path $root 'artifacts'
$folder = Join-Path $artifacts 'publish\StatefulBar'

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> found in Directory.Build.props.' }
$zip = Join-Path $artifacts "StatefulBar-$version-$Runtime.zip"

function Invoke-Publish([string]$project) {
    Write-Host "Publishing $project ($Configuration, $Runtime)"
    & dotnet publish (Join-Path $root $project) -c $Configuration -r $Runtime --self-contained `
        -p:SatelliteResourceLanguages=en -o $folder -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed (exit $LASTEXITCODE)." }
}

if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
New-Item -ItemType Directory -Force $folder | Out-Null

Invoke-Publish 'src\StatefulBar.App\StatefulBar.App.csproj'
Invoke-Publish 'src\StatefulBar.Cli\StatefulBar.Cli.csproj' # second, see above

# Licenses: StatefulBar's own, and the bundled .NET runtime's (MIT, with its third-party notices).
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $folder 'LICENSE.txt')
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
$licenses = New-Item -ItemType Directory -Force (Join-Path $folder 'licenses')
foreach ($name in 'LICENSE.txt', 'ThirdPartyNotices.txt') {
    $source = Join-Path $dotnetRoot $name
    if (-not (Test-Path $source)) { throw "$source not found: the .NET runtime license must ship with the self-contained build." }
    Copy-Item $source (Join-Path $licenses "dotnet-$name")
}

# Checks: both exes present, the CLI is self-contained and actually starts on the bundled runtime.
foreach ($exe in 'StatefulBar.exe', 'sbar.exe') {
    if (-not (Test-Path (Join-Path $folder $exe))) { throw "$exe is missing from $folder." }
}
$cliConfig = Get-Content (Join-Path $folder 'sbar.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $cliConfig.runtimeOptions.includedFrameworks) {
    throw 'sbar.runtimeconfig.json is framework-dependent: the CLI publish did not run after the widget publish.'
}
& (Join-Path $folder 'sbar.exe') status | Out-Null
$cliExit = $LASTEXITCODE
# 0 = session running, 2 = widget not running, 3 = no session: all mean the CLI loaded its runtime and ran.
if (@(0, 2, 3) -notcontains $cliExit) { throw "sbar.exe status exited with ${cliExit}: the CLI does not start from the publish folder." }

if (Test-Path $zip) { Remove-Item $zip -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)

$setup = $null
if ($Installer) {
    if ($Runtime -ne 'win-x64') { throw "The installer is x64 only; run -Installer with -Runtime win-x64." }
    $iscc = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        throw 'Inno Setup 6 (ISCC.exe) not found. Install it: winget install --id JRSoftware.InnoSetup --exact --scope user'
    }
    $setup = Join-Path $artifacts "StatefulBar-$version-win-x64-setup.exe"
    if (Test-Path $setup) { Remove-Item $setup -Force }
    Write-Host "Building the installer with $iscc"
    & $iscc /Q "/DAppVersion=$version" (Join-Path $root 'packaging\inno\StatefulBar.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)." }
    if (-not (Test-Path $setup)) { throw "ISCC finished but $setup is missing." }
}

$layout = $null
$msixFile = $null
if ($Msix) {
    if ($Runtime -ne 'win-x64') { throw "The Store package is x64 only; run -Msix with -Runtime win-x64." }
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "<Version> must be major.minor.patch for the Store package, got '$version'." }

    $layout = Join-Path $artifacts 'msix\layout'
    if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
    New-Item -ItemType Directory -Force (Split-Path -Parent $layout) | Out-Null
    Copy-Item $folder $layout -Recurse
    Copy-Item (Join-Path $root 'packaging\msix\Assets') (Join-Path $layout 'Assets') -Recurse

    # The Store wants four parts with the last one 0.
    $manifestPath = Join-Path $layout 'AppxManifest.xml'
    $manifest = New-Object System.Xml.XmlDocument
    $manifest.PreserveWhitespace = $true
    $manifest.Load((Join-Path $root 'packaging\msix\AppxManifest.xml'))
    $manifest.DocumentElement['Identity'].SetAttribute('Version', "$version.0")
    $manifest.Save($manifestPath)

    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $makeappx = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($makeappx) {
        $msixFile = Join-Path $artifacts "StatefulBar-$version-win-x64.msix"
        if (Test-Path $msixFile) { Remove-Item $msixFile -Force }
        Write-Host "Packing the Store package with $makeappx"
        & $makeappx pack /d $layout /p $msixFile /o | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed (exit $LASTEXITCODE). Run it by hand to see why: `"$makeappx`" pack /d `"$layout`" /p `"$msixFile`" /o" }
        if (-not (Test-Path $msixFile)) { throw "makeappx finished but $msixFile is missing." }
    }
    elseif ($env:GITHUB_ACTIONS -eq 'true') {
        throw "makeappx.exe not found under ${kits}: the Windows SDK is expected on the GitHub Actions runner."
    }
}

$files = Get-ChildItem $folder -Recurse -File
$folderMb = [Math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
$zipMb = [Math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ''
Write-Host "StatefulBar $version ($Runtime)"
Write-Host "  Folder: $folder ($($files.Count) files, $folderMb MB)"
Write-Host "  Zip:    $zip ($zipMb MB)"
if ($setup) { Write-Host "  Setup:  $setup ($([Math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" }
if ($layout) { Write-Host "  Store package layout: $layout (version $version.0)" }
if ($msixFile) { Write-Host "  Msix:   $msixFile ($([Math]::Round((Get-Item $msixFile).Length / 1MB, 1)) MB)" }
elseif ($Msix) { Write-Host '  Msix:   not packed (makeappx.exe from the Windows SDK was not found); the layout can still be registered for a test' }
Write-Host "  CLI check: sbar.exe status exited with $cliExit"

# The CLI check above leaves $LASTEXITCODE at 2 or 3 on purpose; callers such as the GitHub Actions pwsh shell end
# with "exit $LASTEXITCODE", so report success explicitly. Every failure above throws before reaching this line.
exit 0
