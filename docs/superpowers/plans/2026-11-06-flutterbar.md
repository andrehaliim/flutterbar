# FlutterBar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Membangun widget taskbar Windows (WPF) yang menggantikan run toolbar Android Studio untuk project Flutter (pilih project, branch, config, dan device, lalu Run, Hot reload, Hot restart, dan Stop), lengkap dengan `LogWindow` dan CLI `flutterbar` untuk Claude Code.

**Architecture:** Semua logika ada di `FlutterBar.Core`, sebuah library tanpa UI yang bisa di-unit-test. Library ini membungkus proses `flutter run --machine`, git, dan device, lalu mengeksposnya ke dua klien: `FlutterBar.App`, yaitu widget WPF yang menempel di taskbar, dan `flutterbar.exe`, yaitu CLI yang bicara ke widget lewat named pipe. Docking taskbar adalah risiko terbesar, jadi dibuktikan dulu lewat spike sebelum fitur lain.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), WPF, System.Text.Json, UI Automation (`UIAutomationClient`), dan Win32 P/Invoke yang ditulis tangan. Test memakai xUnit.

**Spec:** `docs/superpowers/specs/2026-11-06-flutterbar-design.md`. Baca spec ini dulu. Nomor bagian (§) di rencana ini merujuk ke spec tersebut.

## Global Constraints

- Target `net10.0-windows`, `<Nullable>enable</Nullable>`, dan `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` untuk semua project.
- **Dependency NuGet hanya:** `xunit`, `xunit.runner.visualstudio`, dan `Microsoft.NET.Test.Sdk` (khusus project test). Package lain harus mendapat persetujuan user dulu.
- **Path data:** `%APPDATA%\FlutterBar\settings.json` dan `%APPDATA%\FlutterBar\diag.log`. Config project ada di `<project>/.flutterbar/launch.json`.
- **Format `launch.json`:** `version: 1`, JSONC (komentar dan trailing comma diperbolehkan). Default `program` adalah `"lib/main.dart"` dan default `mode` adalah `debug`.
- **Perintah run:** `<flutter> run --machine -d <deviceId> -t <program> [--flavor <flavor>] --<mode> <args...>`, dengan working directory di root project.
- **fvm:** kalau ada `.fvmrc` atau `.fvm/fvm_config.json`, wajib memakai `fvm flutter` tanpa fallback.
- **Batas waktu:** buffer log 20.000 baris, stop grace 5 detik, timeout reload CLI 60 detik, emulator di-poll setiap 5 detik dengan timeout 120 detik, rescan project kalau sudah lebih dari 10 menit, dan kedalaman scan 3.
- **Exit code CLI:** 0 OK, 1 gagal, 2 FlutterBar tidak berjalan, 3 tidak ada sesi, 4 timeout, 64 usage.
- **Pipe:** `\\.\pipe\FlutterBar-<SID user>`, dengan ACL hanya untuk user saat ini.
- **Teks UI** dalam Bahasa Indonesia, sesuai copy di spec. Identifier kode dalam bahasa Inggris.
- **Error handling:** tidak boleh ada `catch` yang menelan exception. Setiap catch harus menampilkan pesan ke user dan/atau menulis ke `diag.log`.
- **Git (disetujui user 2026-11-06):** setiap langkah "Checkpoint" berarti **commit lokal** di branch `main` dengan commit message yang tertulis di langkah itu (Conventional Commits, bahasa Inggris). **Jangan** menambahkan baris `Co-Authored-By`, "Generated with Claude", atau atribusi Claude/AI apa pun. Jangan `git push`, jangan membuat branch, jangan rewrite history.
- **Verifikasi per task:** `dotnet format --include <file yang diubah>`, `dotnet build FlutterBar.sln` tanpa warning, dan `dotnet test` hijau.

## Review Focus

1. **Argumen berisi spasi, kutip, `&`, atau `%` yang lewat `flutter.bat`** (misalnya `--dart-define=APP_NAME=GTS Mobile Beta`, atau URL dengan `?a=1&b=2`). Argumen harus sampai ke flutter persis seperti aslinya, tanpa command injection. Test-nya ada di Task 5.
2. **Path project berisi spasi atau karakter non-ASCII** (misalnya `C:\Users\Andre\My Projects\aplikasi-ü`). Scan, git, dan run harus tetap jalan. Test-nya ada di Task 4 dan Task 5.
3. **Proses flutter keluar sebelum `app.started`** (misalnya Gradle gagal), atau hanya mengeluarkan teks non-JSON. Sesi harus berakhir di state Failed dengan output tetap ada di log, dan tidak boleh macet di Starting. Test-nya ada di Task 9.
4. **`flutterbar reload` dipanggil saat sesi masih Starting (belum ada `appId`) atau saat reload lain sedang berjalan.** Hasilnya harus langsung exit 3 (tidak ada sesi) atau exit 1 ("Sedang reload"), dan tidak boleh hang. Test-nya ada di Task 9 dan Task 11.
5. **Branch atau `launch.json` diubah dari luar** (oleh Claude Code atau terminal) saat widget terbuka. Widget harus menampilkan branch baru dan config terbaru saat flyout dibuka. Test-nya ada di Task 6 (watcher HEAD) dan Task 2 (`Load` selalu membaca ulang dari disk).

---

## Struktur file

```
FlutterBar.sln
Directory.Build.props                      setting bersama (TFM, nullable, warnings as errors)
.gitignore, README.md
src/FlutterBar.Core/
  Config/LaunchConfig.cs                   model LaunchConfig, RunMode, ConfigError
  Config/LaunchConfigStore.cs              baca/validasi/tulis launch.json
  Config/FlutterCommandBuilder.cs          argumen `flutter run`
  Config/ShellSplitter.cs                  pemecah additionalArgs IntelliJ
  Config/IdeConfigImporter.cs              import .idea / .run / .vscode
  Settings/AppSettings.cs                  model settings.json
  Settings/AppSettingsStore.cs             load/save atomik + backup kalau rusak
  Projects/PubspecInspector.cs             cek apakah folder adalah app Flutter
  Projects/ProjectScanner.cs               scan folder root
  Projects/ProjectCatalog.cs               daftar akhir (manual, hidden, urutan terakhir dipakai, nama)
  Processes/ProcessSpec.cs                 ProcessSpec, ProcessResult
  Processes/CmdEscaper.cs                  escaping cmd.exe untuk .bat/.cmd
  Processes/PathResolver.cs                cari exe di PATH + PATHEXT
  Processes/FlutterToolLocator.cs          fvm flutter vs flutter
  Processes/ProcessRunner.cs               proses singkat (git, devices)
  Processes/ProcessLauncher.cs             proses panjang + stdin/stdout streaming
  Processes/JobObject.cs                   Win32 Job Object kill-on-close
  Git/GitService.cs                        branch, switch, cek pubspec berubah
  Git/HeadWatcher.cs                       FileSystemWatcher untuk HEAD
  Devices/DeviceModels.cs, Devices/DeviceParsers.cs, Devices/DeviceService.cs
  Daemon/DaemonMessages.cs                 tipe event/response
  Daemon/DaemonProtocol.cs                 parse baris + encode request
  Logging/LogBuffer.cs                     ring buffer + query
  Logging/LogClassifier.cs                 deteksi header/footer exception
  Sessions/RunSession.cs                   state machine sesi
  Sessions/SessionModels.cs                SessionState, SessionSnapshot, RunRequest, ReloadOutcome
  Ipc/IpcContracts.cs                      IpcRequest, IpcResponse, ExitCodes, PipeNames
  Ipc/CommandHandler.cs                    eksekusi perintah CLI terhadap RunSession/LogBuffer
  Ipc/IpcServer.cs, Ipc/IpcClient.cs
  Layout/WidgetLayoutPolicy.cs             pilih ukuran widget sesuai ruang
  Layout/TaskbarGeometry.cs                hitung area kosong taskbar (murni)
  Diagnostics/DiagLog.cs                   tulis diag.log
src/FlutterBar.Cli/Program.cs, CliApp.cs   flutterbar.exe
src/FlutterBar.App/                        WPF (detail di Task 12–16)
spikes/DockingSpike/                       spike sekali pakai (Task 1)
tests/FlutterBar.Core.Tests/               xUnit, satu file test per file Core
tests/fixtures/                            launch.json, workspace.xml, rekaman daemon
tests/sample_app/                          app Flutter minimal untuk fixture + integration
tools/record-daemon.ps1                    perekam output `flutter run --machine`
```

---

### Task 0: Prasyarat & scaffold solution

**Files:**
- Create: `FlutterBar.sln`, `Directory.Build.props`, `.gitignore`, `README.md`
- Create: `src/FlutterBar.Core/FlutterBar.Core.csproj`, `src/FlutterBar.App/FlutterBar.App.csproj`, `src/FlutterBar.App/app.manifest`, `src/FlutterBar.Cli/FlutterBar.Cli.csproj`
- Create: `tests/FlutterBar.Core.Tests/FlutterBar.Core.Tests.csproj`, `tests/FlutterBar.Core.Tests/SmokeTests.cs`

**Interfaces:**
- Produces: namespace root `FlutterBar.Core`, `FlutterBar.App`, dan `FlutterBar.Cli`. Assembly CLI bernama `flutterbar`. `FlutterBar.Core.Tests` mereferensikan Core dan Cli.

- [ ] **Step 1: Minta izin user untuk memasang .NET 10 SDK.** Di mesin ini hanya ada runtime 6 dan 9 (spec §12). Kalau user setuju, jalankan `winget install Microsoft.DotNet.SDK.10`. Kalau tidak, minta user memasangnya sendiri.
- [ ] **Step 2: Verifikasi SDK.** Jalankan `dotnet --list-sdks`. Harus muncul baris `10.0.x`.
- [ ] **Step 3: Tanya user apakah folder ini boleh di-`git init`.** Jangan jalankan sebelum ada izin.
- [ ] **Step 4: Buat solution dan project.** Gunakan `dotnet new sln`, `dotnet new classlib`, `dotnet new wpf`, `dotnet new console`, dan `dotnet new xunit`.
  - `Directory.Build.props` mengatur nilai di Global Constraints.
  - App: `OutputType=WinExe`, `UseWPF=true`, dan `ApplicationManifest=app.manifest` dengan `<dpiAwareness>PerMonitorV2</dpiAwareness>`.
  - Cli: `OutputType=Exe` dan `AssemblyName=flutterbar`.
  - `.gitignore`: `bin/`, `obj/`, `*.user`, `.vs/`, `tests/sample_app/build/`, dan `tests/sample_app/.dart_tool/`.
