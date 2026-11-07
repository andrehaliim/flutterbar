# FlutterBar — Feedback Uji Putaran 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Menerapkan 6 catatan user dari uji manual putaran 1: seluruh teks ke bahasa Inggris, ikon log Console putih, menu klik kanan gelap ala Windows 11, "Import from IDE" di flyout config, resize manual dihapus, dan aksi git (fetch, pull, commit and push) di flyout branch.

**Architecture:** Perubahan di atas codebase yang sudah ada (`src/FlutterBar.Core`, `src/FlutterBar.App`, `src/FlutterBar.Cli`). Logika git baru di Core (`GitService`) dengan unit test memakai repo git asli; UI tetap tipis.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), WPF, xUnit. Tidak ada package baru.

**Spec:** `docs/superpowers/specs/2026-11-06-flutterbar-design.md` (diperbarui oleh task-task ini). Rencana induk: `docs/superpowers/plans/2026-11-06-flutterbar.md` (Global Constraints di sana tetap berlaku, kecuali aturan bahasa UI yang diganti di bawah).

## Global Constraints

- Semua constraint di `docs/superpowers/plans/2026-11-06-flutterbar.md` baris 13–27 tetap berlaku, **kecuali**: teks UI dan CLI sekarang **bahasa Inggris** (keputusan user 2026-11-08), sentence case, singkat, tanpa "please"/"successfully"/tanda seru. Identifier kode tetap bahasa Inggris.
- Exit code CLI tidak berubah (0, 1, 2, 3, 4, 64).
- Git: commit lokal di `main` dengan commit message di langkah Checkpoint, tanpa baris atribusi AI apa pun. Jangan push, jangan buat branch.
- UI thread tidak boleh pernah diblokir (widget dimiliki taskbar). Popup/menu mengikuti aturan topmost-suspension yang ada (`TaskbarDocking.Popups.cs`).
- Tidak boleh ada `catch` yang menelan exception (DiagLog + pesan ke user).
- Verifikasi per task: `dotnet format --include <file yang diubah>`, `dotnet build FlutterBar.sln` (Debug dan Release) tanpa warning, `dotnet test` hijau.

## Review Focus

1. **Push/pull memakai kredensial user:** git tidak boleh macet menunggu input terminal (`GIT_TERMINAL_PROMPT=0`), tidak pernah `--force`, dan timeout 2 menit. Test di Task 3.
2. **Pull/commit saat sesi berjalan:** setelah pull yang mengubah file, saran hot restart / run ulang sama seperti ganti branch. Test di Task 3.
3. **Teks lama berbahasa Indonesia tertinggal** di UI, toast, dialog, LogWindow, CLI, atau pesan error Core. Task 1 menambah test yang memindai string literal.
4. **Menu gelap mengikuti tema** dan tetap tampil di atas widget (topmost-suspension). Cek manual di Task 2.
5. **`widthOverride` lama di settings.json** milik user tidak boleh membuat widget berukuran aneh setelah resize manual dihapus. Test di Task 2.

---

### Task 1: Seluruh teks app ke bahasa Inggris

**Files:**
- Modify: semua file di `src/FlutterBar.App/**/*.xaml(.cs)`, `src/FlutterBar.Cli/*.cs`, dan pesan user-facing di `src/FlutterBar.Core/**` (mis. `FlutterToolLocator`, `GitService`, `BranchFlow`, `RunOrchestrator`, `RunCoordinator`, `CommandHandler`, `AppSettingsStore` warning, `DeviceService`, `ImportPlan`, `ProjectListEditor`, `SystemActions`, `LogView` "(N baris)"/"(lanjutan)").
- Modify: test yang meng-assert teks persis.
- Create: `tests/FlutterBar.Core.Tests/Localization/NoIndonesianStringsTests.cs`
- Modify: spec §7–§10 (copy), `README.md`, `docs/manual-test-checklist.md` (teks yang dikutip di kolom "hasil yang diharapkan").

