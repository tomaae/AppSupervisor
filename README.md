# AppSupervisor

AppSupervisor is a lightweight Windows tray application that starts, supervises, restarts, and closes groups of helper applications and Windows services based on whether a configured monitor process is running.

> [!CAUTION]
> **This project was fully created by AI.** Selected behavior has been tested by the project owner, but the code has not received a comprehensive independent human audit. Review it carefully before use—especially because AppSupervisor runs with administrator privileges and can start or stop applications and Windows services.

## Functionality summary

- Activates profiles when their monitored process starts, then processes applications, services, delays, and Home Assistant actions in the configured order.
- Restarts applications or services that stop unexpectedly, with configurable close and restart timeouts.
- Gracefully closes applications by default, with optional force-kill only when explicitly enabled.
- Supports regular executables, Steam applications, Microsoft Store/MSIX applications, and Windows services.
- Provides per-application listener and VRChat OSCQuery health checks, including optional recovery after a confirmed failure.
- Monitors expected SteamVR controllers, trackers, and base stations without starting or controlling SteamVR.
- Sends per-resource alerts through popup dialogs, Windows notifications, or XSOverlay.
- Includes a graphical configuration editor with application, Steam, Store, service, and running-process pickers.
- Validates configuration before applying it and keeps the last valid configuration active if a reload fails.
- Can register itself to start elevated when the user signs in to Windows.

## Detailed functionality

### Profiles and resources

Each profile watches one process. When that process appears, AppSupervisor activates the profile's enabled resources from top to bottom. When it remains absent for the configured **Close timeout**, AppSupervisor closes or stops those resources.

Resources can include:

- Helper applications.
- Windows services.
- Nonblocking delays between startup entries.
- Home Assistant actions.

A resource may depend on one earlier application or service being ready. Profiles operate independently, so a delay or slow resource in one profile does not hold up another. Pausing or exiting AppSupervisor leaves external applications and services untouched.

### Helper applications

Applications can be launched directly from an executable, through Steam, or through a Microsoft Store/MSIX app entry. Direct launches may include command-line arguments.

AppSupervisor identifies helpers by executable path. If multiple independent instances are found, it closes them before starting one fresh instance. Closing is graceful unless **Allow force-kill after all graceful close attempts fail** is enabled.

Per-application options include:

- Restarting after an unexpected exit.
- Keeping the application closed until an active profile needs it.
- Minimizing its windows after launch.
- Detecting unresponsive windows and restarting after repeated failures.
- Choosing notification destinations.

### Windows services

The editor lists installed third-party services by service name. AppSupervisor sets configured services to Manual startup when required, starts them with their profile, optionally restarts them after an unexpected stop, and requests a normal stop when the profile closes.

### Health checks

Health checks belong to a helper application and have their own timing, failure threshold, recovery action, and notification settings.

- **Listener** verifies that the helper owns a configured TCP or UDP port. It can run only while another selected process is active.
- **VRChat OSCQuery** checks VRChat OSCQuery availability and selected avatar parameters. It can also report when most available parameters remain unchanged for too long.

A confirmed failure can optionally trigger a graceful restart of the helper. One-shot tests do not change live failure state or restart external processes.

### Home Assistant

Profiles can run `turn_on`, `turn_off`, and `button.press` actions against compatible entities. Stateful actions can be verified after execution and kept persistent while the profile is active. When the profile closes, `turn_on` and `turn_off` actions are reversed; buttons run only during activation.

Home Assistant uses a shared URL and long-lived access token. The token is stored in the local configuration files, so those files must be treated as credentials.

### SteamVR device monitoring

AppSupervisor can monitor configured controllers, trackers, and Lighthouse/base-station devices while SteamVR is already running. Discovery records controller handedness and SteamVR tracker assignments such as left foot, left knee, or waist, so offline and recovery notifications identify the missing role. It does not start or restart SteamVR and does not control devices.

After repeated connection failures, it sends the selected notifications and shows an offline-device window. Reminders can be silenced for the current outage; recovery is detected automatically.

Generic/FBT trackers are monitored only after SteamVR reports them connected at least once in the current SteamVR session. A tracker intentionally left powered off for the whole session therefore does not produce an offline alert. Hand controllers and tracking references such as base stations remain mandatory from session startup.

### Notifications

Applications, services, health checks, Home Assistant actions, and SteamVR devices can report through:

- Popup dialogs.
- Windows notifications.
- XSOverlay notifications.

If XSOverlay is unavailable, its notifications fall back to Windows notifications.

### Configuration editor

Open the editor from **Configure...** in the tray menu or by double-clicking the tray icon. It supports adding, duplicating, removing, enabling, reordering, and configuring profiles and resources.

The editor provides pickers for running processes, executables, Steam applications, Microsoft Store applications, Windows services, and SteamVR devices. Integrations such as Home Assistant and SteamVR monitoring are configured globally rather than inside a profile.

**Validate** checks the complete configuration without saving. **Save & Apply** validates and writes it, then replaces the running configuration. If the new configuration cannot be applied, the previous valid configuration remains active.

### Administrator and startup behavior

AppSupervisor requires administrator privileges to manage configured services consistently. It prevents multiple supervisor instances and can create a current-user scheduled task that launches it with elevated privileges at sign-in.

## Configuration storage

Configuration is stored in `config.json` beside `AppSupervisor.exe`. If the file is missing, AppSupervisor creates an empty valid configuration. The last verified configuration is also saved as `config.json.old` during normal shutdown.

Both files are excluded from Git and omitted from release packages. Do not share or commit them if they contain a Home Assistant access token.

## Running a packaged build

The release package is Windows x64 and framework-dependent. Install the **.NET 10 Desktop Runtime**, keep all packaged files together, then run `AppSupervisor.exe` and approve the UAC prompt. AppSupervisor runs in the notification area rather than opening a main window.

The package contains:

```text
AppSupervisor.exe
AppSupervisor.NotificationHost.exe
LICENSE
THIRD-PARTY-NOTICES.txt
```

## Building from source

### Requirements

- Windows on x64 hardware.
- .NET 10 SDK.
- PowerShell for the packaging script.
- Administrator access for runtime integration testing involving services.

### Restore, test, and build

From the repository root:

```powershell
dotnet restore .\AppSupervisor.slnx --runtime win-x64
dotnet test .\AppSupervisor.slnx --configuration Release --no-restore
dotnet build .\AppSupervisor.slnx --configuration Release --no-restore
```

### Create a release package

```powershell
.\Publish.ps1
```

The script restores the solution, runs the Release tests, publishes the Windows x64 executables, audits the output, and creates:

```text
artifacts/AppSupervisor/
artifacts/AppSupervisor-win-x64.zip
```

## License

Copyright 2026 Tomaae.

Licensed under the [Apache License 2.0](LICENSE).