- [ ] **Step 5: Tulis smoke test** `SmokeTests.CoreAssemblyLoads`, yang memastikan `typeof(FlutterBar.Core.AssemblyMarker).Assembly` tidak null. Tambahkan juga `public static class AssemblyMarker {}` di Core.
- [ ] **Step 6: Verifikasi.** `dotnet build FlutterBar.sln` harus menampilkan `0 Warning(s) 0 Error(s)`, dan `dotnet test` harus `Passed: 1`.
- [ ] **Step 7: Checkpoint.** Commit message: `chore: scaffold FlutterBar solution`.

---

### Task 1: Spike docking taskbar (sekali pakai, menjadi gerbang keputusan)

Tujuannya menjawab satu pertanyaan: **apakah jendela WPF bisa menempel dengan stabil di area kosong taskbar Windows 11 milik user?** Kode di task ini bersifat throwaway dan tidak di-unit-test.

**Files:**
- Create: `spikes/DockingSpike/DockingSpike.csproj` (WPF, tidak dimasukkan ke solution utama), `spikes/DockingSpike/MainWindow.xaml(.cs)`, `spikes/DockingSpike/Native.cs`
- Create: `docs/superpowers/spikes/2026-11-06-docking-findings.md`

- [ ] **Step 1: Bangun jendela 300×40 px** dengan `WindowStyle=None`, `AllowsTransparency=False`, `ShowInTaskbar=False`, `Topmost=True`, dan extended style `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`. Pakai background solid supaya tidak kedip.
- [ ] **Step 2: Posisikan jendela di kiri tray.**
  - `FindWindow("Shell_TrayWnd")` → `GetWindowRect`.
  - Windows 11: pakai `AutomationElement.FromHandle(taskbar)` lalu cari elemen tray/notification area (misalnya nama kelas `SystemTrayIcon`, atau elemen paling kiri yang berisi jam) dan elemen tombol aplikasi (`Taskbar.TaskListButtonAutomationPeer`), lalu catat `BoundingRectangle`.
  - Catat **nama kelas atau AutomationId yang benar-benar ditemukan** ke file findings.
- [ ] **Step 3: Pertahankan topmost.** Pasang `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`. Saat foreground berpindah ke `Shell_TrayWnd`, panggil `SetWindowPos(HWND_TOPMOST, …, SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE)`.
- [ ] **Step 4: Uji manual dan catat hasilnya di findings.** Untuk setiap baris, tulis "OK", "Bermasalah: …", atau "Tidak bisa":
  - Taskbar rata tengah dan rata kiri.
  - Klik Start, klik tombol aplikasi, dan buka Action Center: apakah widget tetap terlihat?
  - Auto-hide taskbar menyala.
  - Video YouTube fullscreen, lalu tekan F11 di browser.
  - DPI 100%, 125%, dan 150%.
  - Menambah dan menutup aplikasi sampai taskbar penuh: apakah area kosong terhitung ulang (poll 1 detik cukup untuk spike)?
- [ ] **Step 5: Catat working set spike** dari Task Manager.
- [ ] **Step 6: Gerbang keputusan.** Laporkan findings ke user.
  - Kalau penempatan otomatis dan topmost **gagal**, berhenti dan diskusikan alternatif (misalnya posisi manual saja, atau floating bar di atas taskbar) sebelum lanjut ke Task 13.
  - Kalau **berhasil**, findings menjadi sumber nilai nama kelas/AutomationId untuk Task 13.
  - Task 2–12 tidak bergantung pada hasil spike, jadi boleh dikerjakan sambil menunggu keputusan.
- [ ] **Step 7: Checkpoint.** Commit message: `chore: add taskbar docking spike and findings`.

---

### Task 2: `LaunchConfig`, `LaunchConfigStore`, dan `FlutterCommandBuilder`

**Files:**
- Create: `src/FlutterBar.Core/Config/LaunchConfig.cs`, `Config/LaunchConfigStore.cs`, `Config/FlutterCommandBuilder.cs`
- Test: `tests/FlutterBar.Core.Tests/Config/LaunchConfigStoreTests.cs`, `Config/FlutterCommandBuilderTests.cs`

**Interfaces:**
- Produces:
  - `public enum RunMode { Debug, Profile, Release }`
  - `public sealed record LaunchConfig(string Name, string Program, string? Flavor, RunMode Mode, IReadOnlyList<string> Args)`
  - `public sealed record ConfigError(string Message, string? Field, long? Line)`
  - `public sealed record LaunchConfigLoadResult(bool FileExists, IReadOnlyList<LaunchConfig> Configs, IReadOnlyList<ConfigError> Errors)`
  - `public static class LaunchConfigStore`, berisi:
    - `string PathFor(string projectRoot)` → `<root>\.flutterbar\launch.json`
    - `LaunchConfigLoadResult Load(string projectRoot)`, yang selalu membaca ulang dari disk tanpa cache
    - `void Save(string projectRoot, IReadOnlyList<LaunchConfig> configs)`
    - `LaunchConfig CreateDefault()` → `("default", "lib/main.dart", null, Debug, [])`
  - `public static class FlutterCommandBuilder { IReadOnlyList<string> BuildRunArgs(LaunchConfig config, string deviceId); }`

- [ ] **Step 1: Tulis test yang gagal.** Pakai folder sementara per test (`Directory.CreateTempSubdirectory()`).
  ```csharp
  [Fact] void Load_MissingFile_ReturnsFileExistsFalse()        // FileExists == false, Configs kosong, Errors kosong
  [Fact] void Load_Jsonc_WithCommentsAndTrailingCommas()       // fixture tests/fixtures/launch/gts-beta.jsonc -> 1 config:
      // Name "beta", Program "lib/main.dart", Flavor "beta", Mode Debug,
      // Args[2] == "--dart-define=APP_NAME=GTS Mobile Beta"
  [Fact] void Load_DefaultsProgramAndMode()                    // {"version":1,"configurations":[{"name":"x"}]} -> Program "lib/main.dart", Mode Debug
  [Fact] void Load_InvalidJson_ReportsLine()                   // JSON terpotong di baris 4 -> Errors[0].Line == 4
  [Theory] void Load_ValidationErrors(string json, string field)
      // version 2 -> "version"; configurations kosong -> "configurations";
      // nama duplikat -> "configurations[1].name"; mode "fast" -> "configurations[0].mode";
      // program tidak ada di disk -> "configurations[0].program"
  [Fact] void Load_RereadsAfterExternalEdit()                  // Load, tulis ulang file dari luar, Load lagi -> nilai baru (Review Focus #5)
  [Fact] void Save_ThenLoad_RoundTrips()                       // termasuk Args berisi spasi dan kutip
  [Fact] void Save_IsAtomic_NoTmpFileLeft()                    // setelah Save tidak ada *.tmp di folder .flutterbar
  ```
  ```csharp
  [Fact] void BuildRunArgs_Full()   // config beta + device "emulator-5554" ->
      // ["run","--machine","-d","emulator-5554","-t","lib/main.dart","--flavor","beta","--debug", ...Args]
  [Fact] void BuildRunArgs_NoFlavor_ReleaseMode() // tanpa "--flavor", mode "--release"
  ```
- [ ] **Step 2: Jalankan dan pastikan gagal.** Perintah: `dotnet test --filter "FullyQualifiedName~Config"`. Hasil yang diharapkan: FAIL (compile error karena tipe belum ada).
- [ ] **Step 3: Implementasi.**
  - Parse dengan `JsonDocument` memakai `JsonDocumentOptions { CommentHandling = Skip, AllowTrailingCommas = true }`. `JsonException.LineNumber` berbasis 0, jadi tambahkan 1.
  - Nama field di `ConfigError.Field` memakai format path di atas. `Mode` di-parse case-insensitive dari `"debug" | "profile" | "release"`.
  - `Save` menulis JSON berindentasi (`version: 1`) ke `launch.json.tmp`, lalu `File.Move(overwrite: true)`. Komentar di file lama akan hilang. Ini disengaja, karena `Save` hanya dipakai oleh import dan pembuatan config default.
- [ ] **Step 4: Jalankan test.** Hasil yang diharapkan: PASS.
- [ ] **Step 5: Checkpoint.** Commit message: `feat(core): add launch.json store and run command builder`.

---

### Task 3: `ShellSplitter` & `IdeConfigImporter`

**Files:**
- Create: `src/FlutterBar.Core/Config/ShellSplitter.cs`, `Config/IdeConfigImporter.cs`
- Create fixtures: `tests/fixtures/idea/workspace.xml` (salin bagian `RunManager` gts-mobile, yaitu config `beta` dan `live` beserta `&quot;`), `tests/fixtures/idea/run/dev.run.xml`, `tests/fixtures/vscode/launch.json` (salin 3 config gts-mobile, termasuk komentar `//`)
- Test: `tests/FlutterBar.Core.Tests/Config/ShellSplitterTests.cs`, `Config/IdeConfigImporterTests.cs`

**Interfaces:**
- Consumes: `LaunchConfig` dan `RunMode` dari Task 2.
- Produces:
  - `public static class ShellSplitter { IReadOnlyList<string> Split(string commandLine); }`
  - `public sealed record ImportCandidate(LaunchConfig Config, string Source, bool AlreadyExists)`. `Source` bernilai `"idea"` atau `"vscode"`.
  - `public static class IdeConfigImporter`, berisi:
    - `IReadOnlyList<ImportCandidate> Discover(string projectRoot, IReadOnlyList<LaunchConfig> existing)`
    - `IReadOnlyList<LaunchConfig> Merge(IReadOnlyList<LaunchConfig> existing, IReadOnlyList<ImportCandidate> selected)`

- [ ] **Step 1: Tulis test yang gagal.**
  ```csharp
  [Theory]
  [InlineData("--a=1 --b=2", new[]{"--a=1","--b=2"})]
  [InlineData("--dart-define=APP_NAME=\"GTS Mobile Beta\" --x", new[]{"--dart-define=APP_NAME=GTS Mobile Beta","--x"})]
  [InlineData("  ", new string[0])]
  [InlineData("'single quoted' b", new[]{"single quoted","b"})]
  void Split_Cases(string input, string[] expected)
  ```
  ```csharp
  [Fact] void Discover_Idea_MapsBetaConfig()   // Name "beta", Program "lib/main.dart" ($PROJECT_DIR$/ dibuang),
      // Flavor "beta", Args berisi "--dart-define=APP_NAME=GTS Mobile Beta", Source "idea"
  [Fact] void Discover_RunFolder_IsIncluded()  // .run/dev.run.xml ikut ditemukan
  [Fact] void Discover_Vscode_LiftsFlavor()    // "GTS Mobile Dev": Flavor "dev", Args tidak lagi berisi "--flavor"/"dev"
  [Fact] void Discover_Vscode_FlavorEqualsForm() // "--flavor=live" juga diangkat
  [Fact] void Discover_MarksAlreadyExisting()  // existing berisi "beta" -> candidate beta AlreadyExists == true
  [Fact] void Discover_IgnoresNonDartVscodeConfigs()
  [Fact] void Discover_NoIdeFiles_ReturnsEmpty()
  [Fact] void Merge_SuffixesCollidingNames()   // "beta" dari idea dan vscode dipilih semua -> "beta" + "beta (vscode)"
  [Fact] void Merge_SkipsNamesAlreadyInExisting()
  ```
