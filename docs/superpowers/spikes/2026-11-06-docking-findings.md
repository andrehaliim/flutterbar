# Findings spike docking taskbar (2026-11-06)

Spike: `spikes/DockingSpike` (WPF, throwaway, tidak masuk `FlutterBar.sln`).
OS: Windows 11 Pro build 26300. Monitor utama 1920x1080, DPI 96 (100%), taskbar rata tengah, tidak auto-hide.

## Cara menjalankan

```text
dotnet build spikes/DockingSpike
spikes\DockingSpike\bin\Debug\net10.0-windows\DockingSpike.exe
```

(atau `dotnet run --project spikes/DockingSpike`). Mode probe tanpa UI (cetak pohon UI Automation
Shell_TrayWnd): tambahkan `--probe`. Log ditulis ke `spike.log` di sebelah exe (reassert dengan
timestamp, perubahan layout, status fullscreen, TaskbarCreated). Teks di dalam widget menampilkan:
lebar area kosong, DPI, rata taskbar (kiri/tengah), fullscreen ya/tidak, alasan reassert terakhir.
Tutup widget lewat `Stop-Process -Name DockingSpike`.

## Hasil probe otomatis (UI Automation, kedalaman 6)

- `Shell_TrayWnd` rect: (0,1032)-(1920,1080), tinggi 48 px, DPI 96.
- Tombol Start: ClassName `ToggleButton`, AutomationId `StartButton`, rect (784..829).
- Tombol aplikasi: ClassName `Taskbar.TaskListButtonAutomationPeer`, AutomationId `Appid: <AppUserModelID>`
  (contoh `Appid: Chrome`), Name seperti `Google Chrome - 1 running window pinned`.
  Container: `Taskbar.TaskbarFrameAutomationPeer` (AutomationId `TaskbarFrame`) di dalam
  `Windows.UI.Input.InputSite.WindowClass`. Tombol aplikasi terakhir berakhir di x=1137.
- Area tray: elemen anak langsung `Windows.UI.Input.InputSite.WindowClass` dengan ClassName berawalan
  `SystemTray.` (`SystemTray.NormalButton`, `SystemTray.AccentText`, `SystemTray.AccentButton`,
  `SystemTray.OmniButtonRight`, `SystemTray.OmniButton`, `SystemTray.ShowDesktopButton`).
  AutomationId `SystemTrayIcon` (chevron "Show Hidden Icons", Network, Volume, Clock, Show Desktop,
  dll.) atau `NotifyItemIcon` (ikon tray biasa, mis. Steam, Spotify).
  Elemen tray paling kiri: `SystemTray.NormalButton` / `SystemTrayIcon` "Show Hidden Icons", x=1610.
  Jam: `SystemTray.OmniButton`, Name `Clock ...`, rect (1834..1904).
- Ada juga window Win32 lama sebagai anak: `TrayNotifyWnd` rect (1610..1920) dan `ReBarWindow32`
  rect (828..1092, tidak mencerminkan tombol aplikasi sebenarnya, jadi jangan dipakai di Win11);
  serta pane `#32770` rect (2..238), area Widgets di kiri.
- **Area kosong terhitung** (tombol aplikasi terakhir ke elemen tray paling kiri): 1137 .. 1610 = **473 px**.

## Pengamatan otomatis mode normal

- Widget 300x40 start tanpa crash, `GetWindowRect` = (1306,1036)-(1606,1076): tepat 4 px di kiri tray
  (x=1610), terpusat vertikal di taskbar (1032..1080), seluruhnya di dalam area kosong 473 px.
- Re-probe tiap 1 detik berjalan stabil (posisi tetap, tidak ada exception di `spike.log`).
- Hook `EVENT_SYSTEM_FOREGROUND` terpasang; log "reapplied topmost" hanya muncul bila foreground
  berpindah ke `Shell_TrayWnd`. Ini belum teramati karena butuh interaksi manual.
