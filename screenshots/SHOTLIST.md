# Screenshot shot list

The framed pictures in `docs/images/` are rendered from raw captures of the running widget:

```
python screenshots/generate.py
```

It reads `screenshots/raw/en/*.png`, writes `screenshots/framed/en/*.png` (1920x1080), and needs Chrome or Edge.
Copy the results you want to publish to `docs/images/`. `raw/` and `framed/` are ignored by git.

Captions, colours, and the frame size are in `config.json`; the frame layout is `custom/desktop.html`.

## Setup for every shot

- Project `tests/sample_app` (Flutter 3.22.3 through fvm), branch `main`, a `default` run configuration, device Chrome.
  No real project names, branches, or device serials.
- Windows at 100% scale, widget theme Dark, size Full, background opacity 100%.
- Capture only StatefulBar's own windows, never a screen region: other windows and the wallpaper must not end up in
  a picture. Keep the mouse away from the widget so no hover effect is captured.
- When the taskbar is too crowded for Full while the app runs, use "Drag widget" to move it to a wider free strip.

## Raw files

| File | What to capture | State |
|---|---|---|
| `widget_idle.png` | The widget window | Idle, device list already loaded (chip shows `Chrome`) |
| `widget_running.png` | The widget window | Running |
| `widget_error.png` | The widget window | Running, after a hot reload that throws (red dot) |
| `01_widget.png` | Copy of `widget_running.png` | The frame shows the widget on the taskbar only |
| `02_cli.png` | A console after `sbar status`, `sbar reload`, `sbar logs --errors --since-last-reload` | Without the title bar and the empty lower part |
| `03_log.png` | The log window, scrolled to the top | Without the title bar; one clean hot reload, then one that throws |
| `04_devices.png` | The device flyout | Idle, list loaded |
| `05_git.png` | The branch flyout | Idle, a commit message typed |
| `06_tools.png` | The Flutter tools flyout | Idle |
| `07_sizes.png` | Full, Compact, Buttons only, each Dark then Light, stacked with a transparent background | Running |
| `08_menu.png` | The right-click menu with the Style submenu open, on a transparent background | Idle |

For the hot reload that throws, add `throw StateError('Counter overflow');` as the first line of
`_MyHomePageState.build` in `tests/sample_app/lib/main.dart`, hot reload, and restore the file afterwards.

The three `widget_*.png` files are also used by every frame for the widget on the drawn taskbar (`images` in
`config.json`).