- [ ] **Step 2: Jalankan dan pastikan gagal.** Perintah: `dotnet test --filter "FullyQualifiedName~ShellSplitter|FullyQualifiedName~IdeConfigImporter"`.
- [ ] **Step 3: Implementasi.**
  - `ShellSplitter` adalah state machine sederhana (spasi memisahkan argumen, kutip tunggal atau ganda mengelompokkan, `\"` di dalam kutip ganda menjadi kutip literal). Entity XML seperti `&quot;` sudah di-decode oleh `XDocument`.
  - IDEA: ambil `configuration[@type='FlutterRunConfigurationType']` beserta `option[@name]`.
  - VS Code: dibaca dengan opsi JSONC yang sama seperti Task 2. `flutterMode` default-nya `debug`, dan `program` default-nya `lib/main.dart`.
- [ ] **Step 4: Jalankan test.** Hasil yang diharapkan: PASS.
- [ ] **Step 5: Checkpoint.** Commit message: `feat(core): import run configs from Android Studio and VS Code`.

---

### Task 4: Settings, scan project, dan katalog

**Files:**
- Create: `src/FlutterBar.Core/Settings/AppSettings.cs`, `Settings/AppSettingsStore.cs`, `Projects/PubspecInspector.cs`, `Projects/ProjectScanner.cs`, `Projects/ProjectCatalog.cs`
- Test: `tests/FlutterBar.Core.Tests/Settings/AppSettingsStoreTests.cs`, `Projects/PubspecInspectorTests.cs`, `Projects/ProjectScannerTests.cs`, `Projects/ProjectCatalogTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class AppSettings`, dengan property yang bisa diubah dan mengikuti spec §4.3:
    - `int Version = 1`
    - `List<string> ScanRoots`, `List<string> ManualProjects`, `List<string> HiddenProjects`
    - `Dictionary<string, ProjectState> Projects`, dengan key path yang dibandingkan case-insensitive
    - `string? CurrentProject`
    - `WidgetSettings Widget`
    - `bool StartWithWindows`
  - `public sealed class ProjectState { DateTimeOffset? LastUsed; string? Config; string? DeviceId; }`
  - `public sealed class WidgetSettings { string Size = "full"; string Position = "auto"; double? LockedX; double? WidthOverride; }`
  - `public sealed class AppSettingsStore(string path)`, berisi:
    - `static string DefaultPath`
    - `AppSettings Load(out string? warning)`
    - `void Save(AppSettings settings)`
  - `public static class PubspecInspector { bool IsFlutterApp(string pubspecYamlText); }`
  - `public static class ProjectScanner { IReadOnlyList<string> Scan(IEnumerable<string> roots, int maxDepth = 3); }`
  - `public sealed record ProjectInfo(string Path, string DisplayName, bool Available, DateTimeOffset? LastUsed)`
  - `public static class ProjectCatalog { IReadOnlyList<ProjectInfo> Build(IReadOnlyList<string> scanned, AppSettings settings, Func<string,bool> directoryExists); }`

- [ ] **Step 1: Tulis test yang gagal.**
  ```csharp
  // AppSettingsStore
  [Fact] void Load_Missing_ReturnsDefaults_NoWarning()
  [Fact] void Load_Corrupt_BacksUpAndReturnsDefaults() // file berisi "{oops" -> ada settings.json.bak-*, warning != null
  [Fact] void Save_ThenLoad_RoundTrips_PathKeysCaseInsensitive() // Projects["c:\\work\\APP"] ditemukan lewat "C:\\Work\\app"
  // PubspecInspector
  [Fact] void FlutterApp_True()     // "dependencies:\n  flutter:\n    sdk: flutter\n"
  [Fact] void PureDartPackage_False() // "dependencies:\n  path: ^1.9.0\n"
  [Fact] void DevDependencyOnlyFlutterTest_False() // flutter_test di dev_dependencies saja
  // ProjectScanner (struktur dibuat di folder sementara)
  [Fact] void Scan_FindsUpToDepth3_NotDepth4()
  [Fact] void Scan_SkipsExcludedFolders()   // .git, .dart_tool, build, node_modules, android, ios, windows, linux, macos, web, .hidden
  [Fact] void Scan_PathWithSpacesAndUnicode() // "My Projects/aplikasi-ü" ditemukan (Review Focus #2)
  [Fact] void Scan_MissingRoot_IsIgnored()
  // ProjectCatalog
  [Fact] void Build_MergesManual_RemovesHidden()
  [Fact] void Build_OrdersByLastUsedDescThenName()
  [Fact] void Build_DuplicateFolderNames_AppendsParent() // "gts-mobile (GTSmobile)"
  [Fact] void Build_MissingDirectory_MarkedUnavailable()
  ```
- [ ] **Step 2: Jalankan dan pastikan gagal.**
- [ ] **Step 3: Implementasi.**
  - `IsFlutterApp`: blok `dependencies:` di level atas harus berisi key `flutter:` yang diikuti `sdk: flutter`. Cek ini cukup dengan parse baris berbasis indentasi, tanpa package YAML.
  - `Save` di `AppSettingsStore` bersifat atomik (`.tmp` lalu `File.Move`), sama seperti Task 2.
  - Nama backup: `settings.json.bak-yyyyMMddHHmmss`.
- [ ] **Step 4: Jalankan test.** Hasil yang diharapkan: PASS.
- [ ] **Step 5: Checkpoint.** Commit message: `feat(core): add settings store and project catalog`.

---

### Task 5: Proses: `CmdEscaper`, `PathResolver`, `FlutterToolLocator`, `ProcessRunner`, `ProcessLauncher`, `JobObject`

**Files:**
- Create: `src/FlutterBar.Core/Processes/*.cs` (sesuai daftar struktur file)
- Test: `tests/FlutterBar.Core.Tests/Processes/CmdEscaperTests.cs`, `FlutterToolLocatorTests.cs`, `ProcessLauncherTests.cs`
- Create: `tests/fixtures/echoargs/echoargs.cmd`, yang menjalankan `"%~dp0echoargs.exe" %*`. Test helper `EchoArgs` adalah console exe kecil `tests/EchoArgs/`, ditambahkan ke solution, yang mencetak setiap `args[i]` per baris dalam JSON.

**Interfaces:**
- Produces:
  - `public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Args, string WorkingDirectory)`
  - `public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)`
  - `public static class CmdEscaper { string BuildCmdArguments(string batchPath, IReadOnlyList<string> args); }`. Fungsi ini mengembalikan string argumen untuk `cmd.exe`, dan melempar `ArgumentException` kalau ada `\r` atau `\n`.
  - `public interface IPathResolver { string? Find(string command); void ClearCache(); }` dan `public sealed class PathResolver : IPathResolver`. Hasil `Find` di-cache per instance (spec §5.2), dan menu "Rescan" di Task 16 memanggil `ClearCache()`.
  - `public sealed record ToolResolution(ProcessSpec? Spec, string? Error)`
  - `public sealed class FlutterToolLocator(IPathResolver paths, Func<string,bool> fileExists)` dengan method `ToolResolution Resolve(string projectRoot, IReadOnlyList<string> flutterArgs)`
  - `public interface IProcessRunner { Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct); }`
  - `public interface IProcessHandle : IAsyncDisposable`, berisi:
    - `int Id`
    - `Task<int> Exited`
    - `event Action<string>? StdoutLine`, `event Action<string>? StderrLine`
    - `Task WriteLineAsync(string line)`
    - `void KillTree()`
  - `public interface IProcessLauncher { IProcessHandle Start(ProcessSpec spec); }`

- [ ] **Step 1: Tulis test yang gagal.**
  ```csharp
  // CmdEscaper + EchoArgs (end-to-end lewat cmd.exe; Review Focus #1)
  [Theory]
  [InlineData("--dart-define=APP_NAME=GTS Mobile Beta")]
  [InlineData("--dart-define=URL=https://x.id/?a=1&b=2")]
  [InlineData("100%")] [InlineData("%PATH%")] [InlineData("a\"b")] [InlineData("^caret|pipe<lt>gt")]
  [InlineData("")] [InlineData("trailing\\")]
  void BatchArgs_ArriveVerbatim(string arg)   // jalankan echoargs.cmd [arg] lewat ProcessRunner -> output == [arg]
  [Fact] void BatchArgs_NewlineRejected()
  [Fact] void BatchPath_WithSpaces_Works()    // echoargs.cmd disalin ke folder "dir with space"
  // FlutterToolLocator (IPathResolver palsu)
  [Fact] void Fvmrc_UsesFvmExe()              // .fvmrc ada, fvm -> "C:\\x\\fvm.exe" => Spec.FileName fvm.exe, Args diawali "flutter"
  [Fact] void FvmConfigJson_AlsoCountsAsFvm()
  [Fact] void Fvmrc_FvmMissing_ErrorNoFallback() // Spec == null, Error berisi "fvm"
  [Fact] void NoFvm_FlutterBat_WrappedInCmd()    // flutter -> "C:\\f\\flutter.bat" => FileName cmd.exe
  [Fact] void NoFlutter_Error()
  // ProcessLauncher (pakai EchoArgs mode "--interactive": membaca stdin, membalas "echo:<line>")
  [Fact] async Task Launcher_StreamsStdout_AndWritesStdin()
  [Fact] async Task KillTree_KillsGrandchildren() // EchoArgs "--spawn-child": setelah KillTree, PID anak juga hilang
  [Fact] async Task DisposeLauncher_KillsJob()     // dispose handle -> proses mati (kill-on-close)
  ```
