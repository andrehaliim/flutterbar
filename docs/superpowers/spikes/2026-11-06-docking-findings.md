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

## Hasil uji ulang v3 (user, 2026-11-07)

Taskbar rata kiri, 1920x1080, DPI 100%.

BUG: dengan "Drag widget" aktif, klik "Reset to auto position" membuat widget tidak stabil lalu hilang (proses masih hidup).
Bukti di `spike.log` pukul 17:39:03:
`gap fit -> NONE (hide widget) gaps: [595..785]=190 [1218..1502]=284 -> TIDAK ADA gap cukup`.

Akar masalah:
1. Mode auto butuh 300 px, gap terlebar hanya 284 px, dan aturan v3 menyembunyikan widget bila tidak ada gap yang muat.
2. Gap berkedip antar query (occupied=26/27/28; mis. `[1218..1358]=140`, `[1218..1328]=110 [1493..1502]=9`): elemen transien
   (thumbnail/tooltip taskbar saat hover, dan elemen non-tombol) ikut masuk pohon UIA dan dihitung terisi.
3. Setelah tersembunyi, user tidak bisa mencapai menu klik kanan, jadi terkunci (tidak bisa Drag widget lagi).

Keputusan user (opsi A): mode auto TIDAK PERNAH hilang karena kurang ruang.

## Spike v3.1

Teknik (akan disalin ke Task 13):

1. **Ukuran bertingkat** seperti `WidgetLayoutPolicy`: Full 300, Compact 260, ButtonsOnly 120 (px logis, dikali DPI). Iterasi
   Full -> Compact -> ButtonsOnly; untuk tiap ukuran pilih gap muat paling KANAN; ukuran pertama yang menemukan gap menang.
   Spike hanya mengubah lebar dan teks (Compact = teks debug pendek, ButtonsOnly = baris kecil `▶ ⚡`).
2. **Tidak ada yang muat (mode auto):** widget TIDAK disembunyikan. Tetap tampil di X valid terakhir (atau kiri `TrayNotifyWnd`
   bila belum ada) dalam ukuran ButtonsOnly, menampilkan "Taskbar penuh — gunakan Drag widget" (teks + tooltip), dan dicatat ke log
   (`taskbar full: ...`). Sembunyi hanya untuk fullscreen dan auto-hide.
3. **Filter elemen terisi (revisi setelah review)**: elemen dihitung terisi bila (a) rect di dalam rect taskbar (toleransi 2 px),
   (b) tinggi >= 50% taskbar, (c) lebar >= 8 px, (d) bukan proses sendiri/`DockingSpike`/`FlutterBar.App` (by nama proses), (e) bukan transien
   (ControlType `ToolTip`, kelas popup/`TaskListThumbnailWnd`). Thumbnail/flyout ada di atas taskbar sehingga (a) sudah mengecualikan. Daftar putih
   tombol versi awal DIBUANG (bisa melewatkan elemen explorer non-tombol dekat tray). TrafficMonitor tidak ada di pohon UIA `Shell_TrayWnd`, jadi
   tidak pernah terhitung. Tiap (class, id, controlType, pid) baru dicatat sekali: `UIA element COUNTED/IGNORED (alasan)`.
   Hasil log: elemen cuaca dekat tray = tombol `Widgets` (`ToggleButton`, id `WidgetsButton`, rect 1510..1662) beserta anak `NamedContainerAutomationPeer`
   dan `AnimatedVisualPlayer`, semuanya milik explorer dan terhitung; itulah sebab gap berakhir di 1502 (bukan 1674 = TrayNotifyWnd).
4. **Debounce**: penempatan baru (ukuran, x) diterapkan hanya bila kandidat yang sama (toleransi x 3 px) keluar dari 2 query UIA
   berturut-turut (~1-2 detik). Kandidat yang ditahan dicatat (`placement candidate ... held back`). Layout pertama setelah startup,
   setelah "Reset to auto position", dan setelah `TaskbarCreated` diterapkan langsung (`placement APPLIED ... [immediate ...]`).
5. **Reset saat Drag widget aktif**: keluar dari mode drag dengan bersih, hapus settings, lalu memakai jalur terapkan-langsung di atas.
6. Hook uji: `--force-min-gap=N` memperkecil setiap gap menjadi paling lebar N px (tepi kanan tetap), untuk menguji turun ukuran dan jalur tidak-muat.

