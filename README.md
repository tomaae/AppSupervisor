# AppSupervisor
![GitHub release (latest by date)](https://img.shields.io/github/v/release/tomaae/AppSupervisor?style=plastic)
![Project Stage](https://img.shields.io/badge/project%20stage-development-yellow.svg?style=plastic)
![GitHub all releases](https://img.shields.io/github/downloads/tomaae/AppSupervisor/total?style=plastic)

![GitHub commits since latest release](https://img.shields.io/github/commits-since/tomaae/AppSupervisor/latest?style=plastic)
![GitHub commit activity](https://img.shields.io/github/commit-activity/m/tomaae/AppSupervisor?style=plastic)
![GitHub Workflow Status](https://img.shields.io/github/actions/workflow/status/tomaae/AppSupervisor/ci.yml?style=plastic)

AppSupervisor is a lightweight Windows tray application that starts, supervises, restarts, and closes groups of helper applications and Windows services based on whether a configured monitor process is running.

> [!CAUTION]
> **This project was fully created by AI.** Selected behavior has been tested by the project owner, but the code has not received a comprehensive independent human audit. Review it carefully before use—especially because AppSupervisor runs with administrator privileges and can start or stop applications and Windows services.

## Functionality summary

- Activates profiles when their monitored process starts, then processes applications, services, delays, Windows audio interfaces, Home Assistant, OBS, and Twitch actions in the configured order.
- Restarts applications or services that stop unexpectedly, with configurable close and restart timeouts.
- Gracefully closes applications by default, with optional force-kill only when explicitly enabled.
- Supports regular executables, Steam applications, Microsoft Store/MSIX applications, and Windows services.
- Launches every helper with the monitored executable's directory as its working directory so relative files resolve consistently.
- Runs ordered per-application Startup macros after confirmed launches, including delays, hotkeys, and window placement actions.
- Provides per-application listener and VRChat OSCQuery health checks, including optional recovery after a confirmed failure.
- Monitors expected SteamVR controllers, trackers, and base stations without starting or controlling SteamVR.
- Runs Twitch broadcaster chat messages, ads, and reversible chat-mode changes when a profile activates.
- Sends per-resource alerts through popup dialogs, Windows notifications, or XSOverlay.
- Includes a graphical configuration editor with application, Steam, Store, service, and running-process pickers.
- Validates configuration before applying it and keeps the last valid configuration active if a reload fails.
- Can register itself to start elevated when the user signs in to Windows.
- Can expose cached supervision status through an optional passwordless, read-only local HTTP API.

## Detailed functionality

### Profiles and resources

Each profile watches one process. When that process appears, AppSupervisor activates the profile's enabled resources from top to bottom. When it remains absent for the configured **Close timeout**, AppSupervisor closes or stops those resources.

Resources can include:

- Helper applications.
- Windows services.
- Nonblocking delays between startup entries.
- Home Assistant actions.
- OBS actions.
- Twitch broadcaster actions.
- Windows audio interface actions.

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

### Startup macros

Each helper application can have an ordered **Startup macros** sequence. AppSupervisor runs the sequence after it confirms a launch or relaunch that it requested; it does not run merely because an already-running process was discovered.

Available actions are:

- A nonblocking delay.
- A captured multi-key hotkey.
- Moving a window to coordinates relative to a selected monitor's working area.
- Resizing a window.
- Minimizing, maximizing, or restoring a window.
- Bringing a window to the front without explicitly activating it.

The editor displays detected monitor hardware names and stable Windows display identifiers. Move and resize actions can read the running helper window's current position or size, and every non-delay action can be tested individually. The complete sequence can also be tested before saving.

Hotkeys use Windows `SendInput`, so they are injected system-wide rather than sent to one window. AppSupervisor does not explicitly activate the helper, but the active application may also observe the shortcut and the helper's resulting command may change focus.

Window actions require exactly one eligible visible top-level helper window. Missing or ambiguous windows produce a helper error through its configured notification destinations. Safe later actions continue, and a macro failure does not itself restart the helper.

The existing **Minimize windows after starting** option remains the simpler persistent minimization behavior. When a Startup macro contains a Minimize action, that option is disabled and ignored to prevent the two mechanisms from competing.

### Windows services

The editor lists installed third-party services by service name and warns when an Automatic service is selected because applying an enabled entry changes it to Manual startup. AppSupervisor starts configured services with their profile, optionally restarts them after an unexpected stop, and requests a normal stop when the profile closes.

### Health checks

Health checks belong to a helper application and have their own timing, failure threshold, recovery action, and notification settings.

- **Listener** verifies that the helper owns a configured TCP or UDP port. It can run only while another selected process is active.
- **VRChat OSCQuery** checks VRChat OSCQuery availability and selected avatar parameters. It can also report when most available parameters remain unchanged for too long.

A confirmed failure can optionally trigger a graceful restart of the helper. One-shot tests do not change live failure state or restart external processes.

### Home Assistant

Profiles can run `turn_on`, `turn_off`, and `button.press` actions against compatible entities. `light.turn_on` actions also set a brightness from 1% through 100%; verification and persistence check that percentage as well as the `on` state. Stateful actions can be verified after execution and kept persistent while the profile is active. When the profile closes, `turn_on` and `turn_off` actions are reversed; buttons run only during activation.

Home Assistant uses a shared URL and long-lived access token. The token is stored in the local configuration files, so those files must be treated as credentials.

### OBS WebSocket

Profiles can switch the OBS program scene, mute or unmute an OBS input, and show or hide a source in a scene. OBS actions are ordinary ordered resources, so a profile may monitor `obs64.exe` and run them after OBS starts. Each action runs once during profile activation; closing the monitored app never restores or toggles the resulting OBS state.

OBS uses the standard WebSocket 5.x protocol over `ws` with a shared host, port, and optional password. The password is stored in the local configuration files, so those files must be treated as credentials.

### Windows audio interfaces

Profiles can set the master volume and mute state of an active Windows playback or recording interface. The interface picker includes **Default output** and **Default input** choices that follow the current Windows multimedia defaults. Before applying the requested state, AppSupervisor captures the current volume and mute values. By default it restores both values when the monitored app closes; clear **Restore original volume and mute when monitored app closes** when the requested state should remain in place. **Test for 5 seconds** temporarily applies the configured state and always attempts to restore the original values.

Windows can replace an audio endpoint ID after a driver update, reconnect, or device re-enumeration. AppSupervisor therefore stores the endpoint's device-instance ID, physical container ID, direction, and friendly names as recovery signals. It prefers exact and stable identity matches, and accepts a name-only fallback only when exactly one active interface matches; ambiguous matches require selecting the interface again in the editor.

### SteamVR device monitoring

AppSupervisor can monitor configured controllers, trackers, and Lighthouse/base-station devices while SteamVR is already running. Discovery records controller handedness and SteamVR tracker assignments such as left foot, left knee, or waist, so offline and recovery notifications identify the missing role. It does not start or restart SteamVR and does not control devices.

After repeated connection failures, it sends the selected notifications and shows an offline-device window. Reminders can be silenced for the current outage; recovery is detected automatically.

Generic/FBT trackers are monitored only after SteamVR reports them connected at least once in the current SteamVR session. A tracker intentionally left powered off for the whole session therefore does not produce an offline alert. Hand controllers and tracking references such as base stations remain mandatory from session startup.

### Twitch

Profiles can send a chat message, run a 30–180 second advertisement, or temporarily change emote-only, followers-only, slow, and subscribers-only chat modes. Messages and ads run once when the profile activates. Chat modes capture their previous Twitch values and restore those exact values after the monitored process closes.

Twitch uses one global broadcaster connection and allows Twitch resources in only one enabled profile at a time. Authorization uses Twitch's public-client device flow: the broadcaster approves access in a browser once, access tokens refresh automatically, and rotating credentials are stored in Windows Credential Manager for the current user. AppSupervisor includes its public Twitch application identity; users do not configure a Client ID or client secret.

### Notifications

Applications, services, health checks, Windows audio interfaces, Home Assistant actions, and SteamVR devices can report through:

- Popup dialogs.
- Windows notifications.
- XSOverlay notifications.

If XSOverlay is unavailable, its notifications fall back to Windows notifications.

### Configuration editor

Open the editor from **Configure...** in the tray menu or by double-clicking the tray icon. It supports adding, duplicating, removing, enabling, reordering, and configuring profiles and resources.

The editor provides pickers for running processes, executables, Steam applications, Microsoft Store applications, Windows services, Windows playback and recording interfaces, and SteamVR devices. Connections such as Home Assistant, OBS WebSocket, and Twitch, plus SteamVR monitoring, are configured globally rather than inside a profile.

**Validate** checks the complete configuration without saving. **Save & Apply** validates and writes it, then replaces the running configuration. If the new configuration cannot be applied, the previous valid configuration remains active.

### Supervisor API

Enable **Enable read-only WS API** under **Integrations**, then choose **Save & Apply**. AppSupervisor serves HTTP JSON at:

```text
http://127.0.0.1:17834/
```

The listener accepts connections only from the same computer. It has no password, allows cross-origin reads, disables response caching, and accepts only `GET`; unsupported methods return HTTP `405`. It never performs work on behalf of a request. Responses serialize the last immutable snapshot published by the existing one-second supervision timer, so requesting API data does not inspect processes, services, listeners, windows, health probes, or integrations.

#### Internal IDs

Routes use stable internal IDs rather than visible names:

- Profiles use `profileId`.
- Helpers use their existing `resourceId`.
- API responses expose both values as `internalId` and include ready-to-use relative `endpoint` fields.

New IDs are compact hexadecimal strings, so spaces and visible names never appear in API URLs. Existing profiles that predate `profileId` receive one when the configuration is loaded and saved. Duplicating a profile generates a new ID.

An abbreviated configuration therefore looks like:

```json
{
  "profiles": [
    {
      "profileId": "518b32a93ca941a6a65aaf3dde50668d",
      "name": "VR profile",
      "applications": [
        {
          "resourceId": "3fd8f94e25614cb181f09648aaad4e38",
          "path": "C:\\Tools\\helper.exe"
        }
      ]
    }
  ]
}
```

#### Endpoints

| Method and path | Response |
| --- | --- |
| `GET /` | All profiles with `name`, `internalId`, `enabled`, cached `status`, and `endpoint`. |
| `GET /<profileId>` | One profile and all its helpers, including active/enabled state, configured health-check and macro counts, and helper endpoints. |
| `GET /<profileId>/<resourceId>` | Helper identity, cached activity, lifecycle settings, configuration counts, and links to its health-check and macro endpoints. |
| `GET /<profileId>/<resourceId>/healthcheck` | Every configured health check, including application-responsiveness monitoring, timing, recovery settings, and cached status/detail. |
| `GET /<profileId>/<resourceId>/macro` | Whether a Startup macro is configured, its cached execution status, and its ordered actions. |

Unknown profiles, helpers, or child endpoints return HTTP `404`. A helper executable filename may also be accepted in place of `resourceId` when it is unique within the profile, but clients should always use the returned `internalId`; ambiguous filenames return HTTP `409`.

`updatedUtc` identifies when the one-second timer published the returned snapshot. A helper's `active` value means the profile startup sequence has activated that resource; it is not a fresh process query.

Status values are:

- Profile: `disabled`, `paused`, `active`, or `inactive`.
- Helper: `disabled`, `active`, or `inactive`.
- Health check: `disabled`, `inactive`, `checking`, `healthy`, or `unhealthy`.
- Startup macro: `notConfigured`, `idle`, `running`, or `failed`.

#### Example responses

`GET /`:

```json
{
  "updatedUtc": "2026-08-17T12:00:00Z",
  "paused": false,
  "profiles": [
    {
      "name": "VR profile",
      "internalId": "518b32a93ca941a6a65aaf3dde50668d",
      "enabled": true,
      "status": "active",
      "endpoint": "/518b32a93ca941a6a65aaf3dde50668d"
    }
  ]
}
```

`GET /518b32a93ca941a6a65aaf3dde50668d`:

```json
{
  "updatedUtc": "2026-08-17T12:00:00Z",
  "name": "VR profile",
  "internalId": "518b32a93ca941a6a65aaf3dde50668d",
  "enabled": true,
  "status": "active",
  "monitorProcess": "VRChat.exe",
  "helpers": [
    {
      "name": "helper.exe",
      "internalId": "3fd8f94e25614cb181f09648aaad4e38",
      "enabled": true,
      "active": true,
      "status": "active",
      "healthChecksConfigured": 1,
      "macroActionsConfigured": 2,
      "endpoint": "/518b32a93ca941a6a65aaf3dde50668d/3fd8f94e25614cb181f09648aaad4e38"
    }
  ]
}
```

The helper endpoint contains `healthCheckEndpoint` and `macroEndpoint`. Follow those links instead of constructing child paths manually.

#### Windows `curl.exe` smoke test

The following PowerShell script discovers internal IDs and requests every endpoint for the first configured helper:

```powershell
$ErrorActionPreference = 'Stop'
$baseUrl = 'http://127.0.0.1:17834'

function Get-CurlJson([string]$url) {
    $body = & curl.exe --silent --show-error --fail-with-body $url
    if ($LASTEXITCODE -ne 0) {
        throw "curl failed for $url"
    }
    $body | ConvertFrom-Json
}

$root = Get-CurlJson "$baseUrl/"
$profile = $root.profiles | Select-Object -First 1
if ($null -eq $profile) { throw 'No profiles returned.' }

$profileState = Get-CurlJson "$baseUrl/$($profile.internalId)"
$helper = $profileState.helpers | Select-Object -First 1
if ($null -eq $helper) { throw 'The selected profile has no helpers.' }

$helperUrl = "$baseUrl/$($profile.internalId)/$($helper.internalId)"
Get-CurlJson $helperUrl | ConvertTo-Json -Depth 20
Get-CurlJson "$helperUrl/healthcheck" | ConvertTo-Json -Depth 20
Get-CurlJson "$helperUrl/macro" | ConvertTo-Json -Depth 20
```

Error behavior can be checked directly:

```powershell
# 404: unknown profile
curl.exe -sS -o NUL -w '%{http_code}\n' http://127.0.0.1:17834/not-a-profile

# 405: the API is read-only
curl.exe -sS -o NUL -w '%{http_code}\n' -X POST http://127.0.0.1:17834/
```

### Administrator and startup behavior

AppSupervisor requires administrator privileges to manage configured services consistently. It prevents multiple supervisor instances and can create a current-user scheduled task that launches it with elevated privileges at sign-in.

## Configuration storage

Configuration is stored in `config.json` beside `AppSupervisor.exe`. If the file is missing, AppSupervisor creates an empty valid configuration. The last verified configuration is also saved as `config.json.old` during normal shutdown.

Both files are excluded from Git and omitted from release packages. Do not share or commit them if they contain a Home Assistant access token or OBS WebSocket password. Twitch OAuth credentials are not written to these files; Windows Credential Manager stores them separately for the current Windows user.

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
