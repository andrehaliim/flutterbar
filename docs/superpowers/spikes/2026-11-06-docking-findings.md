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
Posisi manual tersimpan di `spike-settings.json` (sebelah exe); hapus file itu atau pilih "Reset to auto position" untuk kembali ke auto.

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

## Pengamatan otomatis mode normal (v1)

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
| Auto-hide, DPI 125/150%, taskbar penuh | tidak dilaporkan (belum tentu OK) |

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
   widget, bukan jendela shell. Klasifikasi shell berdasarkan PROSES PEMILIK untuk setiap kelas jendela
   (explorer, SearchHost, StartMenuExperienceHost, ShellExperienceHost, ShellHost = shell; proses lain = bukan;
   gagal lookup = shell); daftar kelas (Shell_TrayWnd, Shell_SecondaryTrayWnd, Progman, WorkerW) hanya jalur cepat.
   Ini menutup kasus Alt+Tab / Task View (`XamlExplorerHostIslandWindow`, milik explorer) yang menutup monitor.
   Foreground = null atau widget sendiri: status sebelumnya dipertahankan. Syarat lain: satu monitor dengan taskbar, dan `GetWindowRect` menutup seluruh `rcMonitor`
   -> widget di-hide; kembali tampil saat tidak lagi. `SHQueryUserNotificationState` hanya dicatat ke log.
4. **Rata taskbar dan penempatan gap**: `TaskbarAl` (0 = kiri, 1/kosong = tengah) dibaca di timer 600 ms
   (auto-hide juga hanya di timer, di-cache). Semua elemen non-tray (Start, Search, Widgets `#32770`, Task View,
   tombol aplikasi; container lebar, ReBar/TrayNotifyWnd, gambar, dan elemen milik ProcessId widget sendiri
   diabaikan) menjadi kotak terisi. Gap = ruang antar kotak terurut (digabung) dan antara kotak terakhir
   dan `min(Left)` elemen `SystemTray.*` di dalam rect taskbar; tiap tepi gap diinset 8 px. Widget
   menempel di ujung KANAN gap paling kanan yang lebarnya >= lebar widget. Jika tidak ada gap yang muat,
   widget di-hide dan dicatat di log. Gap yang dipilih tampil di teks debug widget. Query UIA di `Task.Run`,
   maksimal 1x/detik, hasil terakhir yang baik dipertahankan bila gagal.
5. **Restart explorer**: pesan `TaskbarCreated` lewat hook HwndSource -> cari ulang Shell_TrayWnd, set owner,
   pasang ulang hook. Hook location hanya dipasang ulang bila handle tray berubah. Jika jendela ikut hancur
   (cek `IsWindow` tiap tick atau event Closed), jendela baru dibuat ulang (dengan log). Dugaan (belum diverifikasi):
   saat explorer restart, owner kemungkinan di-reset ke NULL, bukan jendela dihancurkan; keduanya ditangani.
6. **Efek samping owner lintas proses**: relasi owner/owned antar proses menyatukan (attach) antrean input
   kedua thread (Raymond Chen). Jika thread UI widget macet, input taskbar bisa ikut macet. Spike punya
   menu klik kanan "Freeze 5s (debug)" di widget (Thread.Sleep 5000) untuk menguji ini.
7. Hide/show simetris: hide `ShowWindow(SW_HIDE)`, show `ShowWindow(SW_SHOWNOACTIVATE)`. SetWindowPos hanya
   memindah/mengubah ukuran saat target berubah; topmost tetap di-reassert di setiap pemicu. Ada guard re-entrancy.

Temuan penting: jendela widget yang dimiliki taskbar ikut muncul sebagai anak di pohon UIA Shell_TrayWnd
(ProcessId = proses widget), sehingga harus difilter (ProcessId sendiri), kalau tidak area kosong menyusut.

Pengamatan otomatis v2 awal (taskbar rata tengah, 1920x1080, DPI 96, auto-hide mati):

- `GetWindow(hwnd, GW_OWNER)` == `Shell_TrayWnd` (cocok), ex-style berisi NOACTIVATE.
- Posisi widget (1334,1036)-(1634,1076): 8 px di kiri tray (x=1642), area kosong 511 px
  (occupied right=1115, 8 elemen terhitung: Start, 6 tombol aplikasi, pane Widgets).