- [ ] **Step 1: Inventaris.** Grep semua string literal user-facing berbahasa Indonesia (kata kunci contoh: "tidak", "belum", "sedang", "dibatalkan", "berjalan", "pilih", "gagal", "baris", "lanjutan", "Buka", "Keluar", "Kelola", "Tambah", "Cari", "Hanya", "Bersihkan", "Salin", "Ringkas", "Lengkap", "Tombol saja", "Ya, stop", "Batal"). Buat tabel terjemahan di laporan.
- [ ] **Step 2: Tulis test yang gagal** `NoIndonesianStringsTests`: memindai file `.cs` dan `.xaml` di `src/` (kecuali komentar) untuk daftar kata Indonesia umum sebagai kata utuh; gagal dengan daftar file:baris. Daftar kata minimal: `tidak, belum, sedang, dibatalkan, berjalan, pilih, gagal, baris, lanjutan, buka, keluar, kelola, tambah, cari, hanya, bersihkan, salin, ringkas, lengkap, batal, ganti, sesi, sembunyi, perubahan, ditemukan`.
- [ ] **Step 3: Terjemahkan.** Contoh copy wajib (persis):
  - CLI tidak berjalan: `FlutterBar is not running`
  - Reload ganda: `Reload in progress`
  - Konfirmasi satu sesi: `Stop the current session and switch?` dengan tombol `Stop` / `Cancel`
  - Konfirmasi keluar: `Stop the session and exit?`
  - Branch berganti saat sesi jalan: `Branch changed. Hot restart?` (aksi `Hot restart`); pubspec berubah: `pubspec changed. Stop and run again.`
  - Config belum ada: `No config yet` dengan `Import from IDE` / `Create default`
  - Run dibatalkan: `Run cancelled`
  - Emulator: `Starting emulator…`, `Emulator isn't ready after 2 minutes`
  - Menu: `Size` (`Full`, `Compact`, `Buttons only`), `Drag widget`, `Reset to auto position`, `Manage projects` (`Root folders…`, `Add project…`, `Hidden projects…`, `Rescan`), `Import config from IDE…`, `Open launch.json`, `Open log`, `Add CLI to PATH…`, `Start with Windows`, `Exit`
  - LogWindow: title `FlutterBar — Log`, `Search…`, `Errors only`, `Clear`, `Copy`, group suffix `(N lines)`, continuation `(continued)`
  - Error global: `FlutterBar hit an error. Details are in diag.log`
  - Argumen berkutip: `Arguments with quotes (") aren't supported: <arg>. Use --dart-define-from-file for complex values.`
- [ ] **Step 4: Jalankan test** sampai hijau (termasuk test lama yang diperbarui).
- [ ] **Step 5: Dokumen.** Spec: ganti aturan "Teks UI dalam Bahasa Indonesia" menjadi bahasa Inggris (keputusan user 2026-11-08) dan perbarui copy yang dikutip; README dan checklist ikut.
- [ ] **Step 6: Checkpoint.** Commit message: `refactor: switch all user-facing text to English`.

---

### Task 2: Ikon log, menu gelap, import di flyout config, hapus resize manual

**Files:**
- Modify: `src/FlutterBar.App/Widget/TaskbarWidget.xaml(.cs)`, `src/FlutterBar.App/Menu/WidgetContextMenu.xaml(.cs)`, `src/FlutterBar.App/Theme/TaskbarTheme.cs`, `src/FlutterBar.App/Flyouts/ConfigFlyout.xaml(.cs)`, `src/FlutterBar.App/Docking/TaskbarDocking.Drag.cs` (resize), `src/FlutterBar.Core/Settings/AppSettings.cs` (WidthOverride), test terkait.

**Interfaces:**
- Consumes: `AppController.ImportRequested` / import dialog dari Task 16 induk; `WidgetSizeMenu`.

- [ ] **Step 1: Ikon log** = glyph Console Segoe Fluent Icons `&#xE756;` (CommandPrompt), ukuran sama dengan tombol lain. Warna = brush tema foreground widget (putih di dark mode, gelap di light mode). Tooltip `Open log`.
- [ ] **Step 2: Menu klik kanan ala Windows 11**, mengikuti tema Windows (resource dari `TaskbarTheme`): background `#2C2C2C` / border `#3A3A3A` (dark) dan `#F9F9F9` / `#E5E5E5` (light), corner radius 8, padding 4, item tinggi ~32 px, radius item 4, hover `#3D3D3D` (dark) / `#EAEAEA` (light), teks `#F3F3F3` / `#1A1A1A`, glyph ikon Segoe Fluent Icons 16 px di kolom kiri, checkmark untuk item checkable, chevron untuk submenu, separator 1 px. Submenu memakai style yang sama. Urutan dan isi menu tetap (spec §7.4). Topmost-suspension tetap berlaku (cek class handler ContextMenu masih terpicu untuk subclass/submenu).
- [ ] **Step 3: Flyout config** selalu menampilkan tombol `Import from IDE` di samping `Open file` (baik saat config valid, invalid, atau belum ada), memanggil alur import yang sama dengan menu.
- [ ] **Step 4: Hapus resize manual** (drag tepi kiri). `WidgetSettings.WidthOverride` diabaikan: saat load, nilai lama dihapus (diset null) dan ditulis ulang saat save berikutnya; lebar hanya dari ukuran (Full/Compact/ButtonsOnly). Test: settings berisi `widthOverride` → setelah load nilainya null dan ukuran widget dihitung tanpa override.
- [ ] **Step 5: Verifikasi** build/test; launch singkat (scratch dir kalau instance user berjalan) untuk memastikan menu dan flyout terbuka tanpa exception; tambah baris checklist (`BELUM DIUJI (user)`): menu gelap/terang mengikuti tema, ikon log putih, Import from IDE di flyout config, tidak bisa resize lewat tepi kiri.
- [ ] **Step 6: Checkpoint.** Commit message: `feat(app): restyle menu and log icon, add import to config flyout`.

