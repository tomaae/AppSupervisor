# AppSupervisor Stream Deck plugin

This companion plugin provides two actions:

- **Status** mirrors AppSupervisor's tray icon and short status text; pressing the key opens the
  configuration editor.
- **Launch monitored app** launches the monitored executable of a selected enabled,
  process-triggered profile. If that process is already running, AppSupervisor leaves it alone.

Choose the launch action's profile in its property inspector. Newly browsed or picked monitored
processes retain their full executable path so they can be launched reliably; existing configurations
that contain only a filename continue to work when Windows can resolve that executable. The plugin
sends only the selected profile ID. Elevated AppSupervisor validates the ID against its active
configuration and performs the launch, so the plugin cannot supply an arbitrary executable path.

AppSupervisor's separate profile-managed Stream Deck actions use Stream Deck's official MCP Actions
integration and Elgato MCP Server; they are not implemented by this plugin.

The integration is event-driven. The unelevated plugin hosts a current-user Windows named pipe and
elevated AppSupervisor connects to it, pushing only deduplicated status changes. The plugin performs
no status polling and shares one pipe connection across all companion actions. If AppSupervisor is
not running, its keys show **Offline**. The pipe connection is the sole
online/offline signal, avoiding false state changes from elevation-launcher process events.
AppSupervisor waits for the pipe without consuming CPU and reconnects after either application
restarts.

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

Interactive runs wait for Enter before closing so the final output remains visible. Automated
callers can use `.\StreamDeckPlugin\Build.ps1 -NoPause`; redirected CI runs never pause.

The generated `bin` directory and installer artifact are intentionally excluded from Git.
