## Why

In the v0.2.2 test, posing either character changed nothing on the partner's screen until they left GPose and re-entered it. The cause is in Player Sync (and, per a README note, Lightless): `PairManager.ReceiveCharaData` defers **all** incoming pair data, including SimpleHeels tag updates, while the receiver is in GPose, and replays it after GPose ends. SimpleHeels tags therefore can't carry live posing, or even session presence or "stop", during GPose. GPose Together avoids this by using a separate lobby channel on Player Sync's server, but it only sends each player's *own* pose every 10 s, and there is no IPC for it.

To pose both characters live, CoPose needs a channel nothing defers. The owner already runs Cloudflare Workers (xiv-collar's `oathbound-relay`) on an account this machine is logged in to. A small, separate Worker there can relay CoPose sessions with no setup for players. The user also wants a way to reset poses, and sync that happens by itself without pressing Push pose.

## What Changes

- Add **`CoPose.Relay`**: a separate Cloudflare Worker (`copose-relay`) with one Durable Object per session room. It forwards each participant's messages to the other, keeps each participant's latest state, and replays it to anyone who (re)connects. It tells each side when the other connects or leaves, and caps connections, message size and rate. It knows nothing about poses. It deploys to the owner's existing Cloudflare account, alongside and independent of `oathbound-relay`.
- **Scene data moves from the SimpleHeels tag to the relay.** The tag keeps pairing only: version, self, chosen partner, and a per-session random room nonce. Both sides derive the room from their two nonces, so only players paired in the sync service can find it. The tag no longer carries bones, which ends the size warnings.
- **Presence and stop also go over the relay while connected**, so "partner is ready", "partner stopped" and "partner left" work inside GPose. Tags remain the way to start a session, and pairing happens outside GPose, where tags flow.
- **Live posing:** local edits are published to the relay as soon as they're recorded, up to 10 times a second, so a drag shows on the partner's screen within a few hundred milliseconds.
- **Automatic sync:** every 3 seconds while in a session, each side re-sends its full authored state, and on (re)connect it receives the partner's stored latest state. Nobody needs to press Push pose to get in sync. It stays available as a manual override.
- **Reset:** when a participant becomes ready, CoPose remembers each character's starting pose. "Reset" (per character, or both) re-applies that starting pose as a new edit, so both screens return to it together.
- Diagnostics show the relay connection state, messages sent and received, the last message size, and the time since the partner's last update.
- Release as **0.3.0**. Both players must update, because 0.2.x doesn't use the relay.

## Capabilities

### New Capabilities
- `pose-relay`: the Cloudflare relay service: session rooms, forwarding between two participants, latest-state storage and replay on connect, presence notifications, and connection, size and rate limits.

### Modified Capabilities
- `pose-sync`: publishing goes to the relay live, not throttled into a tag; automatic periodic resync and catch-up on connect; applying targets sub-second latency; Reset to starting pose; the scope no longer excludes live dragging; diagnostics describe the relay.
- `pairing`: the tag carries a room nonce instead of scene data; relay connection lifecycle and status; presence and stop are carried over the relay during a session, so session end is detected in GPose.

## Impact

- **New project:** `CoPose.Relay/` (TypeScript Worker, Durable Object, vitest tests, `wrangler.toml` with no secrets). The deploy uses the owner's logged-in wrangler session and is done as an explicit, approved task.
- **Plugin:** a relay channel over `System.Net.WebSockets.ClientWebSocket` (built into .NET, no new NuGet dependency), a leaner tag, a session control path over the relay, the Reset UI, and a relay status panel. The Worker URL is in configuration, with a default for the deployed Worker.
- **Protocol:** the tag protocol version becomes 3 (tag without actors, plus a nonce), plus a relay message envelope (MessagePack). 0.2.x peers show as "needs the same CoPose version".
- **External systems:** Cloudflare Workers plus Durable Objects on the owner's account, which the free tier covers at expected use (see design). Player Sync or Lightless plus SimpleHeels are still required, for discovery and pairing.
- **Privacy:** pose data transits Cloudflare. Room ids come from secrets only the two paired players know. The relay stores only the latest state per participant and drops a room shortly after both leave.