Pengamatan otomatis (rata kiri, 1920x1080, DPI 96, build ke direktori scratch karena exe di bin dipakai instance lain):
- PERINGATAN: pada run awal, spike v3 milik user (pid 22888, di x 1202..1502) masih berjalan dan ikut terhitung terisi oleh aturan "proses lain"
  yang terlalu longgar, sehingga widget hanya sampai x=894 (tepi kanan ~1194) padahal v3 mencapai 1502. Pengamatan "normal" itu tercemar.
  Setelah filter diperketat (proses `DockingSpike` diabaikan): `placement APPLIED Full x=1202 [immediate (first layout)]`, gap `[595..1502]`, sama dengan v3.
- Run awal juga menunjukkan kandidat transien `Full x=750` ditahan (`held back ... needs 2 consecutive queries`); itu sebagian akibat widget user ikut bergerak.
  Max-hold: kandidat berbeda yang ditahan >= 5 query berturut-turut diterapkan (log `max-hold`); belum teramati di run.
- `--force-min-gap=270`: Compact (260). `--force-min-gap=130`: ButtonsOnly (120). `--force-min-gap=50`: log
  `taskbar full: nothing fits even ButtonsOnly -> widget STAYS VISIBLE`, `visible=True`, ButtonsOnly di x=1546 (fallback kiri TrayNotifyWnd karena belum ada X valid).
- Belum diuji otomatis: klik Reset saat drag (butuh mouse), hover tombol aplikasi, taskbar penuh sungguhan.

## Hasil uji ulang v3.1 (user, 2026-11-07)

Rekaman video ~17:58, taskbar rata tengah, 1920x1080, DPI 100%.

1. Dengan auto-hide taskbar menyala, widget TIDAK ikut hilang saat taskbar meluncur keluar layar.
2. Teks widget "tidak mau diam" (berubah terus). Penyebab: teks debug (angka gap, `fs/ah`, `last=reason`) ditulis ulang tiap tick/event.

Bukti `spike.log` (bin/Debug, mulai 17:56): sejak 17:56:14 muncul `AttachToTaskbar(handle/owner changed): tray=... owner=0x0` tiap ~2,4 detik tanpa henti,
dan `UIA layout REJECTED (tray elements=False, occupied=0, elements=3 ...)` tiap ~6 detik saat taskbar auto-hide. Semua baris `layout:` tetap
`visible=True` dengan `ah=ya` karena v3.1 tidak punya pelacakan visibilitas auto-hide.

Akar masalah owner (TERBUKTI; hipotesis awal "instance lama/zombie setelah window dibuat ulang" SALAH):
- Log tidak punya satu pun baris `widget window destroyed ... recreating`, jadi window tidak pernah dibuat ulang. Owner sudah 0 sejak 17:56:14, sebelum explorer restart (17:57:35),
  dengan handle taskbar yang sama (`0x40D0C`). Hanya ada satu instance.
- Pemicu 17:56:14: user membuka Start/Search (foreground `Windows.UI.Core.CoreWindow`, `SHQueryUserNotificationState=7`, UIA taskbar hanya `elements=1`).
- Direproduksi dengan menekan tombol Win saat spike berjalan (instance tunggal). Selama Start/Search terbuka, `GetWindow(hwnd, GW_OWNER)` mengembalikan 0 PADAHAL
  owner asli masih utuh: `GetWindowLongPtr(GWLP_HWNDPARENT)` = tray (log: `EXP readback differs: GW_OWNER=0x0 GWLP_HWNDPARENT=0x1C0A10 ... qunsFg=Windows.UI.Core.CoreWindow`).
- Kode v3.1 memakai `GW_OWNER != tray` sebagai tanda "owner hilang", lalu memanggil `SetWindowLongPtr(GWLP_HWNDPARENT, tray)` lagi. Memasang owner yang SAMA itu justru MENGHAPUS owner
  (log: `prev=0x1C0A10 err=0` lalu GWLP_HWNDPARENT=0x0). Setelah itu setiap pemasangan ulang tidak berefek (`prev=0x0`, kadang `err=87`), sehingga owner tetap 0 dan
  dicoba lagi tiap tick selamanya. Log user: owner tetap 0x0 dengan tray baru `0x402E2` dari 17:57:37 sampai 18:24:31 (~27 menit) setelah explorer restart, jadi `TaskbarCreated`/pemasangan ke tray baru TIDAK memulihkan
  (memasang owner lain juga gagal). Yang memulihkan hanya WINDOW BARU (pada uji kami: window baru memasang owner dengan benar saat startup). Bukti: `2026-11-07-owner-diagnostics.md`.