- Membuka dan menutup jendela baru (cmd) menghasilkan log `reassert: foreground:CASCADIA_HOSTING_WINDOW_CLASS`
  dan `reassert: locationchange`; log dibatasi 1 baris per alasan per 2 detik.
- `SHQueryUserNotificationState` = 5 (QUNS_ACCEPTS_NOTIFICATIONS) pada desktop normal; fullscreen=false.
- Tidak teramati otomatis: z-order nyata saat Start/Action Center dibuka, fullscreen video, rata kiri,
  auto-hide, DPI lain, restart explorer (tidak dijalankan karena terlihat oleh user): BELUM DIUJI.

## Hasil uji ulang v2 (user, 2026-11-07)

Taskbar rata kiri, 1920x1080, DPI 100%.

| Uji | Hasil |
|---|---|
| Buka Start | BUG 1: widget melompat menutupi ikon tray dan jam. Teks debug: `gap=[8..1912]=1904 -> pakai [8..1912]`. Query UIA tidak mengembalikan kotak terisi dan tidak ada elemen tray saat Start terbuka, sehingga seluruh taskbar dianggap kosong. (Normal: `gap=[595..1470]=875 -> pakai [595..1470] DPI=96 (100%) rata=kiri fs=tidak ah=tidak last=timer`.) |
| Klik kanan widget | BUG 2: ContextMenu tergambar DI BELAKANG widget (reassert topmost widget menaikkan widget di atas popup menu). |

Catatan: user juga memakai TrafficMonitor (overlay pihak ketiga dekat tray) yang tidak terlihat di pohon UIA taskbar.
Deteksi otomatis tidak akan pernah bisa menghindarinya, sehingga diminta posisi manual (drag). Hasil uji v2 lainnya
tidak dilaporkan; semuanya diuji ulang di checklist v3.

## Spike v3

Teknik (akan disalin ke Task 13):

1. **Sanity check layout UIA**: hasil UIA tanpa elemen tray, atau tanpa kotak terisi (Start/tombol aplikasi = 0),
   dianggap TIDAK VALID: `Compute` mengembalikan null, dicatat ke log (`UIA layout REJECTED ...`, dibatasi 1x/5 detik),
   dan layout terakhir yang baik dipertahankan.
2. **TrayNotifyWnd sebagai sumber/cek kiri tray**: `FindWindowEx(Shell_TrayWnd, 0, "TrayNotifyWnd")` lalu `GetWindowRect`
   (stabil saat Start terbuka). Bila kiri tray dari UIA dan kiri TrayNotifyWnd berbeda > 300 px, TrayNotifyWnd
   dipakai dan perbedaannya dicatat (`tray-left mismatch`). Di mesin ini keduanya sama (1674).
3. **Menu/popup di atas widget**: selama ContextMenu terbuka (`ContextMenuOpening`/`Opened` sampai `Closed`) flag
   `_menuOpen` menangguhkan reassert topmost sepenuhnya (SetWindowPos memakai `SWP_NOZORDER`; posisi tetap dihitung).
   Saat `Opened`, HWND popup dicari lewat `PresentationSource.FromVisual(contextMenu) as HwndSource` dan di-`SetWindowPos`
   `HWND_TOPMOST` dengan `NOACTIVATE|NOMOVE|NOSIZE`. Menu dibuka ke atas (`Placement="Top"`) karena widget ada di dasar
   layar. Pola yang sama (flag penangguhan + topmost-kan HWND popup) harus dipakai untuk tooltip/popup/flyout lain
   (Task 13/14).
4. **Posisi manual + drag**: menu klik kanan (label BAHASA INGGRIS, keputusan user): item centang **"Drag widget"**
   dan **"Reset to auto position"**. Mode `auto` (default run pertama) = penempatan gap. Saat "Drag widget" dicentang:
   widget hanya bisa digeser horizontal (Y tetap menempel taskbar), isyarat visual berupa border 1 px warna aksen
   dan kursor SizeWE. Drag memakai mouse capture (`MouseLeftButtonDown` -> `CaptureMouse`; `MouseMove` -> X baru dalam piksel
   fisik dari `GetCursorPos`, di-clamp ke rect taskbar, `SetWindowPos NOZORDER|NOACTIVATE`; `MouseLeftButtonUp` -> `ReleaseMouseCapture`).
   Bukan `Window.DragMove` (mengaktifkan jendela dan memindah dua sumbu). Selama drag, hitung ulang layout dilewati.