---

### Task 3: Git fetch, pull, commit and push di flyout branch

**Files:**
- Modify: `src/FlutterBar.Core/Git/GitService.cs`, `src/FlutterBar.Core/Orchestration/BranchFlow.cs` (atau tempat setara), test `tests/FlutterBar.Core.Tests/Git/GitServiceTests.cs`.
- Modify: `src/FlutterBar.App/Flyouts/BranchFlyout.xaml(.cs)`, `src/FlutterBar.App/Services/AppController*.cs`.

**Interfaces:**
- Produces (Core, `GitService`):
  - `Task<GitStatusInfo> GetStatusAsync(string root, CancellationToken ct)` → `record GitStatusInfo(int ChangedFiles, int Ahead, int Behind, string? Upstream)` (dari `git status --porcelain=v2 --branch`)
  - `Task<GitResult> FetchAsync(string root, CancellationToken ct)` → `git fetch --prune`
  - `Task<GitResult> PullAsync(string root, CancellationToken ct)` → `git pull` (tanpa opsi rebase/merge tambahan; ikut config user)
  - `Task<GitResult> CommitAndPushAsync(string root, string message, CancellationToken ct)` → `git add -A`, `git commit -m <message>`, lalu `git push`, atau `git push -u origin <branch>` bila belum ada upstream
- Semua perintah jaringan (fetch, pull, push) memakai environment `GIT_TERMINAL_PROMPT=0` dan timeout 2 menit; tidak pernah `--force`. Pesan gagal = stderr git apa adanya.

- [ ] **Step 1: Tulis test yang gagal** dengan repo git asli + remote bare lokal (`git init --bare` di folder temp): `Status_CountsChangesAheadBehind`, `Fetch_Prune_Ok`, `Pull_FastForward_UpdatesWorkingTree`, `Pull_Conflict_ReturnsGitStderr`, `CommitAndPush_StagesAllAndPushes` (remote berisi commit baru), `CommitAndPush_NoUpstream_SetsUpstream`, `CommitAndPush_EmptyMessage_Rejected`, `CommitAndPush_NothingToCommit_ReturnsMessage`, `Push_Rejected_NonFastForward_SuggestsPull` (pesan berisi saran `Pull first`), `NetworkCommands_UseNoTerminalPrompt` (ProcessSpec env berisi `GIT_TERMINAL_PROMPT=0`; `ProcessSpec` boleh diperluas dengan environment opsional bila belum ada).
- [ ] **Step 2: Jalankan dan pastikan gagal**, lalu implementasi, lalu hijau.
- [ ] **Step 3: UI flyout branch** (mockup B yang disetujui user): baris tombol `Fetch` dan `Pull` (dengan angka behind bila > 0), kolom `Commit message` + teks `N changed files` + tombol `Commit and push` (aktif hanya bila pesan tidak kosong; tanpa perubahan → toast `Nothing to commit`), lalu kolom cari dan daftar branch seperti sekarang. Spinner/disable tombol selama operasi berjalan; hasil sukses = toast singkat (`Fetched`, `Pulled`, `Pushed to <branch>`), gagal = toast berisi stderr git. Status ahead/behind dan jumlah file di-refresh saat flyout dibuka dan setelah setiap operasi.
- [ ] **Step 4: Saat sesi berjalan**, setelah pull yang mengubah commit HEAD: pakai saran yang sama dengan ganti branch (`BranchChangeAdvice`: hot restart vs stop dan run ulang bila pubspec berubah). Commit and push tidak memicu saran.
- [ ] **Step 5: Verifikasi** build/test; tambah baris checklist (`BELUM DIUJI (user)`): Fetch, Pull (fast-forward dan konflik), Commit and push ke remote sungguhan, push ditolak → saran Pull, branch tanpa upstream, saran hot restart setelah pull saat app berjalan, login Git Credential Manager muncul bila perlu.
- [ ] **Step 6: Checkpoint.** Commit message: `feat: add fetch, pull and commit-and-push to the branch flyout`.