- Perbaikan: verifikasi dengan `GetWindowLongPtr(GWLP_HWNDPARENT)` (bukan `GW_OWNER`), JANGAN pernah memasang ulang bila sudah sama dengan tray, verifikasi setelah pasang,
  log FAILED + `Marshal.GetLastWin32Error()` bila gagal, dan bila gagal pada 3 tick timer berturut-turut (jarak >= 600 ms; hanya tick timer yang dihitung) buat ulang window (maks sekali per 20 detik).
  Setelah perbaikan: buka/tutup Start tidak lagi mengubah owner (tanpa baris `handle/owner changed`).

## Spike v3.2

1. **Pelacakan auto-hide**: bila auto-hide menyala, tiap `EVENT_OBJECT_LOCATIONCHANGE` dan tick timer membaca `GetWindowRect(Shell_TrayWnd)`, menghitung bagian yang terlihat
   di monitor utama (`MonitorFromPoint(0,0, PRIMARY)`, bukan monitor dari HWND taskbar yang tersembunyi; irisan dengan `rcMonitor`). < 50% terlihat -> widget disembunyikan; selain itu ditampilkan dengan Y mengikuti rect taskbar hidup
   (dock di tengah vertikal rect saat itu), sehingga widget ikut meluncur. Saat disembunyikan karena auto-hide, re-layout UIA tidak dijalankan (layout baik terakhir
   dipertahankan, tanpa spam `REJECTED`). Transisi dicatat sekali: `auto-hide: taskbar OFF-SCREEN ...` / `VISIBLE ...`.
2. **Lifecycle & owner**: lihat akar masalah di atas. Window yang dibuat ulang: instance lama berhenti total (`Teardown`: timer + tick handler, kedua WinEvent hook, hook WndProc;
   semua handler cek `_dead` dan tidak mencatat apa pun), lalu `Close()` setelah window baru tampil. Log memuat id instance (GUID pendek, `inst=`) dan `hwnd=`.
3. **Hide debug info**: item menu kanan "Hide debug info" (checkable); bila dicentang widget hanya menampilkan placeholder statis `▶ ⚡ 🔄 ⏹` (Full), `▶ ⚡ 🔄` (Compact),
   `▶ ⚡` (ButtonsOnly) plus tooltip. Disimpan di `spike-settings.json` (`hideDebug`). Teks hanya ditulis bila berubah. Fitur ini HANYA untuk spike.
   Catatan: "Reset to auto position" kini menyimpan `mode=auto` (bukan menghapus file) supaya `hideDebug` tidak hilang.
4. **Startup/recreate**: `SourceInitialized` terjadi di dalam `Show()` SEBELUM `ShowWindow(SW_SHOWNA)` milik WPF. Reassert pertama kini dijalankan lewat `Dispatcher.BeginInvoke(..., Loaded)` setelah window benar-benar tampil,
   keputusan sembunyi/tampil memakai `IsWindowVisible(hwnd)` (keadaan nyata), bukan flag `_hidden`; window dimulai di (-32000,-32000) agar tidak berkedip di posisi default. Tanpa ini, spike yang dijalankan saat auto-hide menyala/fullscreen akan tampil di posisi default padahal log menulis visible=False.
5. Recreate saat mode drag aktif: drag diakhiri dan dicatat (`unsaved drag state dropped`). Hasil query UIA latar tidak diterapkan bila instance sudah mati atau taskbar auto-hide tersembunyi.
6. Hook uji spike: `--simulate-owner-failure` (instance pertama tidak memasang owner, menguji jalur gagal -> buat ulang).

Pengamatan otomatis v3.2 (build ke direktori scratch; 1920x1080, DPI 96, rata tengah):
- Auto-hide dinyalakan lewat `SHAppBarMessage(ABM_SETSTATE)` (state asli dibaca dulu = 0, dicatat, dipulihkan = 0, dikonfirmasi `FINAL=0`; total ~20 detik):
  `auto-hide: taskbar OFF-SCREEN -> hide widget, UIA re-layout paused vis=29% tbRect=(0,1066,1920,1114)`; Y widget ikut turun (1037, 1046, 1057, 1070 ... 1082) lalu `visible=False`;
  tanpa baris `REJECTED` selama tersembunyi. Dimatikan: `auto-hide: taskbar VISIBLE -> show widget`.
