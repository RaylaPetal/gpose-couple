## Why

v0.1.0 needs one player to host a session from their PC, which means the host must accept an incoming connection. In practice that failed: with carrier-grade NAT on the host's connection, UPnP couldn't open a port, and even a playit.gg tunnel didn't let the partner connect. Getting it running (ports, firewall prompts, tunnels or VPNs) is also too complicated for the people this is for.

Players who pose together already run **Player Sync or Lightless with SimpleHeels**, and those sync services already carry a small piece of per-character data between paired players: **SimpleHeels tags**. LivePose uses the same channel for poses. Tag data reaches paired players even while they're in GPose, because Player Sync applies heels updates through a separate fast path with no GPose check. By putting CoPose's shared scene into a tag, CoPose needs no server, host, code, port or VPN.

## What Changes

- **BREAKING** Remove session hosting entirely: the TCP host and client, invite codes, UPnP port mapping, the public/tunnel address field, and the host-port setting. v0.1.0 users must update both clients.
- CoPose publishes its state in a SimpleHeels tag (`CoPose`) on the local player's character (object 0), and reads partners' `CoPose` tags as SimpleHeels reports them.
- **Pairing by choosing a partner instead of a code:** the window lists nearby players whose tag shows CoPose, and you click **Pose with <name>**. A session exists when both players have chosen each other; one-sided choices show as a request the other player can accept.
- **Server-free convergence:** each tag carries the whole shared scene (every bone either player has set, for both characters) with a per-bone version made of a logical clock and an author. Receivers keep whichever version is newer, so both clients converge without a central order, and missed or duplicated tag updates don't matter.
- **Update timing changes:** edits are republished at most twice a second. The sync service delivers them roughly 0.5–1.5 s later, so a partner sees a drag in steps rather than smoothly. The 200 ms "live" target is dropped.
- Initial sync becomes "seed your own character's current pose into the shared state when the session starts". **Push pose** stays (it re-stamps an actor's current bones as the newest). **Request resync** is removed: the partner's tag always holds the full state.
- The window checks for SimpleHeels and a sync service (Player Sync or Lightless), and explains what's missing.
- Keep the Havok bone reader, Ktisis writer, diffing, echo suppression, actor lookup and readiness rules from v0.1.0.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `pairing`: replaces hosting/joining and router reachability with sync-service prerequisites, partner discovery and mutual pairing; participant identity, readiness and shutdown change to work through tags.
- `pose-sync`: replaces host-ordered last-writer-wins with per-bone logical-clock last-writer-wins; publishing, applying, echo handling, initial sync, scope and diagnostics change for the tag channel.
- `relay-rooms`: all requirements removed (no in-plugin host any more).

## Impact

- **Code removed:** `CoPose.Core/Net/*` (FrameConnection, SessionHost, SessionClient, PublicAddress, ISessionTransport), the frame codec, frames and invite in `CoPose.Protocol`, `CoPose/Net/*` (UpnpMapper, NetworkInfo), and their tests. The `Mono.Nat` dependency goes.
- **Code added:** a SimpleHeels IPC wrapper, a tag codec (MessagePack + Brotli + base64), a Dalamud-free scene model with the version-merge rule, a partner-discovery and pairing service, and a new window layout.
- **Reused:** `HavokPoseReader`, `KtisisIpcPoseWriter`, `GposeActorRegistry`, `PoseDiff`, and the readiness logic.
- **Runtime requirements:** Ktisis (unchanged), plus **SimpleHeels** and **Player Sync or Lightless**, with the two players paired in that service and near each other.
- **External systems:** CoPose data travels through the sync service's server. Size and rate have to be kept modest (compressed state, at most 2 publishes per second, only while something changed), both to fit the service and to be a good citizen on a community-run server.
- **Privacy:** anyone paired with you in the sync service and nearby receives your `CoPose` tag. It holds pose data and who you're posing with, nothing else.
