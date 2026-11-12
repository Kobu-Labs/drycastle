DRYCASTLE LAUNCHER

1. Unzip this folder anywhere and start the launcher:
   Windows: DrycastleLauncher.exe   (if "Windows protected your PC" appears: More info, Run anyway)
   Linux:   chmod +x DrycastleLauncher.x86_64 && ./DrycastleLauncher.x86_64
   macOS:   the app is not signed by Apple, so Gatekeeper blocks the first start. Either run once in Terminal (cd into the unzipped folder first):
              xattr -dr com.apple.quarantine "Drycastle Launcher.app"
            or open it, dismiss the warning, then System Settings > Privacy & Security > Open Anyway.
            (The game itself downloads without this prompt.)
2. On first start it downloads the game and the map (about 100 MB). Later it only downloads what changed.
3. Press Play. In the game, enter the server address on the connect screen (it is remembered).

Settings (in the launcher): update channel (Stable or Dev), automatic updates, UI scale.
The game is installed to %LOCALAPPDATA%\Drycastle (Windows) or ~/.local/share/Drycastle (Linux) or ~/Library/Application Support/Drycastle (macOS). Delete that folder to uninstall.
If the launcher cannot reach the internet you can still play the installed version.