- Tombol Win (Start terbuka/tutup): owner tetap `verified`, tanpa pemasangan ulang.
- Explorer di-restart sekali (`Stop-Process -Name explorer`, disetujui user): `AttachToTaskbar(handle/owner changed) ... tray=0x60734 owner=0x60734 verified (before=0x0)`, instance sama. CATATAN: ini pada window yang owner-nya masih sehat; log user (owner sudah 0 sebelum restart) menunjukkan pemasangan ke tray baru gagal sampai ~27 menit. Pemasangan ulang saja tidak cukup; wajib verifikasi + buat ulang window.
- `--simulate-owner-failure`: `FAILED to set owner ... lastError=0 consecutiveFails=1`, lalu `widget window destroyed inst=3f9e35 (owner attach failed 3x, tick timer) -> old instance stopped, recreating`;
  instance baru `inst=b76562` owner `verified`; 0 baris log dari instance lama setelahnya.
- `spike-settings.json` dengan `hideDebug=true`: log `settings: loaded ... hideDebug=True`; tangkapan layar menunjukkan placeholder statis.
- Spike dijalankan dengan auto-hide ON dan taskbar sudah tersembunyi: `auto-hide: taskbar OFF-SCREEN ... [startup]`, `visible=False`, tangkapan layar menunjukkan widget tidak tampil; setelah kursor ke tepi bawah `VISIBLE -> show widget` dan widget tampil. State auto-hide asli (0) dipulihkan (FINAL=0).
- Belum diuji otomatis: klik item menu "Hide debug info" (butuh mouse), auto-hide lewat jendela Settings Windows, multi-monitor.

## Checklist manual v3.2 (pakai ini; menggantikan checklist v3.1)

| Uji | Hasil |
|---|---|
| Taskbar rata tengah | OK (user, 2026-11-07) |
| Taskbar rata kiri (ganti saat widget berjalan: harus pindah dalam ~1 detik) | OK (user, 2026-11-07) |
| Klik Start / Search: widget tetap terlihat? | OK (user, 2026-11-07) |
| Buka Start: widget tidak pindah/menutupi tray | OK (user, 2026-11-07) |
| Klik tombol aplikasi: widget tetap terlihat? | OK (user, 2026-11-07) |
| Buka Action Center / Quick Settings: widget tetap terlihat? | OK (user, 2026-11-07) |
| Menu klik kanan tampil di atas widget | OK (user, 2026-11-07) |
| Auto-hide taskbar menyala | OK (user, 2026-11-07) |
| Video YouTube fullscreen: widget hilang, muncul lagi setelah keluar? | OK (user, 2026-11-07) |
| F11 di browser: widget hilang, muncul lagi setelah keluar? | OK (user, 2026-11-07) |
| DPI 100% | OK (user, 2026-11-07) |
| DPI 125% | OK (user, 2026-11-07) |
| DPI 150% | OK (user, 2026-11-07) |
| Tambah/tutup aplikasi sampai taskbar penuh: area kosong terhitung ulang | OK (user, 2026-11-07) |
| Bekukan UI thread widget (klik kanan widget > "Freeze 5s (debug)"): taskbar tetap bisa diklik? | OK (user, 2026-11-07) |
| Alt+Tab / Task View: widget tidak ikut hilang? | OK (user, 2026-11-07) |
| Explorer restart (Task Manager > Restart): widget kembali terpasang/dibuat ulang | OK (user, 2026-11-07) |
| Drag widget: bisa digeser horizontal | OK (user, 2026-11-07) |
| Uncheck Drag widget: posisi terkunci & bertahan setelah spike ditutup-buka | OK (user, 2026-11-07) |
| Reset to auto position | OK (user, 2026-11-07) |
| Mode manual + fullscreen/auto-hide tetap jalan | OK (user, 2026-11-07) |
| Bersebelahan dengan TrafficMonitor | OK (user, 2026-11-07) |
| Reset to auto saat Drag widget aktif: widget tetap tampil & stabil | OK (user, 2026-11-07) |
| Taskbar penuh: widget mengecil (260→120) lalu tetap tampil dengan pesan | OK (user, 2026-11-07) |
| Hover tombol aplikasi (thumbnail muncul): widget tidak berkedip/pindah | OK (user, 2026-11-07) |
| Working set spike (Task Manager, MB) | tidak dilaporkan (angka belum diukur) |
| Auto-hide: widget ikut sembunyi/muncul bersama taskbar (ikut slide) | OK (user, 2026-11-07) |
| Explorer restart: owner terpasang lagi (log owner≠0x0) | OK (user, 2026-11-07) |
| Hide debug info: teks widget diam | OK (user, 2026-11-07) |
| Auto-hide ON sebelum spike dijalankan: widget tetap tersembunyi sampai taskbar muncul | OK (user, 2026-11-07) |
| Hover/klik widget saat taskbar auto-hide sedang muncul: taskbar tidak langsung sembunyi lagi | OK (user, 2026-11-07) |

