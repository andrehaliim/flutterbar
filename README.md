# FlutterBar

FlutterBar is a Windows taskbar widget (C#/.NET 10, WPF) that replaces Android Studio's run toolbar for Flutter projects: pick a device, run, hot reload, and stop without opening the IDE.

## Build dan test

Butuh .NET SDK 10 dan Windows 10 atau 11.

```
dotnet build FlutterBar.sln
dotnet test
```

Release:

```
dotnet build FlutterBar.sln -c Release
```

| Exe | Lokasi (Release) |
|---|---|
| Widget | `src\FlutterBar.App\bin\Release\net10.0-windows\FlutterBar.App.exe` |
| CLI | `src\FlutterBar.Cli\bin\Release\net10.0-windows\flutterbar.exe` (salinan juga ada di folder Release `FlutterBar.App`) |

Untuk Debug, ganti `Release` dengan `Debug`. Hanya satu instance FlutterBar yang boleh jalan; menjalankannya lagi hanya
mengedipkan widget yang sudah ada. Tutup lewat klik kanan widget > "Exit". Log diagnostik: `%APPDATA%\FlutterBar\diag.log`.

## Pertama kali dipakai

1. Jalankan `FlutterBar.App.exe`; widget menempel di taskbar, di kiri area tray/jam. Kalau posisi otomatis tidak cocok
   (mis. ada widget taskbar pihak ketiga seperti TrafficMonitor, yang tidak terlihat oleh deteksi otomatis), klik kanan > "Drag widget".
2. Beri tahu FlutterBar di mana project Flutter Anda: klik kanan > Manage projects > "Root folders…" > tambah folder
   (mis. `C:\Work`), atau "Add project…" untuk satu folder. Scan sedalam 3 tingkat mencari `pubspec.yaml` Flutter.
   Folder root juga bisa ditulis langsung di `%APPDATA%\FlutterBar\settings.json`: `"scanRoots": ["C:\\Work"]`.
3. Pilih project, branch, config, dan device lewat chip di widget, lalu tekan ▶.
4. Project belum punya `.flutterbar/launch.json`? Di flyout config pilih "Import from IDE" (membaca run configuration Android Studio
   `.idea/workspace.xml`, `.run/*.xml`, dan `.vscode/launch.json`) atau "Create default".
5. Opsional: klik kanan > "Add CLI to PATH…" agar `flutterbar` bisa dipanggil dari terminal baru, dan "Start with Windows".

Project yang memakai fvm (`.fvmrc` atau `.fvm/fvm_config.json`) selalu dijalankan lewat `fvm flutter`, tanpa fallback ke flutter global.

## Menu widget

Klik kanan widget:

- **Size**: Full (semua chip), Compact (satu chip `Configuration ▾` yang membuka flyout gabungan), atau Buttons only
  (klik titik status untuk flyout gabungan). Ukuran yang tidak muat di area kosong taskbar dinonaktifkan dengan catatan;
  kalau taskbar penuh, widget turun ukuran sendiri dan kembali ke ukuran pilihan saat ruang cukup lagi.
- **Style**: slider opacity latar widget 0–100% (chip, ikon, dan teks tetap penuh) dan Theme System / Light / Dark.
  System mengikuti tema taskbar Windows; Light/Dark mengunci semua jendela FlutterBar.
- **Show on**: pindah ke taskbar monitor lain (butuh "Show my taskbar on all displays"). Satu widget saja, tidak
  diduplikasi. Bila monitor itu dicabut, widget pindah ke taskbar utama dan kembali saat monitor dicolok lagi.
- **Drag widget** / **Reset to auto position**: geser widget secara horizontal; posisinya disimpan.
- **Manage projects**, **Import config from IDE…**, **Open launch.json**, **Open log**, **Flutter tools…**, **Open terminal**,
  **Add CLI to PATH…**, **Start with Windows**, **Exit**.

Tombol di widget:

- **Logo Flutter** (Full/Compact; di Buttons only lewat menu > Flutter tools…): Pub get, Clean, Clean and pub get,
  Run build_runner (hanya bila `build_runner` ada di pubspec), dan Doctor. Satu perintah sekaligus, bisa di-Cancel, output masuk
  ke log. Clean nonaktif selama app berjalan; ▶ menunggu sampai perintah selesai.
- **Tombol log** (paling kanan): menu Open log dan Open terminal (Windows Terminal bila ada, selain itu PowerShell,
  di folder project).

## Aksi git (flyout branch)

Klik chip branch: `Fetch` (`git fetch --prune`), `Pull` (`git pull` biasa), kolom `Commit message` + `Commit and push`, dan daftar branch.

- `Commit and push` menjalankan `git add -A` untuk SELURUH repo (bukan hanya folder project; di monorepo perubahan di luar project ikut ter-commit), `git commit`, lalu `git push` (`push -u origin <branch>` bila belum ada upstream).
- Pesan kosong dan ada commit yang belum terkirim (`↑N`): tombol menjadi `Push ↑N` dan hanya mengirim commit itu.
- Tidak pernah `--force`, tidak ada pull otomatis sebelum push; push ditolak = saran "Pull first.". Merge/rebase yang belum selesai dan HEAD detached ditolak.
- Login remote HTTPS lewat Git Credential Manager (tidak pernah menunggu input terminal; batas 2 menit). Remote SSH yang butuh passphrase atau host baru harus disiapkan dulu dari terminal (ssh-agent / sekali `ssh`).
- Hasil tampil di dalam flyout selama terbuka (toast hanya bila flyout sudah tertutup); output git panjang dipotong ~6 baris, teks lengkap di `diag.log`.

## `launch.json`

`<project>\.flutterbar\launch.json`, format JSONC (komentar dan trailing comma boleh), dibaca ulang dari disk setiap kali dipakai.

```jsonc
{
  "version": 1,
  "configurations": [
    {
      "name": "beta",                 // wajib, unik per file
      "program": "lib/main.dart",     // opsional, default "lib/main.dart", relatif ke root project
      "flavor": "beta",               // opsional, menjadi --flavor beta
      "mode": "debug",                // opsional: debug | profile | release, default debug
      "args": [                       // opsional, satu elemen = satu argumen
        "--dart-define=mode=debug",
        "--dart-define=API_SERVER=https://beta.api.example.com/",
        "--dart-define=APP_NAME=GTS Mobile Beta"
      ]
    }
  ]
}
```

Perintah yang dijalankan (working directory = root project):
`<flutter> run --machine -d <deviceId> -t <program> [--flavor <flavor>] --<mode> <args...>`

## CLI

FlutterBar harus sedang berjalan; CLI bicara lewat named pipe milik user saat ini.

```
flutterbar status
flutterbar reload
flutterbar restart
flutterbar stop
flutterbar logs [--errors] [--tail N] [--since-last-reload]
```

Semua perintah mendukung `--json`. `flutterbar run` sengaja tidak ada: jalankan app lewat tombol ▶.

| Exit code | Arti |
|---|---|
| 0 | OK |
| 1 | Gagal (mis. reload gagal compile, "Reload in progress", sesi Failed) |
| 2 | FlutterBar tidak berjalan |
| 3 | Tidak ada sesi aktif |
| 4 | Timeout (reload/restart lebih dari 60 detik) |
| 64 | Pemakaian salah (perintah/argumen tidak dikenal) |

## Test

`dotnet test` menjalankan unit test saja (integration test otomatis di-skip).

Integration test end-to-end menjalankan `fvm flutter run --machine` sungguhan terhadap `tests\sample_app` (Flutter 3.22.3 lewat `.fvmrc`):
start sampai Running, reload, restart, logs, stop, lalu memastikan semua proses turunan mati. Butuh fvm di PATH dan Flutter 3.22.3 terpasang.

```
$env:FLUTTERBAR_IT_DEVICE="chrome"; dotnet test --filter Category=Integration
```

Hasil terakhir (2026-11-07): 2/2 lulus di device `chrome` (build pertama bisa beberapa menit; timeout 10 menit). Di web, hot reload
berperilaku seperti hot restart dan menjawab code 0. Android belum tercakup: AVD `Pixel_9` di mesin pengembang gagal install
(`INSTALL_FAILED_INSUFFICIENT_STORAGE`) dan device `windows` butuh toolchain Visual Studio C++.

Uji yang butuh mouse/taskbar/device nyata ada di `docs\manual-test-checklist.md`. Semua baris sudah diuji user dan lulus
(2026-11-11, sebagian di Windows 10 dan sebagian di Windows 11). Baris baru diisi "BELUM DIUJI (user)" sampai diuji.

## Penggunaan RAM

Build **Release**, `FlutterBar.App.exe` 5 menit idle setelah start (tidak ada sesi, LogWindow tertutup), DPI 96.

| Metrik | Windows 11 (2026-11-07, 1920x1080) | Windows 10 22H2 (2026-11-12, 1600x900) | Arti |
|---|---|---|---|
| **Private working set** | **35,8 MB** | **30,1 MB** | RAM fisik milik proses saja; angka kolom "Memory" di Task Manager. **Patokan target** |
| Working set | 121,9 MB | 105,4 MB | Seluruh RAM fisik yang dipetakan ke proses, termasuk halaman DLL .NET/WPF yang dibagi dengan proses lain |
| Private bytes | 61,2 MB | 39,7 MB | Memori commit privat (termasuk yang sedang di pagefile) |

Target spec §1 (private working set idle di bawah 100 MB, patokan diputuskan user 2026-11-12) **tercapai** di Windows 10 dan 11.
Working set tetap dicatat sebagai pembanding; sebagian besar isinya halaman DLL bersama yang tidak bisa diturunkan oleh kode FlutterBar.

Dengan LogWindow terbuka berisi 20.000 baris pada build Release: diuji user 2026-11-11 (OKE), angkanya tidak dicatat. Rujukan build
Debug (Task 15): widget saja sekitar 117 MB, dengan 20.000 baris dan LogWindow sekitar 167-178 MB working set.

## Keterbatasan

- Argumen di `launch.json` yang berisi tanda kutip (`"`) ditolak (tidak ada cara escaping yang selamat dari cmd.exe sekaligus `flutter.bat`).
  Pakai `--dart-define-from-file` untuk nilai kompleks.
- Penempatan otomatis tidak melihat widget taskbar pihak ketiga (mis. TrafficMonitor); pakai "Drag widget".
- Widget tersembunyi selama aplikasi fullscreen (game, video) berjalan.
- Windows 10 dan 11 memakai deteksi taskbar yang sama (UI Automation, dengan `TrayNotifyWnd` sebagai cadangan); tidak ada jalur
  khusus Windows 10 (`MSTaskSwWClass`).
- Saat taskbar auto-hide, widget ikut sembunyi/muncul sedikit telat dibanding animasi taskbar.
- Fixture protokol daemon direkam di `chrome`; output khusus Android (Gradle, `app.log`, semantik hot reload) belum direkam.
  Deteksi exception Android (prefix logcat `I/flutter ( 1234): `) diuji dengan baris sintetis, belum dengan rekaman asli.
- Stop yang bersih (flutter keluar dalam 5 detik setelah `app.stop`) membiarkan daemon yang dinyalakan sesi itu (Gradle, Kotlin,
  adb) tetap hidup sampai FlutterBar ditutup, supaya ▶ berikutnya tidak membayar Gradle dingin. Exit atau crash FlutterBar tetap
  mematikan semuanya (Job Object).
- Deteksi git/adb hanya saat FlutterBar start; git yang baru dipasang butuh restart FlutterBar.