- Tidak teramati otomatis: tampilan visual, perilaku z-order saat Start dibuka, auto-hide, fullscreen, DPI lain.

## Hasil uji manual v1 (user, 2026-11-07)

| Uji | Hasil |
|---|---|
| Klik Start / Search | GAGAL: widget tersembunyi/tertutup |
| Klik tombol aplikasi di taskbar | GAGAL: widget tersembunyi/tertutup |
| Buka Action Center / Quick Settings | GAGAL: widget tersembunyi/tertutup |
| Video fullscreen / F11 browser | GAGAL: widget tetap tampil di atas video |
| Taskbar rata kiri | GAGAL: posisi salah |
| Auto-hide, DPI 125/150%, taskbar penuh | tidak dilaporkan bermasalah |

Penyebab dan perbaikan di v2: z-order hanya di-reassert saat foreground = Shell_TrayWnd (Start/Search/
Action Center memakai jendela lain), tidak ada owner, tidak ada deteksi fullscreen, dan area kosong
hanya memperhitungkan tombol aplikasi.

## Spike v2

Teknik (akan disalin ke Task 13):

1. **Owner = taskbar**: setelah HWND ada, `SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT(-8), Shell_TrayWnd)`.
   Jendela yang dimiliki selalu di atas owner-nya dalam z-order. Tetap `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` + Topmost.
   Owner di-set ulang bila handle Shell_TrayWnd berubah.
2. **Reassert topmost** (`SetWindowPos(HWND_TOPMOST, ... NOACTIVATE)` + hitung ulang posisi) pada: setiap
   `EVENT_SYSTEM_FOREGROUND` (tanpa filter), `EVENT_OBJECT_LOCATIONCHANGE` untuk proses taskbar (digabung:
   satu antrean via flag pending), dan timer 600 ms. Delegate hook disimpan di field, hook dilepas saat close,
   kegagalan pemasangan hook dicatat ke log.
3. **Fullscreen**: hanya jika auto-hide mati (`SHAppBarMessage(ABM_GETSTATE) & ABS_AUTOHIDE`). Foreground bukan
   widget, bukan jendela shell (Shell_TrayWnd, Shell_SecondaryTrayWnd, Progman, WorkerW; CoreWindow/
   ApplicationFrameWindow dari explorer, SearchHost, StartMenuExperienceHost, ShellExperienceHost, ShellHost;
   gagal lookup = shell), satu monitor dengan taskbar, dan `GetWindowRect` menutup seluruh `rcMonitor`
   -> widget di-hide; kembali tampil saat tidak lagi. `SHQueryUserNotificationState` hanya dicatat ke log.
4. **Rata taskbar**: `HKCU\...\Explorer\Advanced\TaskbarAl` (0 = kiri, 1/kosong = tengah) dibaca tiap tick.
   Area kosong = dari `max(Right)` semua elemen non-tray (Start, Search, Widgets `#32770`, Task View, tombol
   aplikasi; container lebar dan elemen milik proses widget sendiri diabaikan) + 8 sampai `min(Left)` elemen
   `SystemTray.*` di dalam rect taskbar - 8. Widget menempel di ujung kanan area kosong. Query UIA di
   `Task.Run`, maksimal 1x/detik, hasil terakhir yang baik dipertahankan bila gagal.
5. **Restart explorer**: pesan `TaskbarCreated` lewat hook HwndSource -> cari ulang Shell_TrayWnd, set owner,
   pasang ulang hook. Jika jendela ikut hancur bersama owner, jendela baru dibuat ulang (dengan log).

Temuan penting: jendela widget yang dimiliki taskbar ikut muncul sebagai anak di pohon UIA Shell_TrayWnd
(ProcessId = proses widget), sehingga harus difilter (ProcessId sendiri), kalau tidak area kosong menyusut.

Pengamatan otomatis v2 (taskbar rata tengah, 1920x1080, DPI 96, auto-hide mati):