Gerbang keputusan Task 1: LULUS (user, 2026-11-07)

## Checklist manual v3.1 (tidak dipakai lagi, digantikan checklist v3.2)

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
| Reset to auto saat Drag widget aktif: widget tetap tampil & stabil | BELUM DIUJI (user) |
| Taskbar penuh: widget mengecil (260→120) lalu tetap tampil dengan pesan | BELUM DIUJI (user) |
| Hover tombol aplikasi (thumbnail muncul): widget tidak berkedip/pindah | BELUM DIUJI (user) |
| Working set spike (Task Manager, MB) | BELUM DIUJI (user) |

## Checklist manual v3 (tidak dipakai lagi, digantikan checklist v3.1)

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

## Catatan untuk Task 13 (aturan v2 + v3 + v3.1 + v3.2)

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
  tidak ada gap muat = BUKAN hide (lihat v3.1 di bawah). Filter elemen UIA milik ProcessId sendiri (widget yang di-own muncul di pohon UIA taskbar).
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
- **Settings (v3 fix)**: tulis settings secara atomik (`AtomicFile` sudah ada di Core), dan validasi `offsetFromRight`
  saat load (negatif atau sangat besar -> reset ke auto). Reset menghapus offset manual (spike menghapus file
  `spike-settings.json`; di produksi cukup mengosongkan `widget.offsetFromRight`).
- **Failsafe (v3 fix)**: bila flag penangguhan menu aktif tetapi `ContextMenu.IsOpen` false di tick 600 ms, flag dibersihkan
  (log). `LostMouseCapture` mengakhiri drag dengan bersih (X terakhir dipertahankan). Mode auto tanpa layout UIA valid
  saat startup: dock di kiri `TrayNotifyWnd` (kiri - 8 - lebar) sampai layout baik datang.
- Gerbang keputusan (Step 6) ditentukan controller/user setelah checklist v3 diisi.
- **Ukuran bertingkat & tidak-muat (v3.1)**: iterasi Full 300 -> Compact 260 -> ButtonsOnly 120; per ukuran pilih gap muat paling kanan; ukuran
  pertama yang menemukan gap menang. `WidgetLayoutPolicy.Choose` mengembalikan `null` bila tidak ada yang muat: di mode auto, `null` berarti
  "tetap tampil di X valid terakhir (atau kiri TrayNotifyWnd) dalam ukuran ButtonsOnly + pesan 'Taskbar penuh — gunakan Drag widget' + log",
  BUKAN sembunyi. Sembunyi hanya untuk fullscreen dan auto-hide (kalau tidak, user tidak bisa mencapai menu klik kanan dan terkunci).
- **Filter elemen UIA (v3.1, revisi)**: lihat butir 3 "Spike v3.1" (a-e: di dalam rect taskbar, tinggi >= 50%, lebar >= 8, bukan proses `DockingSpike`/`FlutterBar.App`,
  bukan transien). Jangan memakai daftar putih tombol dan jangan menghitung elemen proses lain tanpa filter itu. Log tiap (class, id, controlType, pid) baru sekali.
- **Hook uji hanya untuk spike**: `--force-min-gap` / `ForceMaxGap` dan `--force-empty-after` ditandai `// SPIKE-ONLY test hook`; JANGAN disalin ke produksi.
- **Max-hold (v3.1)**: kandidat berbeda yang ditahan >= 5 query berturut-turut (walau berganti-ganti) -> terapkan kandidat terbaru (log `max-hold`).
- **Debounce (v3.1)**: terapkan penempatan baru hanya bila (ukuran, x) yang sama (toleransi 3 px) keluar dari 2 query UIA berturut-turut; layout pertama
  setelah startup / `ResetToAuto()` / `TaskbarCreated` diterapkan langsung; log kandidat yang ditahan. `ResetToAuto()` saat drag aktif harus keluar dari mode drag dulu.
