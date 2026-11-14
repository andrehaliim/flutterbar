# StatefulBar development

How to build, test, and package StatefulBar. For using the app, see the [README](../README.md).

## Contents

- [Requirements](#requirements)
- [Build and test](#build-and-test)
- [Release package](#release-package)
- [Installer](#installer)
- [Microsoft Store package](#microsoft-store-package)
- [CI and releases](#ci-and-releases)
- [Tests](#tests)
- [RAM usage](#ram-usage)
- [Technical notes](#technical-notes)

## Requirements

- Windows 10 or 11
- .NET 10 SDK
- For the installer: Inno Setup 6 (`winget install --id JRSoftware.InnoSetup --exact --scope user`)
- For the integration tests: fvm on PATH and Flutter 3.22.3

## Build and test

```
dotnet build StatefulBar.sln
dotnet test
```

Release:

```
dotnet build StatefulBar.sln -c Release
```

| Exe | Location (Release) |
|---|---|
| Widget | `src\StatefulBar.App\bin\Release\net10.0-windows\StatefulBar.exe` |
| CLI | `src\StatefulBar.Cli\bin\Release\net10.0-windows\sbar.exe` (a copy is also in the `StatefulBar.App` Release folder) |

For Debug, replace `Release` with `Debug`. Only one StatefulBar instance can run; starting it again just flashes the
existing widget, so exit the running widget (right-click > "Exit") before starting a fresh build. Diagnostic log:
`%APPDATA%\StatefulBar\diag.log`.

The version, company, and copyright of every exe come from `Directory.Build.props`.

## Release package

```
.\tools\publish.ps1
```

Builds a self-contained win-x64 release (no .NET install needed on the user's PC), English only, with the version from
`Directory.Build.props`:

| Output | Contents |
|---|---|
| `artifacts\publish\StatefulBar\` | `StatefulBar.exe`, `sbar.exe`, the shared .NET runtime, `LICENSE.txt`, `licenses\` (.NET licenses) |
| `artifacts\StatefulBar-<version>-win-x64.zip` | The folder above, as a single `StatefulBar\` folder inside the zip |

- The widget is published first and the CLI second, into the same folder: the widget project references the CLI, so its
  publish copies a framework-dependent `sbar.runtimeconfig.json`, which the CLI publish then replaces with the
  self-contained one.
- The script fails when the published `sbar.exe` is not self-contained or does not start.
- The folder inside the zip has no version on purpose, so the "Start with Windows" and PATH entries keep pointing at the
  same place after an update.
- `artifacts\` is ignored by git.

## Installer

```
.\tools\publish.ps1 -Installer
```

Also compiles `packaging\inno\StatefulBar.iss` into `artifacts\StatefulBar-<version>-win-x64-setup.exe` (needs Inno Setup 6,
see [Requirements](#requirements)).

- Installs per user to `%LOCALAPPDATA%\Programs\StatefulBar`, no admin rights needed. A Start menu shortcut is always created.
- Tasks (checked by default): `startup` (Start with Windows), `addtopath` (Add the sbar CLI to PATH), `desktopicon`, plus
  "Launch StatefulBar" on the last page. The Run value and PATH entry use the same format as the app's menu items, so the
  menu shows them as on right away.
- An update (installing a newer version over it) closes the widget running from the install folder through Restart Manager,
  including its flutter session, and clears the old runtime files first.
- Uninstall closes that widget; removes the files, shortcuts, the Run value (only when it points at the install folder) and
  its PATH entry; then asks whether to also remove `%APPDATA%\StatefulBar` (default No; a silent uninstall always keeps it).
- Never change `AppId` in the `.iss`: it ties updates and the uninstaller to earlier installs.
- `Setup.exe` is not code-signed yet, so SmartScreen warns on first run.

Unattended install, e.g. for testing: `StatefulBar-<version>-win-x64-setup.exe /VERYSILENT /TASKS="startup,addtopath"`;
uninstall with `"%LOCALAPPDATA%\Programs\StatefulBar\unins000.exe" /VERYSILENT`.

## Microsoft Store package

```
.\tools\publish.ps1 -Msix
```

Also builds the Store package from the same release folder:

| Output | Contents |
|---|---|
| `artifacts\msix\layout\` | The release folder plus `packaging\msix\AppxManifest.xml` and the logos, with the version set to `<Version>.0` |
| `artifacts\StatefulBar-<version>-win-x64.msix` | The layout packed by `makeappx.exe`; only when the Windows SDK is installed |

- The `.msix` is not signed, because the Store signs it. It is for the Partner Center upload and cannot be installed by
  hand. CI builds it on every run and attaches it to the run as the `StatefulBar-msix` artifact.
- `Identity` (`Name`, `Publisher`) and `PublisherDisplayName` in the manifest come from Partner Center > Product identity.
  A test guards them; change them only when Partner Center changes.
- The logos in `packaging\msix\Assets` are drawn by `.\tools\make-icon.ps1 -MsixDir packaging\msix\Assets`.

To test the packaged app without the Store or a certificate, turn on Developer Mode in Windows and register the layout:

```
Add-AppxPackage -Register artifacts\msix\layout\AppxManifest.xml
```

Exit every other StatefulBar first (one instance per user), start "StatefulBar" from the Start menu, and remove the test
package afterwards with `Get-AppxPackage AndreHaliimStudio.StatefulBar | Remove-AppxPackage`. It must be gone before the
Store version is installed on the same PC.

What is different when packaged (`PackageIdentity.IsPackaged`, logged as `packaged=True` in the start line of `diag.log`):

- "Start with Windows…" opens Settings > Apps > Startup: the package's startup task replaces the `Run` registry value,
  which a package cannot write.
- "Add CLI to PATH…" is hidden: `sbar` is available in every terminal through the package's execution alias.
- Settings and `diag.log` use `%APPDATA%\StatefulBar` when that folder already exists (it is shared with an installed
  copy), otherwise Windows keeps them in the package's own folder under `%LOCALAPPDATA%\Packages`.
- Flutter, git, and adb started by the widget run outside the package, so they read and write their usual locations.

## CI and releases

GitHub Actions, in `.github/workflows/`:

- **CI** (`ci.yml`) runs on every push and pull request to `main`, except changes that only touch `*.md`, `docs/`, or
  `LICENSE`: format check (`dotnet format --verify-no-changes`), Release build, unit tests, then `tools\publish.ps1 -Msix`.
  The zip and the Store package are attached to the run as the `StatefulBar-win-x64` and `StatefulBar-msix` artifacts for
  7 days.
- **Release** (`release.yml`) runs when a `v*` tag is pushed. It checks that the tag matches `<Version>` in
  `Directory.Build.props`, runs the tests, builds the zip and `Setup.exe` (Inno Setup from Chocolatey), writes
  `SHA256SUMS.txt`, and creates a **draft** GitHub Release with those three files and generated release notes. The Store
  package of the same build is attached to the workflow run (30 days), not to the Release.

To ship a new version:

1. Set `<Version>` in `Directory.Build.props` (e.g. `1.0.1`), commit, and push to `main`. Wait for CI to pass.
2. Tag that commit and push the tag:

   ```
   git tag v1.0.1
   git push origin v1.0.1
   ```

3. Open the draft on the Releases page, edit the notes, and click **Publish release**.

If the release workflow fails after creating the draft, delete the draft (and the tag, if the fix needs a new commit)
before pushing the tag again.

## Tests

`dotnet test` runs the unit tests only (integration tests are skipped automatically).

The end-to-end integration tests run a real `fvm flutter run --machine` against `tests\sample_app` (Flutter 3.22.3 via `.fvmrc`):
start until Running, reload, restart, logs, stop, then check that every child process is gone.

```
$env:STATEFULBAR_IT_DEVICE="chrome"; dotnet test --filter Category=Integration
```

Last result (2026-11-07): 2/2 passed on the `chrome` device (the first build can take several minutes; 10 minute timeout).
On the web, hot reload behaves like hot restart and answers code 0. Android isn't covered yet: the `Pixel_9` AVD on the
development machine failed to install (`INSTALL_FAILED_INSUFFICIENT_STORAGE`) and the `windows` device needs the Visual
Studio C++ toolchain.

## RAM usage

**Release** build, `StatefulBar.exe` idle for 5 minutes after start (no session, LogWindow closed), 96 DPI.

| Metric | Windows 11 (2026-11-07, 1920x1080) | Windows 10 22H2 (2026-11-12, 1600x900) | Meaning |
|---|---|---|---|
| **Private working set** | **35.8 MB** | **30.1 MB** | Physical RAM owned by the process alone; the "Memory" column in Task Manager. **Target metric** |
| Working set | 121.9 MB | 105.4 MB | All physical RAM mapped into the process, including .NET/WPF DLL pages shared with other processes |
| Private bytes | 61.2 MB | 39.7 MB | Private committed memory (including what is in the pagefile) |

The spec §1 target (idle private working set under 100 MB) is met on Windows 10 and 11. The working set is still recorded
for comparison; most of it is shared DLL pages that StatefulBar's code can't reduce.

With the LogWindow open holding 20,000 lines on a Release build: tested on 2026-11-11 (OK), numbers not recorded.
Debug reference: about 117 MB for the widget alone, about 167-178 MB working set with 20,000 lines and the LogWindow open.

## Technical notes

- Windows 10 and 11 use the same taskbar detection (UI Automation, with `TrayNotifyWnd` as a fallback); there is no
  Windows 10-specific path (`MSTaskSwWClass`).
- The daemon protocol fixtures were recorded on `chrome`; Android-specific output (Gradle, `app.log`, hot reload semantics)
  hasn't been recorded yet. Android exception detection (logcat prefix `I/flutter ( 1234): `) is tested with synthetic lines,
  not real recordings.
- A clean stop (flutter exits within 5 seconds of `app.stop`) leaves the daemons that session started (Gradle, Kotlin, adb)
  running until StatefulBar closes, so the next Run doesn't pay for a cold Gradle. Exiting or crashing StatefulBar still
  kills all of them (Job Object).
- The CLI talks to the widget over a named pipe owned by the current user; the widget holds a per-user named mutex, so only
  one instance runs per user session.
