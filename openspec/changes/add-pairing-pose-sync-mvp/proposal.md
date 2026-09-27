## Why

Existing tools (Mare "GPose Together", LivePose) only let each player send their **own** pose, at best every 10 s. [DESIGN.md](../../../DESIGN.md) describes CoPose, a shared GPose scene where either player can pose either character live. The repo is still the unmodified Dalamud `SamplePlugin` template. We need the smallest end-to-end slice that proves the core idea before we build locks, permissions, root transforms and the rest: **two players pair up, and bone edits made by either player on either character show up on the other client.** Pairing must not need a VPS or any server someone has to run. One player hosts the session from inside the plugin.

## What Changes

- Rename and repurpose the `SamplePlugin` template into a `CoPose` Dalamud plugin (`/copose` command, main window, config with the host port). Solution becomes `CoPose.slnx`.
- Add `CoPose.Protocol`: a shared DTO library (`ActorKey`, `Hello`, `Welcome`, `PeerInfo`, `Envelope`, `BoneDelta`, `BoneTransform`, `FullSnapshot`, `Presence`, `SnapshotRequest`, `Invite`) with MessagePack serialization and length-prefixed TCP framing.
- Add `CoPose.Core`: Dalamud-free logic.
  - `SessionHost`: a TCP listener that accepts one guest after a secret check. It stamps a sequence number on every message and forwards it, in order, to every participant including the sender and the host itself.
  - `SessionClient`: connects to a host.
  - Sync logic: scene state, diffing, ordering, snapshot scheduling.
  - All of this sits behind `IPoseReader`/`IPoseWriter`/`IActorRegistry`/`ISessionTransport` interfaces so it can be unit-tested.
- Plugin **Host** flow: start the listener on a configurable port (default 47715) and try to open that port on the router with UPnP. Then show an **invite code** that encodes the host's public and LAN addresses, the port and a random session secret. If UPnP fails, show manual port-forward / LAN / Tailscale guidance.
- Plugin **Join** flow: paste the invite code. The plugin tries the encoded addresses and authenticates with the secret.
- Plugin: add a Ktisis IPC wrapper (API version check, `IsPosing`/`PosingChanged`, `SavePose`, `LoadPoseExtended`, `ApplyAbsolutePoses`).
- Plugin: add a GPose actor registry that maps `ActorKey(Name, HomeWorldId)` to the local GPose object index. Object indices never go on the wire.
- Plugin: add an unsafe Havok `PoseReader` and an `IPoseWriter` backed by `Ktisis.ApplyAbsolutePoses`.
- Plugin: host the `SceneSync` tick loop at 20 Hz on the framework thread. It runs diff → send `BoneDelta`, then receive → host-ordered last-writer-wins (pending local edits beat earlier remote ones) → apply → re-read for echo suppression.
- Plugin: send a full-pose snapshot (Ktisis pose JSON) on join, on peer ready and on demand ("Push pose"/"Request resync"). Gate sending and applying on readiness (in GPose, Ktisis posing on).
- Plugin UI: Host / Join (invite field) / Leave, invite code with copy button, reachability status (UPnP result), peer ready state, resolved actors, and a debug counter panel.
- Add a unit/integration test project covering diff, ordering, DTO and framing round-trips, and the host/client over loopback TCP.

**Explicitly deferred** (DESIGN.md goals 3, 5, 6 and M3–M7): root/world transform sync and the anchor model, soft locks, per-partner permissions and consent prompt, auto-reconnect, `HavokDirectPoseWriter`, NAT hole-punching / relay fallback for hosts behind carrier-grade NAT, and the standalone relay server / Docker deploy from DESIGN.md §9. By joining a session, you consent to your partner posing your character; leaving ends it.

## Capabilities

### New Capabilities
- `relay-rooms`: the in-plugin session host that acts as the relay. Starting and stopping hosting, the invite code, authenticating a single guest, ordered message forwarding with a sequence number and echo to the sender, frame limits and keepalive. (The path keeps its original name; the capability is now hosted inside the plugin rather than on a server.)
- `pairing`: the client-side session. Hosting or joining with an invite, router reachability via UPnP, exchanging presence/readiness, and resolving each participant's actor locally by `ActorKey`.
- `pose-sync`: shared bone posing. Detecting local bone edits on any session actor, sending deltas, applying remote deltas with host-ordered last-writer-wins and echo suppression, and full-pose snapshots for initial and on-demand sync.

### Modified Capabilities
<!-- none: no existing specs -->

## Impact

- **Code:** `SamplePlugin/` becomes `CoPose/`. Adds new projects `CoPose.Protocol/`, `CoPose.Core/` and `CoPose.Tests/`. Updates `SamplePlugin.slnx` → `CoPose.slnx` and `.github/workflows/pr-build.yml`. Removes `Data/goat.png` usage. There is no server project.
- **Dependencies:** `MessagePack` (protocol) and `Mono.Nat` (UPnP/NAT-PMP port mapping), both packaged with the plugin output, plus xUnit (tests). Networking uses `System.Net.Sockets` from the base runtime. ASP.NET Core/SignalR is **not** usable, because Dalamud's bundled runtime contains only `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App`.
- **Runtime requirements:** Ktisis installed with IPC API `1.x` that exposes `ApplyAbsolutePoses`, and both players in the same instance, in GPose, with Ktisis posing on. The host must be reachable by the guest in one of these ways: a router with UPnP enabled, a manual port-forward, the same LAN, or a shared VPN like Tailscale. Hosts behind carrier-grade NAT can't be reached directly in this version.
- **Security:** hosting opens an inbound TCP port. It's protected by a per-session random secret, a limit of one guest, a handshake timeout and frame size limits. The listener and port mapping are removed when the session ends or the plugin unloads.
- **Licensing:** the plugin only calls Ktisis through IPC; no Ktisis code is copied in this change. The repo's `LICENSE.md` is AGPL-3.0, which is GPL-3.0-compatible for any later Ktisis code reuse (M6).