- `GetWindow(hwnd, GW_OWNER)` == `Shell_TrayWnd` (cocok), ex-style berisi NOACTIVATE.
- Posisi widget (1334,1036)-(1634,1076): 8 px di kiri tray (x=1642), area kosong 511 px
  (occupied right=1115, 8 elemen terhitung: Start, 6 tombol aplikasi, pane Widgets).
- Membuka dan menutup jendela baru (cmd) menghasilkan log `reassert: foreground:CASCADIA_HOSTING_WINDOW_CLASS`
  dan `reassert: locationchange`; log dibatasi 1 baris per alasan per 2 detik.
- `SHQueryUserNotificationState` = 5 (QUNS_ACCEPTS_NOTIFICATIONS) pada desktop normal; fullscreen=false.
- Tidak teramati otomatis: z-order nyata saat Start/Action Center dibuka, fullscreen video, rata kiri,
  auto-hide, DPI lain, restart explorer (tidak dijalankan karena terlihat oleh user): BELUM DIUJI.

## Checklist manual v2

| Uji | Hasil |
|---|---|
| Taskbar rata tengah | BELUM DIUJI (user) |
| Taskbar rata kiri (ganti saat widget berjalan: harus pindah dalam ~1 detik) | BELUM DIUJI (user) |
| Klik Start / Search: widget tetap terlihat? | BELUM DIUJI (user) |
| Klik tombol aplikasi: widget tetap terlihat? | BELUM DIUJI (user) |
| Buka Action Center / Quick Settings: widget tetap terlihat? | BELUM DIUJI (user) |
| Auto-hide taskbar menyala | BELUM DIUJI (user) |
| Video YouTube fullscreen: widget hilang, muncul lagi setelah keluar? | BELUM DIUJI (user) |
| F11 di browser: widget hilang, muncul lagi setelah keluar? | BELUM DIUJI (user) |
| DPI 100% | BELUM DIUJI (user) |
| DPI 125% | BELUM DIUJI (user) |
| DPI 150% | BELUM DIUJI (user) |
| Tambah/tutup aplikasi sampai taskbar penuh: area kosong terhitung ulang | BELUM DIUJI (user) |
| Explorer restart (Task Manager > Restart): widget kembali terpasang/dibuat ulang | BELUM DIUJI (user) |
| Working set spike (Task Manager, MB) | BELUM DIUJI (user) |

## Checklist manual v1 (tidak dipakai lagi, digantikan hasil uji v1 dan checklist v2)

| Uji | Hasil |
|---|---|
| Taskbar rata tengah (default sekarang) | BELUM DIUJI (user) |
| Taskbar rata kiri | BELUM DIUJI (user) |
| Klik Start: widget tetap terlihat? | BELUM DIUJI (user) |
| Klik tombol aplikasi: widget tetap terlihat? | BELUM DIUJI (user) |
| Buka Action Center: widget tetap terlihat? | BELUM DIUJI (user) |
| Auto-hide taskbar menyala | BELUM DIUJI (user) |
| Video YouTube fullscreen | BELUM DIUJI (user) |
| F11 di browser | BELUM DIUJI (user) |
| DPI 100% | BELUM DIUJI (user) |
| DPI 125% | BELUM DIUJI (user) |
| DPI 150% | BELUM DIUJI (user) |
| Tambah/tutup aplikasi sampai taskbar penuh: area kosong terhitung ulang (poll 1 detik) | BELUM DIUJI (user) |
| Working set spike (Task Manager, MB) | BELUM DIUJI (user) |

## Catatan untuk Task 13

- Batas kiri area kosong: `max(Right)` elemen `Taskbar.TaskListButtonAutomationPeer`.
- Batas kanan: `min(Left)` elemen dengan ClassName berawalan `SystemTray.` (atau AutomationId
  `SystemTrayIcon` / `NotifyItemIcon`).
- Koordinat UIA berupa piksel fisik karena proses PerMonitorV2 (manifest); jangan dikali DPI lagi.
- Gerbang keputusan (Step 6) ditentukan controller/user setelah checklist diisi.