- [ ] **Step 2: Jalankan dan pastikan gagal.**
- [ ] **Step 3: Implementasi `CmdEscaper`.** Gunakan aturan escaping `cmd.exe` yang sama dengan perbaikan Rust std untuk CVE-2024-24576:
  - Command line akhirnya berbentuk `/d /s /c ""<batchPath>" <arg1> <arg2>…"`.
  - Argumen yang kosong atau berisi spasi, tab, atau salah satu dari ``"&|<>^%!()`;,=`` dibungkus `"…"`, dengan `"` di dalamnya digandakan menjadi `""`, dan setiap `%` diganti `%%cd:~,%`.
  - Backslash di akhir argumen yang dikutip digandakan.
  - `ProcessStartInfo.Arguments` diisi string ini, dan `FileName` diisi `cmd.exe`.
  - Kalau test `%` atau `^` gagal, perbaiki aturannya sampai semua `InlineData` lolos. Jangan menghapus test.
- [ ] **Step 4: Implementasi sisanya.**
  - `PathResolver`: telusuri `PATH`, lalu coba setiap ekstensi `PATHEXT`.
  - `ProcessLauncher`: pakai `ProcessStartInfo` dengan redirect stdin, stdout, dan stderr dalam UTF-8, lalu `CreateNoWindow`. Proses dimasukkan ke Job Object baru dengan `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` segera setelah start. `KillTree` menutup job. Pembacaan baris memakai `BeginOutputReadLine`.
  - `ProcessRunner` dibangun di atas launcher dengan timeout. Kalau timeout, `KillTree` dipanggil dan ExitCode diisi -1.
- [ ] **Step 5: Jalankan test.** Hasil yang diharapkan: PASS.
- [ ] **Step 6: Checkpoint.** Commit message: `feat(core): add safe process launching with job objects`.

---

### Task 6: `GitService` & `HeadWatcher`

**Files:**
- Create: `src/FlutterBar.Core/Git/GitService.cs`, `Git/HeadWatcher.cs`
- Test: `tests/FlutterBar.Core.Tests/Git/GitServiceTests.cs`. Test ini memakai git asli di repo sementara yang dibuat dengan `git init`, beberapa commit, dan beberapa branch. Test di-skip dengan pesan kalau `git` tidak ditemukan.

**Interfaces:**
- Consumes: `IProcessRunner`, `ProcessSpec`, dan `IPathResolver` dari Task 5.
- Produces:
  - `public sealed record GitResult(bool Success, string Message)`
  - `public enum BranchChangeAdvice { HotRestart, RerunRequired }`
  - `public sealed class GitService(IProcessRunner runner, string gitExe)`, berisi:
    - `Task<bool> IsRepoAsync(string root)`
    - `Task<IReadOnlyList<string>> GetLocalBranchesAsync(string root)`
    - `Task<string> GetCurrentAsync(string root)`, yang mengembalikan nama branch, atau SHA pendek kalau HEAD detached
    - `Task<string> GetHeadCommitAsync(string root)`
    - `Task<GitResult> SwitchAsync(string root, string branch)`
    - `Task<BranchChangeAdvice> AdviseAfterSwitchAsync(string root, string oldCommit, string newCommit)`
    - `Task<string> GetHeadFilePathAsync(string root)`
  - `public sealed class HeadWatcher(string headFilePath) : IDisposable`, dengan `event Action? Changed`. Event ini di-debounce 300 ms.

- [ ] **Step 1: Tulis test yang gagal.**
  ```csharp
  [Fact] async Task NotARepo_IsRepoFalse()
  [Fact] async Task ListsLocalBranches_Sorted()           // main, feature/x
  [Fact] async Task Current_Detached_ReturnsShortSha()    // panjang 7..12, hex
  [Fact] async Task Switch_Dirty_Conflict_ReturnsGitStderr() // Success false, Message berisi "would be overwritten"
  [Fact] async Task Switch_Ok()
  [Fact] async Task Advise_PubspecChanged_RerunRequired()
  [Fact] async Task Advise_OnlyDartChanged_HotRestart()
  [Fact] async Task HeadWatcher_FiresOnExternalSwitch()   // `git switch` dari luar -> Changed dalam < 2 detik (Review Focus #5)
  [Fact] async Task RepoPathWithSpaces_Works()            // Review Focus #2
  ```
- [ ] **Step 2: Jalankan dan pastikan gagal.**
- [ ] **Step 3: Implementasi** sesuai spec §5.5:
  - Daftar branch: `git branch --format=%(refname:short)`.
  - Branch aktif: `git rev-parse --abbrev-ref HEAD`. Kalau hasilnya `"HEAD"`, ambil `git rev-parse --short HEAD`.
  - Cek pubspec: `git diff --name-only <old> <new> -- pubspec.yaml pubspec.lock`.
  - Path file HEAD: `git rev-parse --git-path HEAD`, di-resolve relatif ke root.
- [ ] **Step 4: Jalankan test.** Hasil yang diharapkan: PASS.
- [ ] **Step 5: Checkpoint.** Commit message: `feat(core): add git branch service and HEAD watcher`.

---

### Task 7: Rekam fixture daemon & `DaemonProtocol`

**Files:**
- Create: `tests/sample_app/` lewat `fvm flutter create`, lalu `fvm use 3.22.3`
- Create: `tools/record-daemon.ps1`
- Create fixtures: `tests/fixtures/daemon/3.22.3/{start-ok,reload-ok,restart-ok,reload-compile-error,runtime-exception,stop,build-fail}.jsonl` dan `web-reload-unsupported.jsonl`
- Create: `src/FlutterBar.Core/Daemon/DaemonMessages.cs`, `Daemon/DaemonProtocol.cs`
- Test: `tests/FlutterBar.Core.Tests/Daemon/DaemonProtocolTests.cs`

**Interfaces:**
- Produces:
  - `public abstract record DaemonMessage`, dengan turunan:
    - `AppStart(string AppId, string DeviceId, bool SupportsRestart, string LaunchMode)`
    - `AppStarted(string AppId)`
    - `AppLog(string AppId, string Log, bool Error)`
    - `AppProgress(string? Message, bool Finished)`
    - `AppStop(string AppId, string? Error)`
    - `DaemonLog(string Level, string Message)`
    - `Response(int Id, int? Code, string? Message, string? Error)`
    - `OtherEvent(string Event)`
    - `NonJson(string Text)`
  - `public static class DaemonProtocol`, berisi:
    - `IReadOnlyList<DaemonMessage> ParseLine(string line)`. Satu baris `[...]` bisa berisi lebih dari satu objek.
    - `string EncodeRestart(int id, string appId, bool fullRestart)`
    - `string EncodeStop(int id, string appId)`

- [ ] **Step 1: Siapkan sample app.**
  - Jalankan `fvm flutter create --platforms=android,web,windows --project-name sample_app tests/sample_app`, lalu jalankan `fvm use 3.22.3 --force` di folder itu.
  - Tanyakan ke user device mana yang tersedia untuk perekaman. Pilihan utama emulator/HP Android. `chrome` juga dipakai, khusus untuk fixture `web-reload-unsupported`.
- [ ] **Step 2: Tulis `tools/record-daemon.ps1`** dengan parameter `-Device <id> -Scenario <nama> -Out <file>`. Script ini:
  - Menjalankan `fvm flutter run --machine -d <id>` di `tests/sample_app`.
  - Menulis setiap baris stdout ke `-Out`.
  - Menunggu event `app.started`, lalu menjalankan skenarionya:
    - `reload-ok`: kirim `app.restart fullRestart:false`.
    - `restart-ok`: kirim `fullRestart:true`.
    - `reload-compile-error`: tambahkan `int x = ;` ke `lib/main.dart`, kirim reload, lalu kembalikan file seperti semula.
    - `runtime-exception`: ganti body `_incrementCounter` dengan `throw StateError('boom')` dan reload. Ketukan pada tombol dilakukan manual oleh user.
    - `stop`: kirim `app.stop`.
    - `build-fail`: jalankan dengan `--flavor doesnotexist`.
  - Membatasi setiap skenario 5 menit, dan selalu mengakhiri proses lewat `app.stop` atau kill.
- [ ] **Step 3: Rekam semua skenario.** Periksa setiap file dan pastikan berisi event yang diharapkan. Catat di `tests/fixtures/daemon/README.md` nama field yang **benar-benar** muncul, misalnya isi `result` pada response restart, dan apakah `app.start` memuat `supportsRestart` dan `launchMode`. Kalau berbeda dari tipe di Interfaces, sesuaikan tipe di sini sebelum Step 4, lalu catat perbedaannya.
- [ ] **Step 4: Tulis test yang gagal.**
  ```csharp
  [Fact] void StartOk_YieldsAppStartThenAppStarted_SameAppId()
  [Fact] void ReloadOk_ResponseCodeZero()
  [Fact] void ReloadCompileError_ResponseCodeNonZero_MessageMentionsError()
  [Fact] void RuntimeException_HasExceptionHeaderInAppLogOrDaemonLog()
  [Fact] void BuildFail_ContainsNonJsonOrErrorLines_AndNoAppStarted()
  [Fact] void NonJsonLine_ReturnsNonJson()         // "Launching lib/main.dart on ..." -> NonJson
  [Fact] void MalformedJsonInBrackets_ReturnsNonJson() // "[{\"event\":" -> NonJson, tanpa exception
  [Fact] void EncodeRestart_Format()               // == "[{\"id\":7,\"method\":\"app.restart\",\"params\":{\"appId\":\"a1\",\"fullRestart\":false,\"pause\":false,\"reason\":\"manual\"}}]"
  [Fact] void EncodeStop_Format()
  ```
- [ ] **Step 5: Jalankan dan pastikan gagal.** Lalu implementasi: baris yang diawali `[` dan diakhiri `]` di-parse dengan `JsonDocument`. Kalau parsing gagal, hasilnya `NonJson`.
- [ ] **Step 6: Jalankan test.** Hasil yang diharapkan: PASS.
- [ ] **Step 7: Checkpoint.** Commit message: `feat(core): parse flutter run --machine protocol with recorded fixtures`.

---

### Task 8: `LogBuffer` & `LogClassifier`

**Files:**
- Create: `src/FlutterBar.Core/Logging/LogBuffer.cs`, `Logging/LogClassifier.cs`
- Test: `tests/FlutterBar.Core.Tests/Logging/LogBufferTests.cs`, `Logging/LogClassifierTests.cs`

**Interfaces:**
- Produces:
  - `public enum LogKind { Tool, App, Error, Reload, Separator }`
  - `public sealed record LogLine(long Seq, DateTimeOffset Timestamp, LogKind Kind, string Text)`
  - `public sealed class LogBuffer(int capacity = 20_000, TimeProvider? time = null)`, berisi:
    - `LogLine Append(LogKind kind, string text)`
    - `void MarkReload(string label)`, yang menambahkan baris Separator `── {label} HH:mm:ss ──` dan menyimpan `LastReloadSeq`
    - `void MarkSessionStart(string label)`
    - `IReadOnlyList<LogLine> Since(long seqExclusive)`
    - `IReadOnlyList<LogLine> SinceLastReload()`
    - `IReadOnlyList<LogLine> Tail(int n)`
    - `IReadOnlyList<LogLine> Snapshot()`
    - `event Action<LogLine>? Appended`
  - `public static class LogClassifier`, berisi:
    - `bool IsExceptionHeader(string text)`, yang bernilai benar untuk teks yang diawali `════════ Exception caught by`
    - `bool IsExceptionFooter(string text)`, yang bernilai benar untuk baris yang seluruhnya `═`, minimal 20 karakter

- [ ] **Step 1: Tulis test yang gagal.**
  ```csharp
  [Fact] void Append_AssignsIncreasingSeq()
  [Fact] void OverCapacity_DropsOldest_KeepsSeq()   // kapasitas 3, append 5 -> Snapshot berisi seq 3,4,5
  [Fact] void SinceLastReload_ReturnsLinesAfterMarker()
  [Fact] void SinceLastReload_NoReload_ReturnsSinceSessionStart()
  [Fact] void Tail_ReturnsLastN()
  [Fact] void ConcurrentAppend_NoLostLines()        // 4 thread x 1000 append -> 4000 seq unik
  [Fact] void Classifier_HeaderAndFooter()          // pakai baris dari fixture runtime-exception (Task 7)
  ```
- [ ] **Step 2: Jalankan dan pastikan gagal.** Lalu implementasi dengan circular array dan `lock`.
- [ ] **Step 3: Jalankan test.** Hasil yang diharapkan: PASS.
- [ ] **Step 4: Checkpoint.** Commit message: `feat(core): add log ring buffer`.

---

### Task 9: `RunSession` (state machine)

**Files:**
- Create: `src/FlutterBar.Core/Sessions/SessionModels.cs`, `Sessions/RunSession.cs`
- Test: `tests/FlutterBar.Core.Tests/Sessions/RunSessionTests.cs`, dengan `FakeProcessHandle` dan `FakeLauncher` di `tests/FlutterBar.Core.Tests/Fakes/`. Fake ini memutar ulang file fixture Task 7 baris per baris, mencatat semua `WriteLineAsync`, dan bisa diperintah untuk exit.

**Interfaces:**
- Consumes: `IProcessLauncher`, `IProcessHandle`, `ProcessSpec` (Task 5), `DaemonProtocol` dan `DaemonMessage` (Task 7), `LogBuffer` dan `LogClassifier` (Task 8), `LaunchConfig` dan `RunMode` (Task 2).
- Produces:
  - `public enum SessionState { Idle, Starting, Running, Reloading, Stopping, Failed }`
  - `public sealed record RunRequest(string ProjectRoot, LaunchConfig Config, string DeviceId, ProcessSpec Spec)`
  - `public sealed record SessionSnapshot(SessionState State, bool HasError, int ErrorCount, string? Progress, string? FailureMessage, bool CanReload, bool CanRestart, RunRequest? Request)`
  - `public enum ReloadFailure { NoSession, Busy, Unsupported, Failed, Timeout }`
  - `public sealed record ReloadOutcome(bool Success, string Message, ReloadFailure? Failure)`
  - `public sealed class RunSession(IProcessLauncher launcher, LogBuffer log, TimeSpan? stopGrace = null)`, berisi:
    - `SessionSnapshot Snapshot`
    - `event Action<SessionSnapshot>? Changed`
    - `Task StartAsync(RunRequest request)`. Melempar `InvalidOperationException` kalau state bukan Idle atau Failed.
    - `Task<ReloadOutcome> ReloadAsync(bool fullRestart, TimeSpan timeout, CancellationToken ct = default)`
    - `Task StopAsync()`
    - `ValueTask DisposeAsync()`

- [ ] **Step 1: Tulis test yang gagal.**
  ```csharp
  [Fact] async Task Start_GoesStartingThenRunning_OnAppStarted()     // fixture start-ok
  [Fact] async Task Start_ProgressMessage_ExposedWhileStarting()
  [Fact] async Task ProcessExitsBeforeStarted_Failed_LogKept()       // fixture build-fail lalu exit 1 -> Failed, FailureMessage != null,
                                                                     // log berisi baris NonJson (Review Focus #3)
  [Fact] async Task OnlyNonJsonThenExit_Failed_NotStuckStarting()    // Review Focus #3
  [Fact] async Task Reload_SendsEncodedRequest_ReturnsSuccess()      // menulis EncodeRestart(id, appId, false); response code 0
  [Fact] async Task Reload_MarksLogSeparator()
  [Fact] async Task Reload_CompileError_FailureAndErrorFlag()
  [Fact] async Task SuccessfulReload_ClearsErrorFlag()
  [Fact] async Task Reload_WhileStarting_NoSession()                 // Review Focus #4
  [Fact] async Task Reload_WhileReloading_Busy()                     // Review Focus #4
  [Fact] async Task Reload_NoResponse_TimesOut_BackToRunning()
  [Fact] async Task ReleaseMode_CannotReloadOrRestart()
  [Fact] async Task SupportsRestartFalse_CannotRestart()
  [Fact] async Task ReloadRejectedByFlutter_DisablesReloadForSession() // fixture web-reload-unsupported
  [Fact] async Task RuntimeException_SetsErrorFlag_ErrorLinesKindError()
  [Fact] async Task Stop_SendsAppStop_ThenIdle()
  [Fact] async Task Stop_NoExitWithinGrace_KillsTree()               // stopGrace 100ms -> KillTree dipanggil
  [Fact] async Task UnexpectedExitWhileRunning_Failed()
  [Fact] async Task Start_WhileRunning_Throws()
  ```
- [ ] **Step 2: Jalankan dan pastikan gagal.**
- [ ] **Step 3: Implementasi** sesuai spec §6.2:
  - Response dicocokkan ke request lewat `id` dengan `TaskCompletionSource` per id.
  - Isi `LogKind`:
    - `AppLog` dengan `Error=true`, `DaemonLog` level `error`, dan semua baris di antara header dan footer exception → `Error`.
    - `AppLog` lainnya → `App`.
    - `NonJson`, `AppProgress`, dan `DaemonLog` lainnya → `Tool`.
    - Hasil reload → `Reload` (contoh: "Reloaded in 420ms" dari message response).
  - Setiap header exception menaikkan `ErrorCount` sebanyak 1.
  - `CanReload` = Running, mode Debug, dan reload belum ditolak. `CanRestart` = Running, mode Debug, dan `SupportsRestart`.
  - `Changed` dipanggil di thread mana pun. UI yang bertugas melakukan marshal ke Dispatcher.
- [ ] **Step 4: Jalankan test.** Hasil yang diharapkan: PASS.
- [ ] **Step 5: Checkpoint.** Commit message: `feat(core): add run session state machine`.

---

### Task 10: `DeviceService`

**Files:**
- Create: `src/FlutterBar.Core/Devices/DeviceModels.cs`, `Devices/DeviceParsers.cs`, `Devices/DeviceService.cs`
- Create fixtures: `tests/fixtures/devices/devices-machine.json` dan `tests/fixtures/devices/emulators.txt`, hasil rekaman dari `fvm flutter devices --machine` dan `fvm flutter emulators` di `tests/sample_app` saat satu emulator menyala dan satu mati
- Test: `tests/FlutterBar.Core.Tests/Devices/DeviceParsersTests.cs`, `Devices/DeviceServiceTests.cs` (dengan `IProcessRunner` palsu)

**Interfaces:**
- Consumes: `IProcessRunner` dan `FlutterToolLocator` (Task 5).
- Produces:
  - `public sealed record DeviceInfo(string Id, string Name, string Platform, bool IsEmulator, string? EmulatorId)`
  - `public sealed record EmulatorInfo(string Id, string Name)`
  - `public static class DeviceParsers`, berisi:
    - `IReadOnlyList<DeviceInfo> ParseDevicesMachine(string json)`
    - `IReadOnlyList<EmulatorInfo> ParseEmulators(string text)`
  - `public sealed class DeviceService(IProcessRunner runner, FlutterToolLocator locator)`, berisi:
    - `IReadOnlyList<DeviceInfo> CachedDevices`
    - `IReadOnlyList<EmulatorInfo> CachedEmulators`
    - `Task<IReadOnlyList<DeviceInfo>> RefreshDevicesAsync(string projectRoot, CancellationToken ct)`
    - `Task<IReadOnlyList<EmulatorInfo>> RefreshEmulatorsAsync(string projectRoot, CancellationToken ct)`
    - `IReadOnlyList<EmulatorInfo> OfflineEmulators()`, yaitu emulator yang tidak cocok dengan device yang sedang terhubung
    - `Task<DeviceInfo?> LaunchEmulatorAsync(string projectRoot, string emulatorId, TimeSpan pollInterval, TimeSpan timeout, CancellationToken ct)`

- [ ] **Step 1: Rekam fixture,** lalu catat di `tests/fixtures/devices/README.md` apakah field `emulatorId` muncul di output `--machine`.
- [ ] **Step 2: Tulis test yang gagal.**
  ```csharp
  [Fact] void ParseDevices_FromFixture()            // jumlah dan id sesuai fixture; chrome punya Platform "web-javascript"
  [Fact] void ParseEmulators_FromFixture()          // baris "Id • Name • Manufacturer • Platform"; abaikan header/footer
  [Fact] void ParseEmulators_NoneAvailable_Empty()
  [Fact] void Offline_ExcludesRunningEmulator()     // cocok lewat EmulatorId, atau fallback Name (dengan '_' diganti spasi)
  [Fact] async Task Refresh_UsesProjectFlutterTool()  // project dengan .fvmrc -> runner menerima fvm.exe flutter devices --machine
  [Fact] async Task Refresh_ToolError_Throws_WithMessage()
  [Fact] async Task LaunchEmulator_PollsUntilNewEmulatorAppears() // poll ke-3 berisi emulator -> mengembalikan device itu
  [Fact] async Task LaunchEmulator_Timeout_ReturnsNull()
  ```
- [ ] **Step 3: Jalankan dan pastikan gagal.** Lalu implementasi. Perintah yang dipakai: `flutter devices --machine` dan `flutter emulators`, keduanya timeout 60 detik. Untuk menyalakan emulator, `flutter emulators --launch <id>` timeout 120 detik, lalu poll sesuai parameter.
- [ ] **Step 4: Jalankan test.** Hasil yang diharapkan: PASS.
- [ ] **Step 5: Checkpoint.** Commit message: `feat(core): add device and emulator service`.

---

### Task 11: IPC & CLI `flutterbar`

**Files:**
- Create: `src/FlutterBar.Core/Ipc/IpcContracts.cs`, `Ipc/CommandHandler.cs`, `Ipc/IpcServer.cs`, `Ipc/IpcClient.cs`
- Create: `src/FlutterBar.Cli/Program.cs`, `src/FlutterBar.Cli/CliApp.cs`
- Test: `tests/FlutterBar.Core.Tests/Ipc/CommandHandlerTests.cs`, `Ipc/IpcRoundTripTests.cs`, `tests/FlutterBar.Core.Tests/Cli/CliAppTests.cs`

**Interfaces:**
- Consumes: `RunSession`, `SessionSnapshot`, dan `ReloadOutcome` (Task 9), `LogBuffer` (Task 8).
- Produces:
  - `public static class ExitCodes { const int Ok=0, Failed=1, NotRunning=2, NoSession=3, Timeout=4, Usage=64; }`
  - `public sealed record IpcRequest(string Command, IReadOnlyDictionary<string,string>? Args)`
  - `public sealed record IpcResponse(bool Ok, int ExitCode, JsonElement? Data, string Message)`
  - `public static class PipeNames { string ForCurrentUser(); }` → `FlutterBar-<SID>`
  - `public sealed record StatusInfo(string State, string? Project, string? Branch, string? Config, string? Device, int ErrorCount)`
  - `public interface IStatusProvider { StatusInfo Current(); }`
  - `public sealed class CommandHandler(RunSession session, LogBuffer log, IStatusProvider status, Action showWidget)`, dengan method `Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct)`. Perintah yang didukung: `status`, `reload`, `restart`, `stop`, `logs` (args `errors`, `tail`, `sinceLastReload`), dan `show` (dipakai oleh instance kedua).
  - `public sealed class IpcServer(string pipeName, Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler)`, dengan method `Task RunAsync(CancellationToken ct)`
  - `public interface IIpcClient { Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct); }`. Nilai null berarti pipe tidak ada. Implementasinya `IpcClient(string pipeName)`.
  - `public static class CliApp { Task<int> RunAsync(string[] args, IIpcClient client, TextWriter stdout, TextWriter stderr); }`

- [ ] **Step 1: Tulis test yang gagal.**
  ```csharp
  // CommandHandler (RunSession + FakeLauncher dari Task 9)
  [Fact] async Task Status_NoSession_Exit3()
  [Fact] async Task Status_Running_Exit0_DataHasProjectConfigDevice()
  [Fact] async Task Reload_Success_Exit0()
  [Fact] async Task Reload_CompileError_Exit1_MessageHasError()
  [Fact] async Task Reload_WhileStarting_Exit3()          // Review Focus #4
  [Fact] async Task Reload_WhileReloading_Exit1_SedangReload() // Message == "Sedang reload"
  [Fact] async Task Reload_Timeout_Exit4()
  [Fact] async Task Logs_ErrorsSinceLastReload_FiltersBoth()
  [Fact] async Task UnknownCommand_Exit64()
  // Round trip (pipe nyata, nama pipe unik per test)
  [Fact] async Task Client_ServerRoundTrip()
  [Fact] async Task Client_NoServer_ReturnsNullQuickly()  // < 1 detik
  // CliApp (IIpcClient palsu)
  [Fact] async Task NotRunning_Exit2_PrintsFlutterBarTidakBerjalan() // stderr berisi "FlutterBar tidak berjalan"
  [Fact] async Task Reload_JsonFlag_PrintsResponseJson()
  [Fact] async Task Reload_UsesTimeout60s()
  [Fact] async Task Logs_ParsesFlags()                    // --errors --tail 50 --since-last-reload -> Args sesuai
  [Fact] async Task NoArgs_OrUnknown_Exit64_PrintsUsage()
  [Fact] async Task RunCommand_NotSupported_Exit64()      // "flutterbar run" sengaja tidak ada
  ```
- [ ] **Step 2: Jalankan dan pastikan gagal.**
- [ ] **Step 3: Implementasi.**
  - Server memakai `NamedPipeServerStream` yang dibuat lewat `NamedPipeServerStreamAcl.Create` dengan `PipeSecurity` yang hanya memberi akses `FullControl` ke SID user saat ini.
  - Protokolnya satu baris JSON untuk request dan satu baris JSON untuk response per koneksi. Request diproses secara serial lewat `SemaphoreSlim(1)`.
  - CLI tanpa `--json` mencetak teks ringkas, misalnya `Running · gts-mobile · beta · emulator-5554 · 0 error`.
  - Untuk `reload` dan `restart`, timeout di sisi klien adalah 65 detik, dan handler memakai timeout session 60 detik.
- [ ] **Step 4: Jalankan test.** Hasil yang diharapkan: PASS.
- [ ] **Step 5: Checkpoint.** Commit message: `feat: add named-pipe IPC and flutterbar CLI`.

---

### Task 12: Kebijakan layout & geometri taskbar (murni, di Core)

**Files:**
- Create: `src/FlutterBar.Core/Layout/WidgetLayoutPolicy.cs`, `Layout/TaskbarGeometry.cs`
- Test: `tests/FlutterBar.Core.Tests/Layout/WidgetLayoutPolicyTests.cs`, `Layout/TaskbarGeometryTests.cs`

**Interfaces:**
- Produces:
  - `public enum WidgetSize { Full, Compact, ButtonsOnly }`
  - `public static class WidgetLayoutPolicy { WidgetSize? Choose(WidgetSize preferred, double available, IReadOnlyDictionary<WidgetSize,double> requiredWidth); }`. Nilai null berarti widget disembunyikan.
  - `public readonly record struct Box(double Left, double Top, double Right, double Bottom)`
  - `public static class TaskbarGeometry { Box? FreeAreaLeftOfTray(Box taskbar, Box tray, IReadOnlyList<Box> appButtons, double margin = 8); }`

- [ ] **Step 1: Tulis test yang gagal.**
  ```csharp
  [Fact] void Choose_FitsPreferred()                // Full 560, tersedia 600 -> Full
  [Fact] void Choose_StepsDownOneAtATime()          // tersedia 300 -> Compact (260); tersedia 150 -> ButtonsOnly (120)
  [Fact] void Choose_NeverLargerThanPreferred()     // preferred Compact, tersedia 1000 -> Compact
  [Fact] void Choose_NothingFits_Null()
  [Fact] void FreeArea_CenteredTaskbar()            // tombol aplikasi di 800..1100, tray mulai 1700 -> area 1108..1692
  [Fact] void FreeArea_LeftAlignedTaskbar()
  [Fact] void FreeArea_NoGap_Null()
  ```
- [ ] **Step 2: Jalankan dan pastikan gagal.** Lalu implementasi, dan jalankan test sampai PASS.
- [ ] **Step 3: Checkpoint.** Commit message: `feat(core): add widget layout policy and taskbar geometry`.

---

### Task 13: App: `TaskbarDocking` & shell `TaskbarWidget`

Prasyarat: gerbang keputusan Task 1 sudah lulus. Gunakan nama kelas dan AutomationId dari `docs/superpowers/spikes/2026-11-06-docking-findings.md`.

**Files:**
- Create: `src/FlutterBar.App/Native/NativeMethods.cs`, `Docking/TaskbarDocking.cs`, `Docking/TaskbarProbe.cs` (UI Automation, Win10/Win11), `Docking/FullscreenDetector.cs`, `Theme/TaskbarTheme.cs`
- Create: `src/FlutterBar.App/Widget/TaskbarWidget.xaml(.cs)`, `Widget/WidgetViewModel.cs`, `App.xaml(.cs)`

**Interfaces:**
- Consumes: `WidgetLayoutPolicy`, `TaskbarGeometry`, dan `Box` (Task 12), `AppSettingsStore` (Task 4), `RunSession` dan `SessionSnapshot` (Task 9).
- Produces:
  - `TaskbarProbe.Read() -> TaskbarInfo?`, dengan `record TaskbarInfo(Box Taskbar, Box Tray, IReadOnlyList<Box> AppButtons, bool AutoHide, bool Visible, double Dpi)`
  - `TaskbarDocking(Window window)`, dengan:
    - `event Action<Box?>? AreaChanged`
    - `void Start()`, `void Stop()`
    - `void SetManual(int offsetFromRight)`, `void ResetToAuto()`
    - `event Action<int>? ManualOffsetChanged` (dipicu saat drag selesai dengan offset baru, supaya app menyimpannya)
  - `WidgetViewModel` (INotifyPropertyChanged), dengan property:
    - `WidgetSize EffectiveSize`
    - `SessionSnapshot Session`
    - `string ProjectName`, `string Branch`, `string ConfigName`, `string DeviceName`
    - `bool IsHidden`
    - `ICommand RunCommand, ReloadCommand, RestartCommand, StopCommand, OpenLogCommand`

- [ ] **Step 1: Docking produksi.** Pindahkan teknik spike ke `TaskbarDocking` (detail teknik v2, v3, dan v3.1 di `docs/superpowers/spikes/2026-11-06-docking-findings.md`, bagian "Spike v3", "Spike v3.1", dan "Catatan untuk Task 13"):
  - **Catatan (keputusan user, v3.1):** `WidgetLayoutPolicy.Choose` mengembalikan `null` bila tidak ada ukuran yang muat (kode Core Task 12 tidak berubah). Di mode `auto`, `null` BUKAN berarti sembunyikan: widget tetap tampil di posisi valid terakhir (atau kiri `TrayNotifyWnd`) dalam ukuran `ButtonsOnly` dan menampilkan pesan "Taskbar penuh — gunakan Drag widget" (tooltip/teks), plus log. Widget hanya disembunyikan untuk fullscreen dan auto-hide.
  - Filter elemen UIA (v3.1): elemen UIA dihitung terisi bila: (a) rect-nya di dalam rect taskbar (toleransi 2 px), (b) tinggi >= 50% tinggi taskbar, (c) lebar >= 8 px,
  (d) bukan milik proses sendiri atau proses widget lain milik kita (`DockingSpike`, `FlutterBar.App`), dan (e) bukan transien (ControlType `ToolTip`,
  kelas popup/`TaskListThumbnailWnd`). Thumbnail/flyout digambar DI ATAS taskbar sehingga (a) sudah mengecualikannya. Catatan: TrafficMonitor tidak ada
  di pohon UIA `Shell_TrayWnd`, jadi tidak pernah terhitung (itu sebabnya ada mode manual). Catat (log) tiap (class, id, controlType, pid) baru sekali beserta verdict counted/ignored dan alasannya.
  - Debounce (v3.1): terapkan penempatan baru (pindah/ubah ukuran) hanya bila hasil (ukuran, x) yang sama keluar dari 2 query UIA berturut-turut (toleransi x 3 px); layout pertama setelah startup, setelah `ResetToAuto()`, dan setelah `TaskbarCreated` diterapkan langsung; kandidat berbeda yang ditahan >= 5 query berturut-turut diterapkan (max-hold). Log saat kandidat ditahan. Jangan salin hook uji spike (`--force-min-gap`, `--force-empty-after`) ke produksi.
  - Sanity check layout: hasil UIA tanpa elemen tray atau tanpa kotak terisi ditolak (pertahankan layout terakhir yang baik, log); `TrayNotifyWnd` (Win32) sebagai sumber/cek kiri tray (selisih > 300 px dengan UIA -> pakai TrayNotifyWnd).
  - Penangguhan topmost saat popup/menu/tooltip terbuka, dan topmost-kan HWND popup (`HwndSource`), menu dibuka ke atas.
  - Filter elemen UIA milik ProcessId sendiri.
  - Teknik owner (`GWLP_HWNDPARENT` = Shell_TrayWnd, `TaskbarCreated`, UI thread tidak boleh blocking).
  - Jendela `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, topmost, dengan topmost diterapkan ulang lewat WinEvent hook.
  - Re-probe dipicu oleh `EVENT_OBJECT_LOCATIONCHANGE` pada `Shell_TrayWnd`, ditambah timer 1 detik sebagai cadangan.
  - Lebar diambil dari `TaskbarGeometry.FreeAreaLeftOfTray`.
  - Widget ikut sembunyi dan muncul bersama auto-hide (v3.2): saat auto-hide menyala, pada tiap `EVENT_OBJECT_LOCATIONCHANGE` taskbar dan tiap tick timer hitung bagian rect `Shell_TrayWnd` yang terlihat di monitor utama (irisan `rcMonitor`); < 50% terlihat -> sembunyikan, selain itu tampilkan dengan Y di tengah vertikal rect taskbar hidup (ikut meluncur); tanpa re-layout UIA selagi tersembunyi; log sekali per transisi.
  - Lifecycle window dibuat ulang (v3.2): instance lama berhenti total (timer, WinEvent hook, hook WndProc, tanpa log lagi), `_hwnd` = handle window hidup, `Close()` yang lama setelah yang baru tampil, id instance di log.
  - Verifikasi owner (v3.2): `GetWindowLongPtr(GWLP_HWNDPARENT) == Shell_TrayWnd` (BUKAN `GetWindow(GW_OWNER)`, yang 0 selama Start/Search terbuka); jangan pasang ulang bila sudah sama (menghapus owner); verifikasi setelah pasang, log FAILED + `GetLastWin32Error`, buat ulang window bila gagal berulang (3 tick timer berturut-turut, dibatasi); `TaskbarCreated` + pasang ulang saja tidak cukup. Reassert pertama setelah window tampil (`Loaded`), sembunyi/tampil dari `IsWindowVisible`. Teks debug widget/"Hide debug info" hanya untuk spike.
  - `FullscreenDetector` memakai `SHQueryUserNotificationState`, dengan `QUNS_BUSY` atau `QUNS_RUNNING_D3D_FULL_SCREEN` berarti sembunyikan widget.
  - Tema dibaca dari `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\SystemUsesLightTheme`, dan perubahannya dipantau lewat `SystemEvents.UserPreferenceChanged`.
