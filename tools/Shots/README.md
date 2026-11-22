# Shots

Renders every launcher page to PNG without a screen (Avalonia headless + Skia), for checking the UI.

    dotnet run --project tools/Shots -- <out folder> [width height]

It starts the launcher's MainWindow like the real app: it needs a `launcher.env` next to the built Shots
(SERVER_URL pointing at a copy of the release folder with a manifest.json, ALLOW_HTTP=1 for a local
`python -m http.server`) and a settings.json in the launcher's settings folder (HOME / XDG_CONFIG_HOME on Linux)
with a WowFolder. Remove "launcher" from that manifest.json so it doesn't try to update itself.
