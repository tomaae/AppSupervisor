# AppSupervisor Stream Deck plugin

This companion plugin provides one **Status** action. It mirrors AppSupervisor's tray icon and
short status text on a Stream Deck key; pressing the key opens the AppSupervisor configuration
editor.

The integration is event-driven. AppSupervisor pushes deduplicated status changes through a
current-user-only Windows named pipe. The plugin performs no status polling and shares one pipe
connection across all visible Status action instances. If AppSupervisor is not running, the key
shows **Offline** and reconnect attempts back off to once per minute; Stream Deck's application
launch event triggers an immediate connection attempt.

## Build and package

Install Node.js 24 or later, then run from the repository root:

```powershell
.\StreamDeckPlugin\Build.ps1
```

The script installs the locked dependencies, runs the protocol tests, builds the plugin, validates
it with Elgato's CLI, and writes the installable package to:

```text
artifacts/StreamDeck/com.tomaae.appsupervisor.streamDeckPlugin
```

The generated `bin` directory and installer artifact are intentionally excluded from Git.