- [ ] **Step 2: Layout XAML untuk tiga ukuran** sesuai spec §7.1 dan mockup yang sudah disetujui:
  - Titik status 8 px.
  - Chip berisi teks dan chevron.
  - Tombol ikon 24 px: ▶ Run hijau, ⚡ Hot reload kuning, 🔄 Hot restart hijau, ⏹ Stop merah, 🖥 log.
  - Lebar yang dibutuhkan setiap ukuran diukur lewat `Measure`, lalu dimasukkan ke `WidgetLayoutPolicy.Choose`.
  - Visibilitas tombol diambil dari `Session.State`, `CanReload`, dan `CanRestart`. Tooltip-nya: "Tidak didukung di mode release/profile" dan "Hot reload tidak didukung di device ini".
  - Saat state Starting, titik status berkedip kuning dan progress ditampilkan di tooltip atau di layout lengkap.
- [ ] **Step 3: Resize lewat drag tepi kiri** mengubah `Widget.WidthOverride`. Menu klik kanan punya item centang "Drag widget" dan "Reset to auto position" (label Inggris, keputusan user). Saat "Drag widget" dicentang, widget bisa digeser horizontal saja dengan mouse capture (bukan `Window.DragMove`), dengan isyarat visual (border aksen 1 px + kursor SizeWE); layout tidak dihitung ulang saat drag. Un-centang setelah drag mengunci posisi: mode manual, `TaskbarDocking` memicu `ManualOffsetChanged(offset)` dan app menyimpannya sebagai `OffsetFromRight` (piksel fisik dari tepi kanan taskbar, di-clamp) lewat `AppSettingsStore`; saat start app memanggil `SetManual(offset)` bila ada, dan menu Reset memanggil `ResetToAuto()` (menghapus offset). Catatan: setting `LockedX` di `WidgetSettings` (Task 4) diinterpretasi ulang sebagai offset-from-right; Task 13 me-rename `LockedX` menjadi `OffsetFromRight` di `WidgetSettings` beserta test-nya.
- [ ] **Step 4: Verifikasi.** `dotnet build` tanpa warning. Jalankan app dan ulangi checklist Task 1 Step 4 di build ini. Catat hasilnya di `docs/superpowers/spikes/2026-11-06-docking-findings.md` di bawah bagian "Build produksi".
- [ ] **Step 5: Checkpoint.** Commit message: `feat(app): dock widget to taskbar with adaptive layout`.