5. **Penguncian**: un-centang "Drag widget" setelah menggeser -> mode `manual`, disimpan sebagai **jarak dari tepi kanan
   taskbar dalam piksel fisik** (`offsetFromRight = taskbar.Right - (x + w)`) di `spike-settings.json` sebelah exe:
   `{"mode":"manual","offsetFromRight":N}`; dimuat saat start. Tanpa menggeser, un-centang tidak mengunci. Mode manual:
   `x = taskbar.Right - offsetFromRight - w`, di-clamp ke taskbar (resolusi berubah), TANPA penghindaran tombol otomatis.
   Owner, reassert topmost, sembunyi fullscreen, dan auto-hide tetap berjalan. "Reset to auto position": mode auto,
   file settings dihapus, posisi dihitung ulang seketika. Teks debug menampilkan `mode=auto|manual|drag off=N`.
6. **Hook uji**: argumen `--force-empty-after=N` membuat hasil UIA dianggap kosong setelah N query baik (untuk
   memverifikasi penolakan layout). Hanya untuk spike.

Pengamatan otomatis v3 (rata kiri, 1920x1080, DPI 96):

- Auto: widget (1202,1036)-(1502,1076), gap [595..1502], `tnLeft=1674` sama dengan tray-left UIA.
- `spike-settings.json` ditulis tangan `{"mode":"manual","offsetFromRight":500}` -> widget di (1120,1036): 1920-500-300 = 1120 (OK).
- `--force-empty-after=2`: log `UIA layout REJECTED (tray elements=False, occupied=0, ...) -> keeping last good layout`, posisi widget tidak berubah.
- Drag dengan mouse, simpan setelah un-centang, menu di atas widget, dan Start terbuka: BELUM DIUJI (butuh interaksi user).

## Checklist manual v3

| Uji | Hasil |
|---|---|
| Taskbar rata tengah | BELUM DIUJI (user) |
| Taskbar rata kiri (ganti saat widget berjalan: harus pindah dalam ~1 detik) | BELUM DIUJI (user) |
| Klik Start / Search: widget tetap terlihat? | BELUM DIUJI (user) |
| Buka Start: widget tidak pindah/menutupi tray | BELUM DIUJI (user) |
| Klik tombol aplikasi: widget tetap terlihat? | BELUM DIUJI (user) |
| Buka Action Center / Quick Settings: widget tetap terlihat? | BELUM DIUJI (user) |
| Menu klik kanan tampil di atas widget | BELUM DIUJI (user) |
| Auto-hide taskbar menyala | BELUM DIUJI (user) |
| Video YouTube fullscreen: widget hilang, muncul lagi setelah keluar? | BELUM DIUJI (user) |
| F11 di browser: widget hilang, muncul lagi setelah keluar? | BELUM DIUJI (user) |
| DPI 100% | BELUM DIUJI (user) |
| DPI 125% | BELUM DIUJI (user) |
| DPI 150% | BELUM DIUJI (user) |
| Tambah/tutup aplikasi sampai taskbar penuh: area kosong terhitung ulang | BELUM DIUJI (user) |
| Bekukan UI thread widget (klik kanan widget > "Freeze 5s (debug)"): taskbar tetap bisa diklik? | BELUM DIUJI (user) |
| Alt+Tab / Task View: widget tidak ikut hilang? | BELUM DIUJI (user) |
| Explorer restart (Task Manager > Restart): widget kembali terpasang/dibuat ulang | BELUM DIUJI (user) |
| Drag widget: bisa digeser horizontal | BELUM DIUJI (user) |
| Uncheck Drag widget: posisi terkunci & bertahan setelah spike ditutup-buka | BELUM DIUJI (user) |
| Reset to auto position | BELUM DIUJI (user) |
| Mode manual + fullscreen/auto-hide tetap jalan | BELUM DIUJI (user) |
| Bersebelahan dengan TrafficMonitor | BELUM DIUJI (user) |
| Working set spike (Task Manager, MB) | BELUM DIUJI (user) |

