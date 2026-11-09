# FlutterBar — Feedback Uji Putaran 2 Implementation Plan

**Goal:** Menerapkan 9 temuan dari uji manual putaran 2: teks progress di samping chip device dihapus, klik tetap
berfungsi saat Drag widget aktif, tombol loncat ke log terbaru, redesign mode Compact, tombol
Flutter (clean / pub get / doctor / build_runner) dan Open terminal, mirror layar HP via scrcpy, widget di taskbar
monitor kedua, dan sisa teks Indonesia di `SessionControls`.

**Architecture:** Perubahan di atas codebase yang ada (`src/FlutterBar.Core`, `src/FlutterBar.App`). Logika baru
(perintah flutter, peluncur terminal, peluncur scrcpy) di Core dengan unit test; UI tetap tipis.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), WPF, xUnit. Tidak ada package baru. scrcpy = program eksternal
opsional yang diinstal user sendiri.

**Spec:** `docs/superpowers/specs/2026-11-06-flutterbar-design.md` (diperbarui per batch).

## Global Constraints

- Global Constraints `docs/superpowers/plans/2026-11-08-flutterbar-round1-feedback.md` tetap berlaku (teks UI bahasa
  Inggris, sentence case; UI thread tidak pernah diblokir; tidak ada `catch` yang menelan exception).
- Satu batch = satu commit, dilakukan oleh user (agen hanya memberi commit message).
- Verifikasi per batch: `dotnet format --include <file yang diubah>`, `dotnet build FlutterBar.sln` (Debug dan
  Release) tanpa warning, `dotnet test` hijau, baris baru di `docs/manual-test-checklist.md` (`BELUM DIUJI (user)`).
- Mockup ditunjukkan dan disetujui user sebelum batch UI baru (batch 2, 4, 5).

## Keputusan user (2026-11-09)

| # | Keputusan |
|---|---|
| 2 | Di mode drag, klik tanpa gerakan tetap jadi klik biasa; drag baru mulai setelah melewati jarak minimum |
| 3 | Tombol mengambang "↓ Latest" (hanya saat tidak di bawah) + `Ctrl+End` |
| 4 | Clean, Pub get, Clean + pub get, Doctor, build_runner (hanya bila ada di pubspec); flyout berupa daftar baris (mockup opsi A) |
| 5 | Tombol HP di samping log, hanya untuk HP Android fisik; scrcpy diinstal user |
| 6 | Pindah via menu *Show on ▸*, tanpa duplikat; spike dulu |
| 7 | Chip jadi tab (daftar di bawahnya, satu popup), label "Configuration", tooltip nilai aktif, juga untuk Buttons only; default ukuran tetap Full (user 2026-11-09, setelah sempat diputuskan Compact) |
| 8 | Di flyout Flutter dan menu klik kanan; `wt.exe`, fallback PowerShell |

---

### Batch 1: Perbaikan kecil (temuan 1, 2, 9)

**Files:** `src/FlutterBar.App/Widget/TaskbarWidget.xaml`, `src/FlutterBar.App/Docking/TaskbarDocking.Drag.cs`,
`src/FlutterBar.Core/Sessions/SessionControls.cs`, `tests/FlutterBar.Core.Tests/Sessions/SessionControlsTests.cs`,
`tests/FlutterBar.Core.Tests/Localization/NoIndonesianStringsTests.cs`, spec §6.2/§7.3, checklist.

- [ ] Hapus `TextBlock` progress di samping chip device (layout Full). Progress tetap di tooltip ⏳ dan titik status,
  dan tetap tercatat di log.
- [ ] Drag: tekan mouse hanya mencatat titik awal (tidak di-`Handled`); capture + gesture baru mulai setelah gerakan
  horizontal ≥ `SystemParameters.MinimumHorizontalDragDistance`. Capture window merebut capture dari Button sehingga
  tombol tidak ter-klik setelah drag. Tanpa gerakan, klik sampai ke tombol/chip.