- **Visibilitas auto-hide (v3.2)**: bila auto-hide menyala, pada tiap `EVENT_OBJECT_LOCATIONCHANGE` taskbar dan tiap tick timer baca `GetWindowRect(Shell_TrayWnd)`, hitung bagian yang terlihat
  di monitor utama (`MonitorFromPoint(0,0, MONITOR_DEFAULTTOPRIMARY)`, atau monitor yang di-cache saat taskbar terlihat; JANGAN `MonitorFromWindow` taskbar yang tersembunyi) (irisan dengan `rcMonitor` dari `GetMonitorInfo`). < 50% terlihat -> sembunyikan widget; selain itu tampilkan dan taruh Y di tengah vertikal rect taskbar saat itu
  (widget ikut meluncur). Jangan jalankan re-layout UIA selagi tersembunyi oleh auto-hide (pertahankan layout baik terakhir, jangan spam log); log sekali per transisi.
  Posisi Y selalu dari rect taskbar hidup, bukan dari rect hasil query UIA yang basi.
- **Aturan lifecycle window dibuat ulang (v3.2)**: window lama harus berhenti total sebelum yang baru dibuat (stop timer, lepas semua WinEvent hook dan hook WndProc, semua handler
  cek flag mati dan tidak menulis log), `_hwnd` selalu handle window hidup instance itu, dan `Close()` window lama setelah yang baru tampil. Log memuat id instance agar zombie terlihat.
- **Verifikasi owner (v3.2)**: verifikasi owner dengan `GetWindowLongPtr(hwnd, GWLP_HWNDPARENT) == Shell_TrayWnd`, BUKAN `GetWindow(GW_OWNER)` (mengembalikan 0 selama Start/Search terbuka).
  JANGAN memasang ulang bila sudah sama (itu menghapus owner). Setelah `SetWindowLongPtr` verifikasi hasilnya; bila tidak sama log FAILED dengan `Marshal.GetLastWin32Error()`,
  dan bila gagal berulang (3 tick timer berturut-turut) buat ulang window (dibatasi, mis. maks sekali per 20 detik).
  `TaskbarCreated` + pemasangan ulang owner SAJA tidak cukup: log user menunjukkan owner tetap 0 selama ~27 menit dengan tray baru; hanya window baru yang memulihkan. Wajib verifikasi + buat ulang.
- **Debug info hanya untuk spike (v3.2)**: teks debug widget, item "Hide debug info", dan hook `--simulate-owner-failure` TIDAK dibawa ke produksi.
- **Visibilitas saat startup/recreate (v3.2 fix)**: `SourceInitialized` datang sebelum WPF menampilkan window, jadi jangan memutuskan hide/show di sana. Jalankan Reassert pertama setelah window tampil
  (`Dispatcher.BeginInvoke(..., DispatcherPriority.Loaded)` atau `ContentRendered`), dan putuskan sembunyi/tampil dari `IsWindowVisible(hwnd)` (keadaan nyata), bukan dari flag cache. Mulai di luar layar
  agar tidak berkedip. Saat window dibuat ulang selagi mode drag aktif, akhiri drag dan log bahwa state drag yang belum disimpan dibuang.

## Build produksi (Task 13)

Teknik spike v3.2 dipindah ke `src/FlutterBar.App` (`Docking/TaskbarDocking*.cs`, `Docking/TaskbarProbe.cs`, `Docking/FullscreenDetector.cs`,
`Widget/TaskbarWidget.xaml`), aturan murni di `src/FlutterBar.Core/Docking/` (dengan unit test). Log ke `%APPDATA%\FlutterBar\diag.log`.
Jalankan: `src\FlutterBar.App\bin\Debug\net10.0-windows\FlutterBar.App.exe`; tutup lewat klik kanan widget > "Keluar".
Posisi manual tersimpan di `%APPDATA%\FlutterBar\settings.json` (`widget.offsetFromRight`, `widget.widthOverride`).

Perbedaan dengan spike: tidak ada teks debug, item "Hide debug info", "Freeze 5s (debug)", maupun hook uji. Lebar tiap ukuran diukur dari
layout nyata (`Measure`), bukan 300/260/120 tetap. Fullscreen = aturan rect spike ATAU `SHQueryUserNotificationState` BUSY/D3D (foreground non-shell
di monitor taskbar). Resize tepi kiri hanya aktif saat "Drag widget" dicentang.