## Checklist manual v2 (tidak dipakai lagi, digantikan checklist v3)

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
| Bekukan UI thread widget (klik kanan widget > "Freeze 5s (debug)"): taskbar tetap bisa diklik? | BELUM DIUJI (user) |
| Alt+Tab / Task View: widget tidak ikut hilang? | BELUM DIUJI (user) |
| Explorer restart (Task Manager > Restart): widget kembali terpasang/dibuat ulang | BELUM DIUJI (user) |
| Working set spike (Task Manager, MB) | BELUM DIUJI (user) |

## Checklist manual v1 (tidak dipakai lagi, digantikan hasil uji v1 dan checklist v3)

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

## Catatan untuk Task 13 (aturan v2 + v3)

- Owner: setelah HWND ada, `SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, Shell_TrayWnd)` (SetWindowLongPtrW hanya
  ada di x64/ARM64; jangan target x86). Setelah itu jangan ubah `ShowInTaskbar`/`Owner` WPF. Pasang ulang
  owner bila handle Shell_TrayWnd berubah; tangani `TaskbarCreated`.
- Peringatan: owner lintas proses menyatukan antrean input thread. **UI thread tidak boleh pernah blocking**
  (tanpa Thread.Sleep, .Result, I/O sinkron), jika tidak taskbar bisa ikut macet.
- Reassert topmost (`SetWindowPos(HWND_TOPMOST, NOMOVE|NOSIZE|NOACTIVATE)`) pada setiap
  `EVENT_SYSTEM_FOREGROUND`, `EVENT_OBJECT_LOCATIONCHANGE` proses taskbar (digabung), dan timer 600 ms.
- **Popup/menu/tooltip (v3)**: tangguhkan reassert topmost selama popup terbuka (flag, `SWP_NOZORDER`), dan pada
  `Opened` topmost-kan HWND popup (`PresentationSource.FromVisual(popup) as HwndSource`). Berlaku untuk ContextMenu,
  tooltip, dan flyout. Buka menu ke atas (`Placement=Top`).
- **Layout sanity check (v3)**: hasil UIA tanpa elemen tray atau tanpa kotak terisi tidak valid -> pertahankan layout
  terakhir yang baik (log). Pakai `TrayNotifyWnd` (Win32, `FindWindowEx` anak `Shell_TrayWnd`) sebagai sumber/cek kiri
  tray; selisih > 300 px dengan UIA -> pakai TrayNotifyWnd. UIA kosong saat Start terbuka terbukti terjadi.
- Penempatan (mode auto): kotak terisi (semua elemen non-tray) -> gap -> ujung kanan gap paling kanan yang muat, inset 8 px;
  tidak ada gap = hide. Filter elemen UIA milik ProcessId sendiri (widget yang di-own muncul di pohon UIA taskbar).
  Koordinat UIA piksel fisik (PerMonitorV2); jangan kalikan DPI lagi. UIA di thread latar, maks 1x/detik.
  Overlay pihak ketiga (mis. TrafficMonitor) tidak terlihat di UIA -> itulah alasan mode manual.
- **Mode manual (v3)**: drag horizontal dengan mouse capture (`GetCursorPos` piksel fisik, clamp ke rect taskbar,
  `SetWindowPos NOZORDER|NOACTIVATE`; jangan `Window.DragMove`), hitung ulang layout dilewati saat drag. Un-centang
  setelah drag mengunci posisi sebagai offset dari tepi kanan taskbar (piksel fisik), clamp bila di luar taskbar,
  tanpa penghindaran tombol. Reset = kembali auto.
- Klasifikasi shell lewat proses pemilik (explorer, SearchHost, StartMenuExperienceHost, ShellExperienceHost,
  ShellHost); fullscreen = foreground non-shell di monitor taskbar dengan rect menutup `rcMonitor`, hanya bila
  auto-hide mati.
- Nama kelas/AutomationId: Start `ToggleButton`/`StartButton`; tombol aplikasi `Taskbar.TaskListButtonAutomationPeer`;
  tray `SystemTray.*` (`SystemTrayIcon`/`NotifyItemIcon`); jangan pakai `ReBarWindow32`.
- Gerbang keputusan (Step 6) ditentukan controller/user setelah checklist v3 diisi.