- [ ] `"Memulai…"` → `"Starting…"`, `"Menghentikan…"` → `"Stopping…"`; tambah `memulai`, `menghentikan` ke
  `NoIndonesianStringsTests`.
- [ ] Commit message: `fix(app): keep clicks working in drag mode and drop the progress label`.

### Batch 2: Loncat ke log terbaru (temuan 3)

**Files:** `src/FlutterBar.App/Log/LogWindow.xaml(.cs)`, checklist.

- [ ] Bungkus `ListBox` dengan `Grid`; tombol bulat "↓" pojok kanan bawah, terlihat hanya bila `_atBottom == false`
  (diperbarui di `OnScrollChanged`).
- [ ] Klik tombol atau `Ctrl+End`: `_atBottom = true; ScrollToEnd()`.

### Batch 3: Redesign Compact (temuan 7)

**Files:** `src/FlutterBar.App/Flyouts/CombinedFlyout.xaml(.cs)`, `src/FlutterBar.App/Services/AppController.Selection.cs`,
`src/FlutterBar.App/Widget/TaskbarWidget.xaml`, `src/FlutterBar.App/Widget/WidgetViewModel.cs`, spec §7.1, checklist.

- [ ] Flyout gabungan: baris 4 chip (branch disembunyikan bila bukan repo git) + area konten; klik chip memuat
  flyout bagian itu di area konten dalam popup yang sama; chip aktif disorot.
- [ ] Chip Compact berlabel `Configuration ▾`, tooltip `project · branch · config · device`.
- [ ] Default ukuran tetap Full (rencana awal default Compact dibatalkan user).

### Batch 4: Tombol Flutter dan Open terminal (temuan 4, 8)

**Files:** baru `src/FlutterBar.Core/Processes/FlutterCommands.cs`, `src/FlutterBar.Core/Processes/TerminalLauncher.cs`,
`src/FlutterBar.App/Flyouts/FlutterFlyout.xaml(.cs)` + test; ubah `TaskbarWidget.xaml`, `WidgetViewModel.cs`,
`AppController*.cs`, `WidgetContextMenu.xaml(.cs)`, spec, checklist.

- [ ] Perintah lewat `FlutterToolLocator` (FVM otomatis) + `IProcessRunner`, timeout + cancel; output ke `LogBuffer`
  (separator + `LogKind.Tool`); jendela log dibuka untuk doctor atau bila gagal.
- [ ] Clean nonaktif saat Starting/Running/Reloading; pub get nonaktif saat Starting; ▶ Run diblok selama perintah
  berjalan; satu perintah sekaligus.
- [ ] Open terminal: `wt.exe -d <root>`, fallback `powershell.exe -NoExit` (working dir = root), PATH segar dari registry.
- [ ] Tombol logo Flutter (vector `Path` monokrom) di Full dan Compact; cek ulang lebar dan fallback ukuran.

### Batch 5: Mirror layar HP via scrcpy (temuan 5)

**Files:** baru `src/FlutterBar.Core/Devices/ScrcpyLauncher.cs` + test; ubah `TaskbarWidget.xaml`, `WidgetViewModel.cs`,
`AppController*.cs`, spec, checklist.

- [ ] `scrcpy -s <id> --window-title <nama>` dengan env `ADB=<AdbLocator>`, diluncurkan terlepas (bukan job
  kill-on-close); jendela yang sudah ada difokuskan.
- [ ] Tombol hanya untuk device `Platform` diawali `android` dan `!IsEmulator`; tanpa scrcpy → toast berisi
  `winget install Genymobile.scrcpy`.

### Batch 6: Widget di monitor kedua (temuan 6)

- [ ] 6a Spike: log diagnostik `TaskbarProbe` untuk `Shell_SecondaryTrayWnd`, diuji user di dua monitor; catat di
  `docs/superpowers/spikes/`.
- [ ] 6b (rencana ulang setelah spike): docking dengan taskbar target, fallback tanpa `TrayNotifyWnd`, setting
  `widget.monitor`, menu *Show on ▸*, kembali ke monitor utama bila monitor dicabut.
