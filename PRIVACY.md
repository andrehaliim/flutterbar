# Privacy

Last updated: 2026-11-14

StatefulBar does not collect, store, or send personal data. It has no account, no telemetry, no analytics, and no ads.

## What StatefulBar keeps on your PC

- **Settings**, in `%APPDATA%\StatefulBar\settings.json`: the folders that hold your Flutter projects, the last
  configuration and device you picked for each project, how the widget looks, and when it last checked for an update.
- **A diagnostic log**, in `%APPDATA%\StatefulBar\diag.log`: what the widget did, to help find problems. It can contain
  the paths of your projects, branch and device names, and error output of the tools it ran. It stays on your PC unless
  you choose to share it.
- **Run configurations**, in `<project>\.statefulbar\launch.json`, inside each project you use it with.

The Microsoft Store version may keep the first two in the app's own data folder instead; Windows removes that folder when
you uninstall the app.

## Connections StatefulBar makes

- **To the app you run.** StatefulBar reads the app's log output from the Dart VM service that `flutter run` starts on
  your own PC. Nothing leaves the PC.
- **To GitHub, to check for updates.** Once a day, and when you choose Updates > Check now, the version downloaded from
  GitHub asks `api.github.com` for the newest release of StatefulBar. GitHub receives your IP address and the name and
  version of the app, as with any web request; StatefulBar sends nothing else and downloads nothing. Turn the daily
  check off with Updates > Check automatically. The Microsoft Store version never makes this request. GitHub's handling
  of the request is covered by the [GitHub Privacy Statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).

The `sbar` command line tool talks to the widget on the same PC and under the same Windows account only.

## Tools StatefulBar starts for you

StatefulBar runs tools you installed yourself: Flutter and Dart (directly or through fvm), git, the Android tools
(`adb`, the emulator), and your terminal or text editor. They run under your Windows account and behave as they do when
you start them yourself. They may use the network, for example to download packages or to fetch and push with git, and
they follow their own privacy policies and settings. StatefulBar does not read or store your git credentials.

## Changes and questions

Changes to this policy are recorded in the history of this file. For questions, open an issue at
<https://github.com/andrehaliim/statefulbar/issues>.