---

### Task 14: App: flyout & wiring layanan

**Files:**
- Create: `src/FlutterBar.App/Flyouts/ProjectFlyout.xaml(.cs)`, `BranchFlyout.xaml(.cs)`, `ConfigFlyout.xaml(.cs)`, `DeviceFlyout.xaml(.cs)`, `CombinedFlyout.xaml(.cs)`, `Flyouts/FlyoutHost.cs` (Popup di atas widget)
- Create: `src/FlutterBar.App/Services/AppController.cs`, yang menjadi titik orkestrasi
- Create: `src/FlutterBar.App/Widget/Toast.xaml(.cs)`
- Modify: `src/FlutterBar.App/Widget/WidgetViewModel.cs`

**Interfaces:**
- Consumes: semua service Core dari Task 2–11.
- Produces: `AppController`, dengan:
  - `Task InitializeAsync()`
  - `Task SelectProjectAsync(string path)`, `Task SelectConfigAsync(string name)`, `Task SelectDeviceAsync(string id)`, `Task SwitchBranchAsync(string branch)`
  - `Task RunAsync()`, `Task<ReloadOutcome> ReloadAsync(bool full)`, `Task StopAsync()`
  - `Task LaunchEmulatorAsync(string emulatorId)`
  - `void ShowToast(string message, string? actionLabel = null, Func<Task>? action = null)`
  - `AppController` juga mengimplementasikan `IStatusProvider` untuk Task 11.

