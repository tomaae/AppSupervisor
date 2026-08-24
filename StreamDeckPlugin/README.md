# AppSupervisor Stream Deck plugin

This companion plugin provides one **Status** action. It mirrors AppSupervisor's tray icon and
short status text on a Stream Deck key; pressing the key opens the AppSupervisor configuration
editor.

This plugin is only for the tray-style Status key. AppSupervisor's standalone Stream Deck profile
actions use Stream Deck's official MCP Actions integration and Elgato MCP Server; they are not
implemented by this plugin.

The integration is event-driven. The unelevated plugin hosts a current-user Windows named pipe and
elevated AppSupervisor connects to it, pushing only deduplicated status changes. The plugin performs
no status polling and shares one pipe connection across all visible Status action instances. If
AppSupervisor is not running, the key shows **Offline**; AppSupervisor waits for the pipe without
consuming CPU and reconnects after either application restarts.

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
