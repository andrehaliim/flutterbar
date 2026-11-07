# FlutterBar — Desain

- **Tanggal:** 2026-11-06
- **Status:** Disetujui di brainstorming, menunggu review spec
- **Nama:** FlutterBar (nama kerja)

## 1. Latar belakang & tujuan

Developer Flutter yang banyak menulis kode lewat Claude Code membuka Android Studio hampir hanya untuk
**run toolbar** (pilih project, branch, entry file, device, run configuration, lalu Run, Hot reload,
Hot restart, Stop). Android Studio memakan RAM 2–4 GB untuk itu.

FlutterBar adalah widget kecil yang **menempel di taskbar Windows** dan menggantikan run toolbar
tersebut, sehingga IDE tidak perlu dibuka untuk siklus run → reload → stop sehari-hari. Bentuknya
meniru konsep [now-playing-taskbar-widget](https://github.com/mechanicwb2-hub/now-playing-taskbar-widget):
jendela tanpa border yang selalu terlihat dan menempel di area kosong taskbar (bukan tray icon, bukan
deskband).

**Kriteria sukses:**
1. Bisa memilih project, branch, config, dan device, lalu Run, Hot reload, Hot restart, dan Stop
   tanpa membuka IDE.
2. Log app bisa dibaca di jendela log tersendiri.
3. Claude Code bisa melakukan hot reload/restart dan membaca error lewat CLI `flutterbar`.
4. Working set FlutterBar saat idle di bawah 100 MB (di luar proses flutter/dart/gradle).

### Di luar cakupan (non-goals)
- Editor kode, debugger dengan breakpoint, profiler, dan layout inspector. Untuk itu buka IDE.
- Tombol Debug. Hanya ada Run, dan mode diatur lewat `mode` di config.
- Lebih dari satu sesi run sekaligus.
- Operasi git selain melihat dan pindah branch lokal (tidak ada create, pull, push, atau branch remote).
- Integrasi DTD/Dart MCP. Ini dicatat sebagai eksperimen terpisah (lihat §12).
- macOS dan Linux. Hanya Windows 10 dan 11.

## 2. Keputusan utama

| Topik | Keputusan |
|---|---|
| Bentuk | Widget yang menempel di taskbar, dengan perilaku seperti project referensi |
| Layout | Adaptif: Lengkap, Ringkas, atau Tombol saja. User bisa memilih atau resize, dan widget menyusut otomatis kalau ruang kurang |
| Tombol | Idle: ▶ Run. Running: ⚡ Hot reload, 🔄 Hot restart, ⏹ Stop |
| Log | `LogWindow` terpisah yang dibuka dari widget |
| Run config | Milik FlutterBar sendiri, di `<project>/.flutterbar/launch.json` dengan format mirip `launch.json` VS Code. Bisa import sekali dari `.idea` dan `.vscode` |
| Daftar project | Scan otomatis dari folder root, ditambah add/hide manual, diurutkan dari yang terakhir dipakai |
| Ganti branch | `git switch` biasa, dan error dari git ditampilkan apa adanya. Hanya branch lokal |
| Device | Device terhubung ditambah emulator offline yang bisa dinyalakan dari dropdown |
| Jumlah sesi | Satu sesi aktif |
| Integrasi Claude Code | CLI `flutterbar` (reload, restart, stop, status, logs) lewat named pipe |
| Stack | C# + WPF, **.NET 10 (LTS)** |

> **Catatan versi .NET:** project referensi memakai .NET 8, tapi dukungan .NET 8 dan .NET 9 berakhir
> pada 10 November 2026. Karena itu FlutterBar memakai .NET 10 LTS, yang didukung sampai November 2028.

## 3. Arsitektur

### 3.1 Struktur solution

```
FlutterBar.sln
src/
  FlutterBar.Core/        class library, tanpa UI: semua logika, bisa di-unit-test
  FlutterBar.App/         WPF (WinExe): widget, flyout, LogWindow, menu
  FlutterBar.Cli/         console exe, nama assembly `flutterbar`
tests/
  FlutterBar.Core.Tests/  xUnit
  fixtures/               contoh launch.json, workspace.xml, rekaman output `flutter run --machine`
docs/
```

> **Revisi dari diskusi:** awalnya direncanakan "satu exe untuk widget dan CLI". Ini diubah menjadi
> **dua exe**, karena exe WPF (subsystem GUI) tidak bisa menulis ke console terminal dengan andal, dan
> shell juga tidak menunggu exe GUI selesai. Akibatnya exit code dan output CLI tidak bisa dipakai oleh
> Claude Code. Jadi `FlutterBar.App.exe` adalah widget, dan `flutterbar.exe` adalah CLI. Keduanya
> berbagi kontrak IPC dari `FlutterBar.Core`.

### 3.2 Komponen `FlutterBar.Core`

| Komponen | Tanggung jawab | Bergantung pada |
|---|---|---|
| `AppSettingsStore` | Baca/tulis `%APPDATA%\FlutterBar\settings.json` | — |
| `ProjectCatalog` | Scan folder root, add/hide manual, urutan terakhir dipakai | `AppSettingsStore` |
| `LaunchConfigStore` | Baca, validasi, dan tulis `<project>/.flutterbar/launch.json` | — |
| `IdeConfigImporter` | Konversi `.idea/workspace.xml`, `.run/*.xml`, dan `.vscode/launch.json` ke `LaunchConfig` | — |
| `FlutterToolLocator` | Menentukan perintah flutter per project (`fvm flutter` atau `flutter`) | — |
| `ProcessRunner` | Menjalankan proses (termasuk `.bat`/`.cmd`) dengan argumen aman, plus Job Object | — |
| `DeviceService` | Daftar device (`flutter devices --machine`), emulator (`flutter emulators`), dan menyalakan emulator | `FlutterToolLocator`, `ProcessRunner` |
| `GitService` | Branch lokal, branch aktif (dipantau), `git switch`, cek perubahan `pubspec` | `ProcessRunner` |
| `FlutterDaemonClient` | Parse event dan kirim request protokol `flutter run --machine` | — |
| `RunSession` | State machine sesi, buffer log, deteksi error | `FlutterDaemonClient`, `ProcessRunner` |
| `LogBuffer` | Ring buffer 20.000 baris dengan penanda reload | — |
| `IpcServer` / `IpcClient` | Named pipe dan kontrak pesan CLI | `RunSession` (server) |

### 3.3 Komponen `FlutterBar.App`
- `TaskbarWidget`: jendela utama, docking, layout adaptif, dan menu klik kanan.
- `TaskbarDocking`: menghitung area kosong taskbar, topmost, auto-hide, fullscreen, dan DPI (Win32 interop).
- `Flyouts`: Project, Branch, Config, Device, dan flyout gabungan untuk mode ringkas/tombol.
- `LogWindow`: tampilan log.
- `Toast`: notifikasi kecil di atas widget.

### 3.4 Alur data
```
Widget/Flyout ──pilih──► AppSettingsStore (project, config, device terakhir)
▶ Run ──► RunSession.Start(project, config, device)
           └─► ProcessRunner: [fvm] flutter run --machine ...
                 stdout ──► FlutterDaemonClient ──event──► RunSession ──► state + LogBuffer
                                                                 │
                      Widget (tombol, titik status) ◄────────────┤
                      LogWindow ◄────────────────────────────────┤
                      IpcServer ◄── flutterbar.exe (CLI) ────────┘
⚡/🔄/⏹ (widget maupun CLI) ──► RunSession ──► request JSON ke stdin flutter
```

## 4. Konfigurasi

### 4.1 `<project>/.flutterbar/launch.json`

Format JSONC: komentar dan trailing comma diperbolehkan, sama seperti VS Code.

```jsonc
{
  "version": 1,
  "configurations": [
    {
      "name": "beta",                 // wajib, unik per file
      "program": "lib/main.dart",     // opsional, default "lib/main.dart", relatif ke root project
      "flavor": "beta",               // opsional, menjadi --flavor beta
      "mode": "debug",                // opsional: debug | profile | release, default debug
      "args": [                       // opsional, diteruskan apa adanya, satu elemen = satu argumen
        "--dart-define=mode=debug",
        "--dart-define=API_SERVER=https://beta.api.example.com/",
        "--dart-define=APP_NAME=GTS Mobile Beta"
      ]
    }
  ]
}
```

**Validasi:**
- `version` harus `1`.
- `configurations` minimal berisi satu item.
- `name` wajib ada dan unik.
- `mode` hanya boleh `debug`, `profile`, atau `release`.
- File di `program` harus ada.

Kalau validasi gagal, hasilnya adalah error yang menyebutkan nomor baris (kalau ada), field, dan pesan
dalam bahasa manusia.

**Penyusunan perintah** (working directory = root project):
```
<flutter> run --machine -d <deviceId> -t <program> [--flavor <flavor>] --<mode> <args...>
```

### 4.2 Import dari IDE (`IdeConfigImporter`)

| Sumber | Pemetaan |
|---|---|
| `.idea/workspace.xml` dan `.run/*.xml`: `<configuration type="FlutterRunConfigurationType">` | `name` → `name`. `filePath` (buang `$PROJECT_DIR$/`) → `program`. `buildFlavor` → `flavor`. `additionalArgs` di-*shell-split* (mendukung tanda kutip dan `&quot;`) → `args` |
| `.vscode/launch.json`: item dengan `type: "dart"` | `name` → `name`. `program` → `program`. `flutterMode` → `mode`. `args`: pasangan `--flavor X` atau `--flavor=X` diangkat ke `flavor`, sisanya masuk ke `args` |

- Import dipicu lewat menu klik kanan, atau tombol "Import dari IDE" saat `launch.json` belum ada.
- Sebelum disimpan, tampil daftar config yang ditemukan dari semua sumber sebagai checklist. Item yang
  namanya sudah ada di `launch.json` ditandai dan tidak dicentang secara default.
- Item yang dipilih **ditambahkan** ke `launch.json`. Nama yang bentrok antar sumber diberi akhiran,
  misalnya `beta (vscode)`.
- File IDE tidak pernah ditulis.

### 4.3 `%APPDATA%\FlutterBar\settings.json`

```jsonc
{
  "version": 1,
  "scanRoots": ["C:\\Work"],
  "manualProjects": [],              // path project di luar folder root
  "hiddenProjects": [],              // path yang disembunyikan dari daftar
  "projects": {                      // state per project, key = path absolut
    "C:\\Work\\gts-mobile": { "lastUsed": "2026-11-06T10:42:00+07:00", "config": "beta", "deviceId": "chrome" }
  },
  "currentProject": "C:\\Work\\gts-mobile",
  "widget": { "size": "full", "position": "auto", "offsetFromRight": null, "widthOverride": null },
  "startWithWindows": false
}
```

Penulisan bersifat atomik: tulis ke file sementara, lalu rename. Kalau file rusak, file lama
di-rename menjadi `settings.json.bak-<timestamp>`, FlutterBar memakai default, dan memberi toast.

## 5. Project, branch, device

### 5.1 `ProjectCatalog`
- **Scan:** setiap folder root ditelusuri sampai kedalaman 3. Folder `.git`, `.dart_tool`, `build`,
  `node_modules`, `android`, `ios`, `windows`, `linux`, `macos`, `web`, dan folder yang diawali `.`
  dilewati. Sebuah folder dianggap project kalau punya `pubspec.yaml` yang mencantumkan dependency
  `flutter: sdk: flutter`.
- **Kapan scan berjalan:** saat FlutterBar start, saat menu "Rescan" dipilih, dan saat flyout project
  dibuka kalau scan terakhir sudah lebih dari 10 menit lalu (hasil cache tampil dulu).
- **Daftar akhir:** (hasil scan + `manualProjects`) − `hiddenProjects`, diurutkan menurut `lastUsed`
  terbaru lalu nama.
- **Project yang hilang** (foldernya sudah tidak ada) ditandai "tidak tersedia" sampai scan berikutnya,
  lalu dibuang dari daftar.
- Nama yang ditampilkan adalah nama folder. Kalau ada dua project dengan nama folder yang sama, ditambah
  nama parent-nya, misalnya `gts-mobile (GTSmobile)`.

### 5.2 `FlutterToolLocator`
- Kalau project punya `.fvmrc` atau `.fvm/fvm_config.json`, perintahnya `fvm flutter`. `fvm` harus
  ditemukan di PATH. Kalau tidak ditemukan, muncul error dan tidak ada fallback ke `flutter` global.
- Kalau tidak, perintahnya `flutter` dari PATH.
- Hasil lookup PATH di-cache per sesi FlutterBar, dan cache dibersihkan setiap kali menu "Rescan" dipilih.

### 5.3 `ProcessRunner`
- Argumen selalu diberikan sebagai list, bukan string yang dirangkai.
- `flutter` di Windows adalah `flutter.bat`. Untuk `.bat`/`.cmd`, proses dijalankan lewat
  `cmd.exe /d /s /c` dengan escaping khusus cmd (`"`, `^`, `&`, `|`, `<`, `>`, `%`). Escaping ini
  **wajib** punya unit test dengan argumen yang berisi spasi, kutip, `&`, dan `%`.
- Setiap proses sesi run dimasukkan ke Job Object dengan `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. Jadi
  kalau FlutterBar ditutup atau crash, seluruh pohon proses (cmd, dart, gradle, adb yang di-spawn) ikut
  mati.
- Proses daemon adb global yang sudah jalan sebelumnya tidak ikut dimatikan karena memang bukan anak
  Job Object.

### 5.4 `DeviceService`
- **Tidak ada polling berkala.** Daftar device di-refresh saat flyout device dibuka dan sesaat sebelum
  Run. Cache terakhir langsung ditampilkan sambil refresh berjalan di latar.
- **Device terhubung** diambil dari `flutter devices --machine` (id, nama, platform, emulator/fisik).
- **Emulator offline** diambil dari `flutter emulators`, dengan cache yang di-refresh setiap flyout
  device dibuka. Emulator yang sudah tampil sebagai device terhubung tidak ditampilkan lagi di bagian
  offline.
- **Menyalakan emulator:** jalankan `flutter emulators --launch <id>`, lalu polling
  `flutter devices --machine` setiap 5 detik, maksimal 120 detik, sampai muncul device emulator baru.
  Device itu lalu otomatis terpilih. Kalau timeout, muncul toast dan emulator dibiarkan menyala.
- Device terakhir per project disimpan. Kalau device itu tidak tersedia saat Run, flyout device dibuka
  dan run tidak dimulai.

### 5.5 `GitService`
- Folder dianggap repo git kalau `git rev-parse --is-inside-work-tree` berhasil. Kalau bukan repo, chip
  branch disembunyikan.
- Daftar branch diambil dengan `git branch --format=%(refname:short)`. Branch aktif diambil dengan
  `git rev-parse --abbrev-ref HEAD`. Kalau HEAD detached, yang ditampilkan SHA pendek.
- **Memantau branch aktif:** `FileSystemWatcher` pada file HEAD dari `git rev-parse --git-path HEAD`,
  supaya worktree juga didukung. Perubahan dari luar, misalnya Claude Code atau terminal, langsung
  terlihat.
- **Pindah branch:** `git switch <branch>`. Kalau gagal, stderr git ditampilkan apa adanya di toast dan
  log.
- **Pindah branch saat sesi berjalan:** `git diff --name-only <old> <new> -- pubspec.yaml pubspec.lock`
  dijalankan. Kalau ada perubahan, muncul toast "pubspec berubah, stop lalu run ulang". Kalau tidak,
  toast "Branch berganti, hot restart?" dengan tombol Hot restart.

## 6. Sesi run

### 6.1 Protokol `flutter run --machine`
- stdout berisi baris JSON berbentuk `[{...}]`. Baris yang tidak bisa di-parse sebagai JSON diperlakukan
  sebagai log biasa.
- **Event yang dipakai:** `daemon.connected`, `app.start` (`appId`, `supportsRestart`), `app.debugPort`,
  `app.started`, `app.log` (`log`, `error`), `app.progress` (`message`, `finished`), `app.stop`, dan
  `daemon.logMessage` (`level`, `message`).
- **Request** ditulis ke stdin dengan format `[{"id":N,"method":...,"params":...}]` lalu newline:
  - Hot reload: `app.restart` `{appId, fullRestart:false, pause:false, reason:"manual"}`
  - Hot restart: `app.restart` `{appId, fullRestart:true, pause:false, reason:"manual"}`
  - Stop: `app.stop` `{appId}`
- Response yang ditunggu: `{"id":N,"result":{...}}` atau `{"id":N,"error":...}`. Kalau `result.code != 0`,
  hasilnya dianggap gagal dan `result.message` ditampilkan.
- Format pasti setiap field diverifikasi terhadap rekaman output asli dari Flutter 3.22.3, yang dipakai
  sebagai fixture test (lihat §11).

### 6.2 State machine

```
Idle ──Run──► Starting ──app.started──► Running ◄──┐
  ▲              │                        │  │      │ hasil reload/restart diterima
  │              │ proses exit / gagal    │  └─► Reloading
  │              ▼                        │
  └──────────── Failed ◄──app.stop+error──┘
  ▲                                       │ Stop
  └────────────── Stopping ◄──────────────┘
```

| State | Titik status | Tombol |
|---|---|---|
| Idle | abu-abu | ▶ (aktif kalau config dan device sudah dipilih) |
| Starting | kuning berkedip, progress tampil di tooltip atau di layout lengkap | ⏳ ⏹ (⏹ membatalkan build) |
| Running | hijau | ⚡ 🔄 ⏹ |
| Reloading | hijau, tombol yang ditekan berubah jadi spinner | ⚡ 🔄 ⏹ (request kedua ditolak selama masih reloading) |
| Stopping | abu-abu berkedip | — |
| Failed | merah, dengan label "Build gagal" atau "App berhenti" | ▶ |

- **Flag error** (titik merah) terpisah dari state. Flag ini menyala saat `app.log` dengan
  `error:true`, saat ada baris `════════ Exception caught by`, saat `daemon.logMessage` dengan
  level error, atau saat reload/restart gagal. Flag padam saat reload/restart berikutnya berhasil, atau
  saat sesi baru dimulai.
- **Mode `profile` atau `release`:** ⚡ dan 🔄 dinonaktifkan, dengan tooltip "Tidak didukung di mode
  release/profile".
- **`supportsRestart:false`:** 🔄 dinonaktifkan. Kalau hot reload ditolak oleh flutter (misalnya web di
  Flutter versi lama), ⚡ dinonaktifkan untuk sisa sesi dan pesan dari flutter ditampilkan.
- **Stop:** kirim `app.stop`. Kalau proses belum keluar dalam 5 detik, Job Object ditutup sehingga
  semua proses mati paksa.
- **Satu sesi:** mengganti project, config, atau device saat sesi berjalan memunculkan konfirmasi
  "Stop sesi sekarang dan ganti?". Kalau Ya, sesi dihentikan dan pilihan berubah, tapi app tidak
  otomatis di-run.
- **Menutup FlutterBar (Exit):** kalau ada sesi berjalan, muncul konfirmasi, lalu Stop, lalu keluar.

### 6.3 `LogBuffer`
- Ring buffer 20.000 baris. Setiap baris berisi `{seq, timestamp, kind, text}`, dengan `kind` salah satu
  dari `tool`, `app`, `error`, `reload`, atau `separator`.
- Separator ditambahkan saat sesi dimulai dan saat setiap reload/restart (`── Hot reload 10:42:15 ──`).
- Disediakan query `since(seq)`, `sinceLastReload()`, `errorsOnly`, dan `tail(n)`, yang dipakai bersama
  oleh `LogWindow` dan CLI.

## 7. Widget taskbar

### 7.1 Layout
- **Lengkap:** `● [project▾] [⎇ branch▾] [config▾] [device▾] | tombol | 🖥 log`
- **Ringkas:** `● project · config ▾ | tombol | 🖥 log`. Klik bagian nama untuk membuka flyout gabungan
  yang berisi keempat dropdown.
- **Tombol saja:** `● tombol`. Klik titik status untuk membuka flyout gabungan. Kalau titiknya merah,
  klik membuka `LogWindow` di posisi error.
- Hover chip config menampilkan tooltip berisi `program`, `flavor`, dan `mode`.
- Flyout project dan branch punya kolom pencarian.
- Flyout device dibagi dua bagian: "Terhubung" dan "Emulator (offline)".

### 7.2 Aturan adaptif
- Ukuran pilihan user (`widget.size`) bisa diatur lewat menu Size, atau dengan drag tepi kiri widget
  (lebar disimpan di `widthOverride`).
- Kalau area kosong taskbar lebih kecil dari lebar ukuran pilihan, widget turun satu tingkat, lalu
  dicek lagi. Kalau "tombol saja" pun tidak muat (mode `auto`), widget TIDAK disembunyikan: tetap tampil di posisi
  valid terakhir (atau di kiri `TrayNotifyWnd` bila belum ada) dalam ukuran "tombol saja", dengan pesan
  "Taskbar penuh — gunakan Drag widget". Widget hanya disembunyikan untuk fullscreen dan auto-hide. Saat ruang
  cukup lagi, widget kembali ke ukuran pilihan.
- Lebar dihitung ulang saat taskbar berubah (tombol aplikasi bertambah atau berkurang, DPI berubah,
  monitor berubah).

### 7.3 Docking (`TaskbarDocking`)
- Jendela tanpa border, `WS_EX_TOOLWINDOW` (tidak muncul di taskbar atau Alt+Tab), dan topmost.
- **Posisi `auto`:** tepat di kiri area tray/jam, dengan menghitung area kosong di antara tombol
  aplikasi dan tray:
  - Windows 11: batas tombol diambil lewat UI Automation dari elemen taskbar.
  - Windows 10: diambil dari `MSTaskSwWClass` / `TrayNotifyWnd`.
- Mendukung taskbar rata tengah maupun rata kiri.
- **Posisi `manual`:** deteksi `auto` tidak bisa melihat widget overlay pihak ketiga (mis. TrafficMonitor) yang tidak
  muncul di taskbar UI Automation, sehingga ada mode manual. Run pertama = `auto`. Dengan "Drag widget" dicentang,
  user menggeser widget secara horizontal (Y tetap menempel taskbar); saat dicentang lagi/dimatikan, posisi terkunci dan
  disimpan sebagai `widget.offsetFromRight` = jarak dari tepi KANAN taskbar dalam piksel fisik (tahan terhadap
  perubahan tombol aplikasi/rata taskbar; di-clamp ke taskbar bila resolusi berubah). Mode manual TIDAK menghindari
  tombol aplikasi; owner, topmost, fullscreen, dan auto-hide tetap berlaku. "Reset to auto position" kembali ke `auto`.
- **Stabilitas (v3.1):** elemen UIA dihitung terisi bila: (a) rect-nya di dalam rect taskbar (toleransi 2 px), (b) tinggi >= 50% tinggi taskbar, (c) lebar >= 8 px,
  (d) bukan milik proses sendiri atau proses widget lain milik kita (`DockingSpike`, `FlutterBar.App`), dan (e) bukan transien (ControlType `ToolTip`,
  kelas popup/`TaskListThumbnailWnd`). Thumbnail/flyout digambar DI ATAS taskbar sehingga (a) sudah mengecualikannya. Catatan: TrafficMonitor tidak ada
  di pohon UIA `Shell_TrayWnd`, jadi tidak pernah terhitung (itu sebabnya ada mode manual). Penempatan baru (pindah/ubah ukuran) baru diterapkan bila hasil
  (ukuran, x) yang sama (toleransi x 3 px) keluar dari 2 query UIA berturut-turut (sekitar 1-2 detik); kandidat berbeda yang
  ditahan >= 5 query berturut-turut (walau berganti-ganti) diterapkan (max-hold). Layout pertama setelah
  startup, setelah "Reset to auto position", dan setelah `TaskbarCreated` diterapkan langsung.
- Hasil layout UIA tanpa elemen tray atau tanpa kotak terisi dianggap tidak valid (layout terakhir yang baik
  dipertahankan); `TrayNotifyWnd` dipakai sebagai cadangan/cek kiri tray. Selama menu/tooltip/popup terbuka,
  reassert topmost ditangguhkan dan HWND popup di-topmost-kan.
- Topmost diterapkan ulang saat taskbar menjadi foreground, lewat WinEvent hook, supaya widget tidak
  tertutup taskbar Windows 11.
- **Auto-hide:** status dan posisi taskbar dipantau (`SHAppBarMessage`). Widget ikut sembunyi dan
  muncul bersama taskbar.
- **Fullscreen:** kalau jendela foreground menutupi seluruh monitor (atau dari
  `SHQueryUserNotificationState`), widget disembunyikan.
- **DPI:** per-monitor DPI aware (v2).
- **Tema:** mengikuti `SystemUsesLightTheme` di registry (tema taskbar), dan berganti otomatis saat
  tema berubah.
- Versi pertama hanya tampil di taskbar monitor utama.
- Project referensi boleh dipelajari sebagai rujukan pendekatan, tapi kodenya tidak disalin.

### 7.4 Menu klik kanan
- Size: Lengkap / Ringkas / Tombol saja
- Drag widget (checkbox) / Reset to auto position (kedua label ini sengaja berbahasa Inggris, keputusan user)
- Kelola project: Folder root…, Tambah project…, Project tersembunyi…, Rescan
- Import config dari IDE…
- Buka `launch.json`, yang membuka file dengan aplikasi default `.json`
- Buka `LogWindow`
- Tambahkan CLI ke PATH, yang menambah folder `flutterbar.exe` ke PATH user (`HKCU\Environment`) setelah
  konfirmasi
- Start with Windows (`HKCU\...\Run`, toggle)
- Exit

## 8. `LogWindow`
- Jendela WPF biasa (resize, minimize, muncul di taskbar selama terbuka). Dibuka lewat ikon log, titik
  merah, atau menu. **Menutupnya tidak menghentikan sesi.**
- Daftarnya virtualized dan mengambil data dari `LogBuffer`.
- **Warna per `kind`:** `tool` abu-abu, `app` normal, `reload` hijau, `error` merah.
- **Blok exception:** dari baris `════════ Exception caught by` sampai garis penutupnya dikelompokkan
  dan bisa dilipat.
- **Toolbar:** pencarian (filter teks), toggle "Errors only", lompat ke error sebelumnya/berikutnya,
  Clear (mengosongkan tampilan, buffer CLI tidak ikut kosong), dan Copy (pilihan atau semua).
- **Auto-scroll:** berhenti saat user scroll ke atas, dan aktif lagi saat kembali ke bawah.

## 9. CLI `flutterbar`

### 9.1 Perintah

| Perintah | Fungsi | Exit code |
|---|---|---|
| `flutterbar status` | State, project, branch, config, device, dan jumlah error | 0 ada sesi aktif, 3 tidak ada sesi |
| `flutterbar reload` | Hot reload, menunggu hasilnya (timeout 60 detik), dan mencetak error kalau gagal | 0 berhasil, 1 gagal, 3 tidak ada sesi, 4 timeout |
| `flutterbar restart` | Hot restart, sama seperti reload | sama dengan reload |
| `flutterbar stop` | Menghentikan sesi | 0, atau 3 kalau tidak ada sesi |
| `flutterbar logs [--errors] [--tail N] [--since-last-reload]` | Mencetak log dari buffer | 0 |

- Semua perintah mendukung `--json` untuk output terstruktur.
- Kalau FlutterBar tidak berjalan (pipe tidak ada), exit code 2 dengan pesan "FlutterBar tidak berjalan".
- Argumen yang tidak dikenal menghasilkan exit code 64 dan menampilkan usage.
- **Sengaja tidak ada `flutterbar run`.** Menjalankan app hanya lewat tombol ▶, supaya Claude tidak
  meluncurkan app sendiri.

### 9.2 IPC
- Nama pipe `\\.\pipe\FlutterBar-<SID user>`, dengan ACL hanya untuk user saat ini.
- Protokol: satu request JSON per koneksi, lalu satu response JSON:
  `{"command":"reload","args":{...}}` → `{"ok":true,"exitCode":0,"data":{...},"message":"..."}`.
- Server menangani request secara berurutan. Request reload/restart yang datang saat state Reloading
  langsung mendapat `exitCode 1` dengan pesan "Sedang reload".

## 10. Penanganan error

| Situasi | Perilaku |
|---|---|
| Ada `.fvmrc` tapi `fvm` tidak ditemukan | Run dibatalkan dengan toast dan log berisi pesan jelas. Tidak ada fallback |
| `flutter` atau `git` tidak ditemukan | Pesan yang jelas, dan fitur terkait dinonaktifkan |
| `launch.json` belum ada | Chip menampilkan "Belum ada config", dengan aksi Import dari IDE atau Buat default |
| `launch.json` tidak valid | Chip berwarna merah dan menampilkan error validasi (baris/field), dengan aksi Buka file |
| Folder project hilang | Ditandai tidak tersedia, lalu dibuang dari daftar pada scan berikutnya |
| `git switch` gagal | stderr git ditampilkan apa adanya |
| Device dicabut atau app crash | State Failed beserta pesannya |
| Emulator timeout | Toast, emulator dibiarkan menyala |
| Output flutter bukan JSON | Ditampilkan sebagai log `tool` |
| `settings.json` rusak | Backup file lama, pakai default, toast |
| Instance kedua `FlutterBar.App` dijalankan | Instance pertama dimunculkan (lewat named mutex dan pipe), lalu instance kedua keluar |
| Exception tak tertangani di FlutterBar | Ditulis ke `%APPDATA%\FlutterBar\diag.log`, ditampilkan toast kalau memungkinkan. Job Object memastikan proses flutter ikut mati kalau FlutterBar crash |

Tidak ada exception yang ditelan diam-diam. Setiap catch harus menampilkan pesan ke user dan/atau
menulis ke `diag.log`.

## 11. Testing

- **Unit test (`FlutterBar.Core.Tests`, xUnit):**
  - Parse dan validasi `launch.json`, termasuk JSONC dan field yang salah.
  - `IdeConfigImporter`, dengan fixture yang meniru `.idea/workspace.xml` dan `.vscode/launch.json`
    gts-mobile (termasuk `&quot;` dan argumen berspasi).
  - Penyusunan perintah: fvm atau non-fvm, flavor, mode, program.
  - Escaping `cmd.exe` untuk `.bat`: spasi, kutip, `&`, `%`, `^`.
  - `FlutterDaemonClient`, dengan rekaman output asli `flutter run --machine` dari Flutter 3.22.3.
    Rekamannya mencakup start sukses, reload sukses, reload gagal karena compile error, exception
    runtime, dan app.stop.
  - Transisi state machine `RunSession` dan flag error.
  - `LogBuffer`: ring buffer penuh, `sinceLastReload`, dan `errorsOnly`.
  - `ProjectCatalog`: scan di folder sementara (kedalaman, folder yang dilewati, pubspec non-Flutter,
    hidden, manual, dan nama yang bentrok).
  - Parsing output git.
  - Kontrak IPC: serialisasi request dan response, serta exit code.
- **Integration test (opt-in, kategori `Integration`, butuh Flutter):** sebuah sample app minimal
  dijalankan di device `windows`, lalu diuji Run, Hot reload, Hot restart, Stop, dan CLI
  `status`/`reload`/`logs`.
- **Checklist manual docking:** Windows 11 dengan taskbar rata tengah dan rata kiri, auto-hide,
  aplikasi fullscreen, DPI 100/125/150%, tema gelap/terang, taskbar penuh (menyusut atau sembunyi),
  monitor kedua dicolok dan dicabut.
- **Pengukuran RAM:** working set `FlutterBar.App` saat idle dan saat `LogWindow` terbuka berisi 20.000
  baris. Hasilnya dicatat apa adanya.

## 12. Prasyarat, risiko, dan tindak lanjut

**Prasyarat environment:** saat ini mesin belum punya .NET SDK (hanya runtime 6 dan 9). Untuk build
dibutuhkan **.NET 10 SDK**. Pemasangannya dilakukan oleh user, atau atas persetujuan user.

**Risiko utama:**
1. **Docking taskbar Windows 11** (UI Automation, z-order topmost). Ini risiko teknis terbesar, jadi
   rencana implementasi harus memulainya dengan spike docking sebelum fitur lain.
2. **Detail protokol `flutter run --machine`** di Flutter 3.22.3. Ini dimitigasi dengan merekam output
   asli lebih dulu dan menjadikannya fixture.
3. **Escaping argumen lewat `cmd.exe` untuk `flutter.bat` dan `fvm`.** Ini dimitigasi dengan unit test
   khusus.

**Tindak lanjut di luar versi ini:**
- Spike integrasi DTD supaya Dart MCP (`hot_reload`, `get_runtime_errors`) bisa menemukan sesi FlutterBar.
- Update aturan `flutter.md` §9: kalau tidak ada DTD, pakai `flutterbar status/reload/logs`.
- Widget di semua monitor, dan installer (winget/Inno Setup).
