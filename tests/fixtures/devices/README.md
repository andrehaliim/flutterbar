# Device fixtures

Captured with `fvm flutter` 3.22.3 in `tests/sample_app`.

- `emulators.txt`: `flutter emulators` captured BEFORE launching any emulator: one AVD, `Pixel_9 • Pixel 9 • Google • android`.
- `devices-machine.json`: `flutter devices --machine` (stdout only, no banner) captured WHILE Pixel_9 was running and booted
  (`emulator-5554`, "sdk gphone64 x86 64"), together with `windows`, `chrome`, `edge`.
- `emulatorId` does NOT appear in the `--machine` output in 3.22.3 (keys: `name`, `id`, `isSupported`, `targetPlatform`,
  `emulator` (bool), `sdk`, `capabilities`). A running emulator device can therefore not be mapped back to its AVD id
  (`Pixel_9`) from this output; `emulator-5554` can only be matched to the AVD by other means (e.g. `adb emu avd name`).
- Before launch only `windows`, `chrome`, `edge` were connected; Pixel_9 was offline (not listed in `flutter devices`).
- The emulator was started by the recording session and shut down afterwards.