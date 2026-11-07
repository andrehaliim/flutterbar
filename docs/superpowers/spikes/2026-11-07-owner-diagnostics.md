# Bukti diagnostik akar masalah owner (spike v3.2, 2026-11-07)

Sumber 1: `spikes/DockingSpike/bin/Debug/net10.0-windows/spike.log` milik user (file mentah MASIH ada, 1697 baris, terakhir diubah 18:24). Sumber 2: log build diagnostik sementara di direktori scratch
(`%TEMP%\ds1\spike.log`); log mentah itu sudah dihapus saat dibersihkan, jadi kutipan di bagian B diambil dari output yang tercatat di sesi, dan klaim di sana ditulis sebagai "teramati".

## A. Log user (bukan ulang-uji)

Owner awal sehat, lalu hilang tanpa window dibuat ulang:

```text
17:55:15.747 AttachToTaskbar(startup): tray=0x40D0C owner=0x40D0C
17:56:14.409 reassert: foreground:Shell_TrayWnd
17:56:14.958 AttachToTaskbar(handle/owner changed): tray=0x40D0C owner=0x0
17:56:14.958 SHQueryUserNotificationState=7 fullscreenRule=False autoHide=False
17:56:14.961 UIA layout REJECTED (tray elements=False, occupied=0, elements=1, TrayNotifyWnd.left=1674) -> keeping last good layout
17:56:17.402 AttachToTaskbar(handle/owner changed): tray=0x40D0C owner=0x0
17:56:17.851 reassert: foreground:Windows.UI.Core.CoreWindow
```

Explorer restart baru terjadi ~80 detik kemudian; owner sudah 0 dan tidak pulih dengan tray baru:

```text
17:57:35.562 AttachToTaskbar(handle/owner changed): tray=0x40D0C owner=0x0
17:57:36.831 location hook (handle/owner changed): tray=0x402E2 pid=13532 tid=408 locHook=ok
17:57:37.565 AttachToTaskbar(handle/owner changed): tray=0x402E2 owner=0x0
17:57:38.813 TaskbarCreated received (explorer restarted)
...
18:24:31.675 AttachToTaskbar(handle/owner changed): tray=0x402E2 owner=0x0
```

`grep -c "recreating\|destroyed"` pada log ini = 0 (window tidak pernah dibuat ulang). Baris `owner=0x0` ada 805 kali.

## B. Reproduksi (instance tunggal, tombol Win), build diagnostik

Teramati: selama Start terbuka `GW_OWNER` = 0 sementara `GWLP_HWNDPARENT` = tray.

```text
18:50:05.870 TEST: Win key open
18:50:06.296 EXP readback differs: GW_OWNER=0x0 GWLP_HWNDPARENT=0x1C0A10 reason=timer qunsFg=Windows.UI.Core.CoreWindow
18:50:08.745 EXP readback differs: GW_OWNER=0x0 GWLP_HWNDPARENT=0x1C0A10 reason=timer qunsFg=Windows.UI.Core.CoreWindow
```

Teramati: kode v3.1 (memasang ulang owner yang sama) menghapus owner, lalu pemasangan selanjutnya tidak berefek:

```text
18:48:21.802 AttachToTaskbar(handle/owner changed) DIAG inst=267d7b _hwnd=0xB036E wpfHwnd=0xB036E isWindow=True prev=0x1C0A10 err=0 tray=0x1C0A10 owner=0x0
18:48:24.244 AttachToTaskbar(handle/owner changed) DIAG inst=267d7b ... prev=0x0 err=87 tray=0x1C0A10 owner=0x0
18:48:29.114 AttachToTaskbar(handle/owner changed) DIAG inst=267d7b ... prev=0x0 err=0 tray=0x1C0A10 owner=0x0
18:48:53.499 AttachToTaskbar(handle/owner changed) DIAG inst=267d7b ... prev=0x0 err=87 tray=0x1C0A10 owner=0x0   (masih gagal ~30 detik setelah Start ditutup)
```

Teramati pada keadaan gagal (varian di build diagnostik): `widgetTopmost=True trayTopmost=True widgetVisible=True trayVisible=True gwlpParent=0x0 hung=False`;
varian NOTOPMOST lalu set, dan varian clear-then-set, keduanya `prev=0x0 err=0 owner=0x0` (tidak berefek).

Teramati setelah perbaikan (verifikasi GWLP_HWNDPARENT, tanpa pasang ulang bila sama): buka/tutup Start tidak menghasilkan baris `handle/owner changed`; owner tetap `verified`.
Mekanisme internal Windows yang membuat pemasangan owner sama menghapus owner tidak didokumentasikan; ini perilaku teramati.
