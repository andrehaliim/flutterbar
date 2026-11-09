# Spike: taskbar monitor kedua (round 2, batch 6a)

**Tujuan:** sebelum widget bisa dipindah ke taskbar monitor lain (menu *Show on ▸ Monitor N*, plan putaran 2 batch 6b),
pastikan apa yang bisa dibaca FlutterBar dari `Shell_SecondaryTrayWnd` di Windows 11: rect dan monitor, DPI, anak Win32
(apakah ada `TrayNotifyWnd`), pohon UI Automation, dan apakah `TaskbarLayoutAnalyzer` menghasilkan layout yang valid
(tepi kiri "tray"/jam, kotak terisi tombol aplikasi).

## Cara menjalankan

1. Windows Settings → Personalization → Taskbar → Taskbar behaviors → **Show my taskbar on all displays** aktif.
2. Tutup FlutterBar (klik kanan → Exit), lalu jalankan dengan argumen survei:
   `start "" "src\FlutterBar.App\bin\Release\net10.0-windows\FlutterBar.App.exe" --survey-taskbars`
3. Tunggu ±10 detik. Survei berjalan sekali, 5 detik setelah start, read-only (docking tidak berubah).
4. Ambil baris `survey:` dari `%APPDATA%\FlutterBar\diag.log` (dari `survey: N taskbar(s)` sampai `survey: done`).
5. Ulangi bila perlu dengan variasi: taskbar sekunder dengan "Show taskbar apps on: taskbar where window is open",
   monitor kedua dengan DPI berbeda, jam di taskbar sekunder (Windows 11: "Show clock on all taskbars").

## Yang dicatat per taskbar

- `[primary|secondary 0x…] rect=… monitor=… dpi=… visible=…`
- `win32 child class='…'`: anak langsung (cek `TrayNotifyWnd`, `ReBarWindow32`, `MSTaskSwWClass`, dsb.)
- `uia dN COUNTED|TRAY|… : class id type proc rect`: setiap elemen UIA dengan verdict analyzer yang sama dengan taskbar utama
- `analysis valid=… reject=… trayLeft=… trayNotifyLeft=… occupied=… elements=…`

## Catatan pengambilan log

FlutterBar yang dijalankan dari panel Terminal Claude desktop menulis `diag.log` asli di `%APPDATA%\FlutterBar`. Shell
agen (sandbox paket MSIX Claude) melihat salinan virtual lama di
`%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\FlutterBar\diag.log` (berhenti 2026-11-08 00:58), jadi
baris `survey:` dibaca lewat panel Terminal. Ini bukan bug FlutterBar.

## Hasil (2026-11-09, Windows 11, 2 monitor, satu run)

| | Primary (`Shell_TrayWnd`) | Secondary (`Shell_SecondaryTrayWnd`) |
|---|---|---|
| Monitor | (0,0,1920,1080) landscape, utama | (-1080,0,0,1920) portrait, di kiri utama (koordinat negatif) |
| Rect taskbar | (0,1032,1920,1080) | (-1080,1872,0,1920), tinggi 48 sama |
| DPI | 96 | 96 (variasi DPI berbeda belum diuji) |
| Anak Win32 | DesktopWindowContentBridge, CoreWindow, Start, TrayDummySearchControl, **TrayNotifyWnd**, ReBarWindow32 | DesktopWindowContentBridge, Start, WorkerW; **tanpa `TrayNotifyWnd` dan `ReBarWindow32`** |
| Pohon UIA | TaskbarFrame → TaskbarFrameRepeater → StartButton + TaskListButton; tray = 8 `SystemTray.*` | struktur XAML sama (TaskbarFrame → Repeater → StartButton + TaskListButton); tray hanya `SystemTray.OmniButton` (jam) |
| Analyzer | valid, trayLeft=1642 (= TrayNotifyWnd), 11 kotak terisi | **valid**, trayLeft=-78 (jam), trayNotifyLeft tidak ada, 7 kotak terisi |
| Ruang kosong | 1115 → 1642 (527 px) | -473 → -78 (395 px) |

Detail lain:
- Taskbar sekunder rata tengah seperti utama (Start di -606 pada monitor -1080..0).
- `WorkerW` / `MSTaskListWClass` (Win32 lama) ikut COUNTED tetapi rect-nya di dalam area tombol aplikasi, jadi tidak
  menggeser hasil.
- Root `Shell_SecondaryTrayWnd` di-IGNORE karena aturan lebar ≥ 50%, bukan karena daftar kelas container (kosmetik).

## Keputusan untuk batch 6b

1. **Probe dan analyzer bisa dipakai apa adanya** untuk taskbar sekunder: cukup ganti handle akar dari `Shell_TrayWnd`
   ke `Shell_SecondaryTrayWnd` milik monitor target. Penempatan otomatis di sekunder layak.
2. **Tanpa `TrayNotifyWnd`:** cadangan "dock di kiri `TrayNotifyWnd` sebelum ada layout valid" harus diganti untuk
   sekunder, mis. tepi kanan taskbar dikurangi lebar jam terakhir yang diketahui, atau tunggu layout UIA pertama.
3. **Belum teruji, wajib dicek di 6b:** jam disembunyikan di taskbar sekunder (tidak ada elemen tray sama sekali →
   `TaskbarLayoutAnalyzer` menolak dengan "no tray elements" karena tidak ada `TrayNotifyWnd` sebagai cadangan → perlu
   aturan khusus sekunder: trayLeft = tepi kanan taskbar), monitor sekunder dengan
   DPI berbeda, taskbar sekunder rata kiri, dan owner/topmost terhadap `Shell_SecondaryTrayWnd`.
4. Hal yang sekarang terikat ke monitor utama dan harus mengikuti monitor target: `PrimaryMonitorRect()` di visibilitas
   auto-hide, deteksi fullscreen (`FullscreenDetector` membandingkan dengan monitor `Shell_TrayWnd`), dan owner.
5. Kosmetik: tambahkan `Shell_SecondaryTrayWnd` ke daftar kelas container agar log verdict konsisten (tidak dikerjakan:
   tidak mengubah hasil).

**Diterapkan di 6b:** butir 1 (probe/analyzer per handle target), 2 (sekunder menunggu layout pertama maks 3 detik, lalu
tepi kanan), aturan tanpa jam (`trayOptional`), dan butir 4 (auto-hide memakai monitor target untuk sekunder; owner dan
fullscreen sudah mengikuti handle target). Butir 3 sisanya diuji manual (checklist "Show on").