Pengamatan otomatis (agen, 2026-11-07; 1920x1080, DPI 96, taskbar rata tengah, auto-hide mati):
- Start: `owner attached (startup) ... verified (before=<owner tersembunyi WPF>)`; `GetWindowLongPtr(GWLP_HWNDPARENT)` = `Shell_TrayWnd`, ex-style `0x08000088`
  (TOOLWINDOW|NOACTIVATE|TOPMOST), widget tampil, tanpa log FAILED/REJECTED selama 2 menit idle.
- Lebar terukur (DIP, label default "Pilih project/config/device"): Full 437, Compact 263-271, ButtonsOnly 62. Gap `[166..663]=497 [1257..1666]=409`:
  Full (437) tidak muat di gap kanan, jadi aturan v3.1 (ukuran dulu, lalu gap paling kanan) menaruh Full di gap KIRI (x=226). Dengan `widget.size=compact`:
  Compact di 1666-271=1395 (dekat tray); `buttonsOnly`: x=1604.
- `widget.offsetFromRight=500` (tulis tangan): mode manual, x = 1920-500-437 = 983 (OK). `offsetFromRight=-5`: diabaikan saat load -> auto.
- Working set setelah 2 menit idle: 121,4 MB (render GPU, GC concurrent) -> 104,9 MB Debug / 105,8 MB Release setelah `RenderMode.SoftwareOnly` +
  `ConcurrentGarbageCollection=false` (private bytes 52 MB). Target spec < 100 MB BELUM tercapai. CPU idle ~1,6% satu core (query UIA ~1x/1,2 detik).
- Tidak diuji otomatis: Start/Search, fullscreen, auto-hide, restart explorer, drag/resize/menu (butuh mouse), DPI lain, tema terang.

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
| Taskbar tetap bisa diklik saat widget berjalan (produksi tidak punya item "Freeze 5s"; UI thread tidak pernah blocking) | BELUM DIUJI (user) |
| Alt+Tab / Task View: widget tidak ikut hilang? | BELUM DIUJI (user) |
| Explorer restart (Task Manager > Restart): widget kembali terpasang/dibuat ulang | BELUM DIUJI (user) |
| Drag widget: bisa digeser horizontal | BELUM DIUJI (user) |
| Uncheck Drag widget: posisi terkunci & bertahan setelah app ditutup-buka | BELUM DIUJI (user) |
| Reset to auto position | BELUM DIUJI (user) |
| Mode manual + fullscreen/auto-hide tetap jalan | BELUM DIUJI (user) |
| Bersebelahan dengan TrafficMonitor | BELUM DIUJI (user) |
| Reset to auto saat Drag widget aktif: widget tetap tampil & stabil | BELUM DIUJI (user) |
| Taskbar penuh: widget mengecil (Full→Compact→ButtonsOnly) lalu tetap tampil dengan pesan (tooltip + border oranye) | BELUM DIUJI (user) |
| Hover tombol aplikasi (thumbnail muncul): widget tidak berkedip/pindah | BELUM DIUJI (user) |
| Working set FlutterBar.App (Task Manager, MB, idle) | agen: 104,9 MB (Debug) / 105,8 MB (Release) setelah 2 menit idle, target < 100 MB belum tercapai; user: BELUM DIUJI (user) |
| Auto-hide: widget ikut sembunyi/muncul bersama taskbar (ikut slide) | BELUM DIUJI (user) |
| Explorer restart: owner terpasang lagi (diag.log `owner attached` / tanpa `FAILED`) | BELUM DIUJI (user) |
| Teks widget diam (tidak berubah sendiri tiap detik) | BELUM DIUJI (user) |
| Auto-hide ON sebelum app dijalankan: widget tetap tersembunyi sampai taskbar muncul | BELUM DIUJI (user) |
| Hover/klik widget saat taskbar auto-hide sedang muncul: taskbar tidak langsung sembunyi lagi | BELUM DIUJI (user) |
| Resize tepi kiri (saat "Drag widget" dicentang) menyimpan lebar dan bertahan setelah app ditutup-buka | BELUM DIUJI (user) |
| Tombol & chip tampil sesuai ukuran (Full/Compact/ButtonsOnly) | BELUM DIUJI (user) |
| Tema terang/gelap mengikuti Windows | BELUM DIUJI (user) |
