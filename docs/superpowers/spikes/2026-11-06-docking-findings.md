# Findings spike docking taskbar (2026-11-06)

Spike: `spikes/DockingSpike` (WPF, throwaway, tidak masuk `FlutterBar.sln`).
OS: Windows 11 Pro build 26300. Monitor utama 1920x1080, DPI 96 (100%), taskbar rata tengah, tidak auto-hide.

## Cara menjalankan

```text
dotnet run --project spikes/DockingSpike
```

atau exe hasil build: `spikes\DockingSpike\bin\Debug\net10.0-windows\DockingSpike.exe`.
Mode probe tanpa UI (cetak pohon UI Automation Shell_TrayWnd): tambahkan `--probe`
(`dotnet run --project spikes/DockingSpike -- --probe`). Log ditulis ke `spike.log` di sebelah exe.
Tutup widget lewat Task Manager atau `Stop-Process -Name DockingSpike`.

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

## Checklist manual (Step 4 dan 5)

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