- [ ] **Step 1: `AppController.InitializeAsync`.**
  - Load settings. Kalau ada warning, tampilkan toast.
  - Jalankan scan project di background.
  - Pulihkan `CurrentProject`, config, dan device terakhir.
  - Pasang `HeadWatcher` untuk project aktif.
  - Jalankan `IpcServer` dengan `CommandHandler`.
- [ ] **Step 2: Flyout project.** Berisi kolom pencarian dan item `ProjectInfo` (item yang tidak tersedia tampil abu-abu dan tidak bisa dipilih). Rescan otomatis kalau scan terakhir sudah lebih dari 10 menit, dan item dari cache tampil lebih dulu.
- [ ] **Step 3: Flyout branch.** Berisi kolom pencarian. Memilih branch menjalankan `SwitchBranchAsync`. Kalau gagal, tampilkan toast berisi `GitResult.Message`. Kalau sesi sedang berjalan, toast mengikuti `BranchChangeAdvice`:
  - `HotRestart`: "Branch berganti, hot restart?" dengan aksi "Hot restart".
  - `RerunRequired`: "pubspec berubah, stop lalu run ulang".
  - Chip branch disembunyikan kalau `IsRepoAsync` bernilai false.
- [ ] **Step 4: Flyout config.**
  - Setiap kali dibuka, `LaunchConfigStore.Load` dipanggil (membaca ulang dari disk).
  - Kalau `FileExists=false`: tampilkan "Belum ada config" dengan aksi "Import dari IDE" (Task 16) dan "Buat default".
  - Kalau ada `Errors`: chip berwarna merah, dan flyout menampilkan setiap error (baris/field) dengan aksi "Buka file".
  - Tooltip chip berisi `program`, `flavor`, dan `mode`.
- [ ] **Step 5: Flyout device.**
  - Saat dibuka, `CachedDevices` langsung ditampilkan, lalu `RefreshDevicesAsync` dan `RefreshEmulatorsAsync` dijalankan dengan spinner.
  - Ada dua bagian: "Terhubung" dan "Emulator (offline)".
  - Memilih emulator offline menjalankan `LaunchEmulatorAsync` (poll 5 detik, timeout 120 detik). Kalau timeout, tampilkan toast "Emulator belum siap setelah 2 menit".
- [ ] **Step 6: Run.**
  - Refresh device dulu. Kalau device terakhir tidak ada, buka flyout device dan batalkan run.
  - Lalu `FlutterToolLocator.Resolve`. Kalau `Error`, tampilkan toast, tulis ke log, dan batalkan run.
  - Lalu `FlutterCommandBuilder`, lalu `RunSession.StartAsync`.
  - Simpan `LastUsed`, config, dan device ke settings.
- [ ] **Step 7: Konfirmasi satu sesi.** Mengganti project, config, atau device saat state bukan Idle atau Failed memunculkan dialog "Stop sesi sekarang dan ganti?". Kalau Ya: Stop, ubah pilihan, dan jangan run otomatis.
- [ ] **Step 8: Verifikasi manual** dengan gts-mobile:
  - Pilih config `beta` setelah import (atau buat `launch.json` secara manual dulu).
  - Pilih device, Run, reload, restart, lalu Stop.
  - Ganti branch dari terminal saat widget terbuka: chip harus berubah dalam waktu kurang dari 2 detik.
  - Edit `launch.json` dari luar, lalu buka flyout config: isinya harus nilai baru.
