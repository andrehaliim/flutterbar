# Daemon fixtures (Flutter 3.22.3)

Raw stdout of `fvm flutter run --machine -d <device>` recorded by `tools/record-daemon.ps1` against `tests/sample_app`
(pristine `flutter create` template, pinned to 3.22.3 via `.fvmrc`). One line per line, exactly as emitted (UTF-8, no BOM).
stderr is recorded as a sidecar `<name>.stderr.txt` next to each `<name>.jsonl` (empty when nothing was written; the
recorder's `-StderrOut` defaults to that path). Cross-stream ordering between stdout and stderr is NOT captured. Observed
behavior: stderr diagnostics (compiler errors) arrive before the `code:1` response / `app.stop` on stdout.
`web-reload-unsupported` is effectively identical to `reload-ok` (web turns reload into restart, code 0), so Task 9 needs a
hand-built "reload rejected" response for that case.

## Device used

The Pixel_9 AVD booted (as `emulator-5554`) but the debug APK install failed with
`INSTALL_FAILED_INSUFFICIENT_STORAGE` (the AVD /data has ~440 MB free of 5.8 GB and holds the user's own apps; not touched).
`windows` has no Visual Studio toolchain ("Unable to find suitable Visual Studio toolchain"). Fallback per ruling:
**every fixture was recorded on `chrome`** (Chrome 157, web-javascript). Consequently Android-specific output
(Gradle progress, `app.log` events, hot *reload* semantics) is NOT represented here.

## Fixtures

| File | Key content |
|---|---|
| `start-ok.jsonl` | `daemon.connected`, `app.start`, `app.progress` (start/finish), `app.webLaunchUrl`, `app.started`, `app.debugPort`. Process killed after `app.started` + 3 s (no `app.stop`). |
| `reload-ok.jsonl` | start sequence, then request id 1 `app.restart fullRestart:false`: progress `hot.restart`, text `Restarted application in NNNms.`, response `{"id":1,"result":{"code":0,"message":""}}`; then request id 2 `app.stop` -> `Application finished.`, `{"id":2,"result":true}`, `app.stop`. |
| `restart-ok.jsonl` | Same as reload-ok but `fullRestart:true`. |
| `reload-compile-error.jsonl` | `int x = ;` appended to main.dart. Response `{"id":1,"result":{"code":1,"message":"Failed to recompile application."}}`. The compiler diagnostics (`lib/main.dart:127:9: Error: ...`) go to **stderr only**, not stdout. Then `app.stop` request/response. |
| `runtime-exception.jsonl` | `build()` made to throw `StateError('boom')`, then reload (code 0 response). The framework error arrives as **plain NonJson lines** (`══╡ EXCEPTION CAUGHT BY WIDGETS LIBRARY ╞═══…`, `Bad state: boom`, ~780 lines of stack, footer `════…`), NOT as `app.log`/`daemon.logMessage`. ~70 KB. |
| `stop.jsonl` | start sequence, request id 1 `app.stop`: `Application finished.`, `{"id":1,"result":true}`, `app.stop`. |
| `build-fail.jsonl` | Dart syntax error in main.dart at launch (`--flavor` is only a stderr warning on web, so it does not fail; on Android it would). stdout: `daemon.connected`, `app.start`, `Launching ...`, `app.progress` start/finish, then `app.stop` (no `app.started`). The error text (`Error: Expected an identifier...`, `Failed to compile application.`) is on **stderr only**. A session must therefore detect failure by `app.stop`/process exit without `app.started`. |
| `web-reload-unsupported.jsonl` | `chrome`, `fullRestart:false`. Real result: web turns a "reload" into a hot restart (`Performing hot restart...`, `Restarted application`) and returns `code:0`. There is NO "reload unsupported" error in 3.22.3. |

## Actual field names

- `daemon.connected` params: `version` ("0.6.1"), `pid`.
- `app.start` params: `appId`, `deviceId`, `directory`, `supportsRestart` (bool), `launchMode` ("run"), `mode` ("debug"). Both `supportsRestart` and `launchMode` ARE present.
- `app.started` params: `appId`.
- `app.debugPort` params: `appId`, `port`, `wsUri`.
- `app.webLaunchUrl` params: `url`, `launched` (web only; note: no `appId`).
- `app.progress` params: `appId`, `id` (string), `progressId` (null or "hot.restart"), `message` (absent on finish), `finished`.
- `app.stop` params: `appId` only (no `error` observed).
- `app.restart` response: `{"id":N,"result":{"code":0|1,"message":"..."}}`; `message` is `""` on success.
- `app.stop` response: `{"id":N,"result":true}` — `result` is a bare boolean, not an object.
- `app.log` and `daemon.logMessage` did NOT occur in any recording (web prints app output as plain text lines). Their field names are as in the spec (`log`/`error`, `level`/`message`), unverified against real output.
- Non-JSON stdout lines seen: `Launching lib\main.dart on Chrome in debug mode...`, `This app is linked to the debug service: ws://...`, `Debug service listening on ws://...`, `Restarted application in NNNms.`, `Application finished.`, framework exception dumps. (No "new version available" banner appeared.)
- The first fixture of a fresh SDK may contain `Downloading ...` lines on stderr only.