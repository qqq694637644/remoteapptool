# RemoteAppSessionHost

This executable implements the isolated Strict App Session lifecycle described in `STRICT_APP_SESSION_PROJECT_PLAN.md`.

When a RemoteApp has **Strict App Session** enabled, RemoteApp Tool stores the real application path in its own registry values and publishes a per-app copy of this launcher instead. The launcher resolves that configuration from its GUID deployment directory and then owns the application lifecycle:

1. run inside the RemoteApp/RDP session;
2. launch a target application;
3. track the target process tree and its visible top-level windows;
4. when the last tracked application window closes, wait for the close grace period;
5. log off **only the current session** with `WTSLogoffSession`;
6. optionally log off a disconnected session after a separate disconnect grace period.

Strict always uses `LastTrackedWindowClosed`. Persistent always uses `ProcessTreeExited`. The two modes do not share termination or disconnect policies.

RemoteApp Tool also forces generated Strict RDP connections to use:

```text
disableconnectionsharing:i:1
```

so each Strict launch requests a new session.

## Persistent App Session

**Persistent App Session** reuses the same launcher infrastructure but changes the lifecycle for long-running tray/background applications such as messaging clients:

- the launcher tracks the application process tree instead of treating the last visible window as application exit;
- closing or hiding the application's last window does not log off the session while the tracked process is still running;
- disconnecting the RemoteApp client never triggers the launcher's disconnect-logoff path;
- generated client connections keep connection sharing enabled so Windows can reconnect to the existing disconnected user session instead of forcing a new Strict session;
- when the tracked process tree actually exits, the launcher exits without calling `WTSLogoffSession`.

Persistent mode does not override an administrator- or domain-enforced RDS disconnected-session timeout. If such a policy exists, that external policy remains authoritative.

## Breaking configuration change (schema v2)

The published app now stores exactly one mode in `RemoteAppToolSessionMode`: `1` = Strict, `2` = Persistent. Normal apps do not have managed-session metadata. The old `RemoteAppToolStrictSession`, `RemoteAppToolPersistentSession`, `RemoteAppToolTerminationMode`, and `RemoteAppToolDisconnectBehavior` values are no longer supported. The launcher requires `RemoteAppToolSchemaVersion = 2`.

**No upgrade or fallback is provided for v1 registrations.** Before installing this version, back up and remove existing Strict/Persistent RemoteApp registrations, then recreate them in RemoteApp Tool and regenerate client RDP/MSI packages. Do not merely change the schema version in the registry. Strict mode now always logs off on last-window close (or after its configured disconnect grace period); Persistent mode always preserves disconnects and tracks the process tree.

## Safety behavior

- No session ID is accepted from the command line. The host can only request logoff for its own session.
- A session-local controller mutex and shared-session signal disable automatic logoff when two hosts are detected in the same session.
- Visible windows that already existed in the session before the target starts make the launcher fail safe and disable automatic logoff for that session.
- `--no-logoff` exercises lifecycle detection without actually logging off the session.
- Full target arguments are not written to the lifecycle log.

## Normal deployed mode

The normal deployed launcher does not need `--target`. Its parent directory is a Strict App GUID and the launcher resolves the matching RemoteApp registry entry automatically. Any RemoteApp command-line arguments received by the launcher are forwarded to the real target application.

Registry preflight after enabling Strict mode:

```powershell
.\scripts\Test-StrictSessionRegistration.ps1 -Alias <remote-app-alias> -RdpPath <generated-rdp-file>
```

## Spike / diagnostic command line

The original direct-target mode remains available when the executable is not running from a GUID deployment directory. It is intended for lifecycle diagnostics:

```text
RemoteAppSessionHost.exe --target <exe> [options] [-- <target arguments>]

Options:
  --startup-grace-ms <n>      default 10000
  --close-grace-ms <n>        default 1500
  --disconnect-grace-ms <n>   default 30000; -1 preserves disconnected sessions
  --poll-ms <n>               default 250
  --no-logoff                 detect lifecycle but do not call WTSLogoffSession
  --persistent                use process-tree tracking and preserve disconnected sessions
```

Safe local smoke test:

```powershell
.\scripts\Test-StrictSessionHost.ps1 -Headless
```

Interactive safe test:

```powershell
.\scripts\Test-StrictSessionHost.ps1
```

For a manual lifecycle diagnostic, publish this executable with a required command line such as:

```text
--target C:\Windows\System32\notepad.exe
```

and ensure the client RDP contains:

```text
disableconnectionsharing:i:1
```

For hand-built diagnostic RemoteApps, do the first remote test with `--no-logoff`. Remove it only after the session ID and lifecycle log show that the process is running in the intended isolated RemoteApp session.

Logs are written to:

```text
%LOCALAPPDATA%\RemoteAppTool\StrictSession\logs
```