- [ ] **Step 9: Checkpoint.** Commit message: `feat(app): add project, branch, config and device flyouts`.

---

### Task 15: App: `LogWindow`

**Files:**
- Create: `src/FlutterBar.App/Log/LogWindow.xaml(.cs)`, `Log/LogViewModel.cs`, `Log/LogEntryGroup.cs` (pengelompokan blok exception)
- Modify: `src/FlutterBar.App/Widget/WidgetViewModel.cs`, supaya `OpenLogCommand` dan klik titik merah membuka `LogWindow` di posisi error

**Interfaces:**
- Consumes: `LogBuffer`, `LogLine`, `LogKind`, dan `LogClassifier` (Task 8).
- Produces: `LogWindow.ShowAt(long? seq)`, sebuah singleton. Kalau jendela sudah terbuka, fokus dan scroll ke `seq` yang diminta.

- [ ] **Step 1: Daftar log.** Pakai `ListBox` dengan `VirtualizingStackPanel.IsVirtualizing=True` dan `VirtualizationMode=Recycling`.
  - Item diambil dari `LogBuffer.Snapshot()` saat jendela dibuka, lalu bertambah lewat `Appended` (di-marshal ke Dispatcher dan di-batch setiap 100 ms).
  - Warna per `LogKind` sesuai spec §8.
  - Blok exception (dari header sampai footer) dikelompokkan menjadi satu item yang bisa dilipat.
- [ ] **Step 2: Toolbar.**
  - Pencarian: filter case-insensitive dengan debounce 200 ms.
  - Toggle "Errors only".
  - ⬆/⬇ untuk lompat ke error sebelumnya/berikutnya.
  - Clear hanya menyembunyikan baris sampai seq saat ini dari tampilan. Buffer tidak diubah.
  - Copy: baris yang dipilih, atau semua kalau tidak ada yang dipilih.
- [ ] **Step 3: Auto-scroll.** Aktif hanya selama posisi scroll berada di bawah (toleransi 2 item).
- [ ] **Step 4: Menutup `LogWindow` tidak menghentikan sesi.** Instance-nya dibuang, dan saat dibuka lagi dibuat ulang dari buffer.
- [ ] **Step 5: Verifikasi manual.**
  - Isi buffer dengan 20.000 baris (tambahkan tombol tersembunyi khusus `#if DEBUG` yang menulis baris dummy). Scroll harus tetap lancar.
  - Catat working set dengan `LogWindow` terbuka.
  - Dengan fixture runtime-exception (Task 7) di sample_app: blok exception harus terlipat dan berwarna merah.
- [ ] **Step 6: Checkpoint.** Commit message: `feat(app): add log window`.

---

### Task 16: App: menu, import, single instance, diagnostik, dan integrasi sistem

**Files:**
- Create: `src/FlutterBar.App/Menu/WidgetContextMenu.xaml(.cs)`, `Import/ImportDialog.xaml(.cs)`, `Projects/ManageProjectsDialog.xaml(.cs)`, `System/StartupRegistration.cs`, `System/UserPathEditor.cs`, `System/SingleInstance.cs`
- Create: `src/FlutterBar.Core/Diagnostics/DiagLog.cs`
- Test: `tests/FlutterBar.Core.Tests/Diagnostics/DiagLogTests.cs`
- Modify: `src/FlutterBar.App/App.xaml.cs`

**Interfaces:**
- Consumes: `IdeConfigImporter`, `ImportCandidate`, dan `LaunchConfigStore` (Task 2–3), `AppSettingsStore` dan `ProjectCatalog` (Task 4), `IpcClient` dan `IpcRequest("show")` (Task 11), `AppController` (Task 14).
- Produces: `public static class DiagLog`, dengan:
  - `void Write(string message, Exception? ex = null)`, yang menulis ke `%APPDATA%\FlutterBar\diag.log`
  - `void Initialize(string path, long maxBytes = 1_000_000)`, yang merotasi file ke `diag.log.1` kalau ukurannya melebihi batas

- [ ] **Step 1: Test `DiagLog`.**
  ```csharp
  [Fact] void Write_AppendsTimestampAndException()
  [Fact] void Write_RotatesWhenOverMax()
  [Fact] void Write_ConcurrentCalls_NoException()
  ```
  Jalankan dan pastikan gagal, implementasi, lalu jalankan sampai PASS.
- [ ] **Step 2: Single instance.** Pakai named mutex `Local\FlutterBar-<SID>`. Kalau mutex sudah dipegang, instance baru mengirim `IpcRequest("show")` lalu keluar. Kalau menerima `show`, widget tampil dan kedip satu kali.
- [ ] **Step 3: Handler global.** Pasang handler di `DispatcherUnhandledException`, `AppDomain.UnhandledException`, dan `TaskScheduler.UnobservedTaskException`. Semuanya memanggil `DiagLog.Write` dan menampilkan toast "FlutterBar mengalami error, detail di diag.log". Exception di dispatcher ditandai handled supaya widget tetap hidup.
- [ ] **Step 4: Menu klik kanan** sesuai spec §7.4:
  - Size.
  - Drag widget (centang) dan Reset to auto position (label Inggris, keputusan user); lihat Task 13 Step 3 untuk perilaku drag/kunci.
  - Kelola project: dialog folder root, tambah project, daftar project tersembunyi yang bisa di-unhide, dan Rescan. Rescan juga memanggil `IPathResolver.ClearCache()`.
  - Import config dari IDE: dialog checklist berisi `ImportCandidate`. Item dengan `AlreadyExists` tidak dicentang secara default. Simpan lewat `Merge` lalu `Save`.
  - Buka `launch.json` dengan `Process.Start` dan `UseShellExecute=true`.
  - Buka `LogWindow`.
  - Tambahkan CLI ke PATH, setelah dialog konfirmasi:
    - `UserPathEditor` menambahkan folder `flutterbar.exe` ke `HKCU\Environment\Path` kalau belum ada. Nilainya harus tetap bertipe `REG_EXPAND_SZ`.
    - Lalu broadcast `WM_SETTINGCHANGE` "Environment".
    - Tampilkan toast "Buka terminal baru agar PATH terbaca".
  - Start with Windows: toggle `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\FlutterBar`.
  - Exit: kalau ada sesi aktif, tampilkan konfirmasi, lalu Stop, lalu keluar.
- [ ] **Step 5: Verifikasi manual.**
  - Jalankan `FlutterBar.App.exe` dua kali: hanya satu widget yang ada.
  - Import di gts-mobile: muncul 5 kandidat, yaitu `beta` dan `live` dari idea serta Dev, Beta, dan Live dari vscode.
  - Tambahkan ke PATH, lalu di terminal baru jalankan `flutterbar status`.
  - Exit saat app sedang jalan: tidak boleh ada proses `dart.exe` atau `java.exe` dari sesi itu yang tersisa (cek di Task Manager).
- [ ] **Step 6: Checkpoint.** Commit message: `feat(app): add context menu, IDE import and system integration`.

---

### Task 17: Integration test, checklist manual, dan pengukuran RAM

**Files:**
- Create: `tests/FlutterBar.Core.Tests/Integration/EndToEndTests.cs` (`[Trait("Category","Integration")]`)
- Create: `docs/manual-test-checklist.md`
- Modify: `README.md` (build, cara menjalankan, CLI, dan isi `launch.json`)

- [ ] **Step 1: Tulis integration test.**
  - Kalau env var `FLUTTERBAR_IT_DEVICE` kosong, test di-skip.
  - Alurnya:
    - `FlutterToolLocator` membaca `tests/sample_app` (yang punya `.fvmrc`).
    - `RunSession.StartAsync` dengan config default dan device dari env var. Tunggu sampai Running (timeout 10 menit, karena build pertama lambat).
    - `CommandHandler` dengan `reload`: exit 0.
    - `restart`: exit 0.
    - `logs --since-last-reload`: tidak kosong.
    - `stop`: state menjadi Idle.
    - Setelah itu, tidak ada proses turunan yang tersisa: semua PID yang tercatat oleh Job Object sudah mati.
  - Tambahkan juga test `DisposeWhileRunning_NoOrphans`. Test ini men-dispose `RunSession` dan `ProcessLauncher` saat state Running, lalu memastikan semua PID turunan mati.
- [ ] **Step 2: Jalankan integration test.** Perintah: `$env:FLUTTERBAR_IT_DEVICE="<id>"; dotnet test --filter Category=Integration`. Hasil yang diharapkan: PASS. Catat device yang dipakai.
- [ ] **Step 3: Tulis `docs/manual-test-checklist.md`.** Isinya checklist docking (spec §11), alur gts-mobile dari Task 14 Step 8, dan Task 16 Step 5. Jalankan seluruh checklist dan isi hasilnya.
- [ ] **Step 4: Ukur RAM.**
  - Working set `FlutterBar.App` saat idle 5 menit setelah start, dan saat `LogWindow` terbuka berisi 20.000 baris.
  - Catat angkanya di README. Targetnya di bawah 100 MB saat idle (spec §1). Kalau target tidak tercapai, laporkan angka sebenarnya ke user. Jangan klaim target tercapai.
- [ ] **Step 5: Verifikasi akhir.** Jalankan `dotnet build FlutterBar.sln` (0 warning) dan `dotnet test` (semua unit test PASS).
- [ ] **Step 6: Checkpoint.** Commit message: `test: add end-to-end session test and manual checklist`.

---

## Cakupan spec → task

| Spec | Task |
|---|---|
| §3 struktur solution, dua exe | 0, 11 |
| §4.1 `launch.json` + command | 2 |
| §4.2 import IDE | 3, 16 |
| §4.3 settings | 4 |
| §5.1 katalog project | 4, 14 |
| §5.2–5.3 fvm, `.bat`, Job Object | 5 |
| §5.4 device & emulator | 10, 14 |
| §5.5 git | 6, 14 |
| §6 protokol, state machine, log | 7, 8, 9 |
| §7 widget, adaptif, docking, menu | 1, 12, 13, 14, 16 |
| §8 `LogWindow` | 15 |
| §9 CLI & IPC | 11, 16 (PATH) |
| §10 penanganan error | 2, 4, 5, 9, 14, 16 |
| §11 testing & RAM | semua, 17 |
| §12 prasyarat .NET 10, risiko docking | 0, 1 |
