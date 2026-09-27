## Context

See proposal.md for why. Relevant facts:

- **Player Sync defers pair data in GPose.** `PairManager.ReceiveCharaData` queues every pair DTO, addon/heels fast path included, while `DeferringDataApplications` is true, and that includes `IsInGpose`. The queue is replayed later. It's the same on the receiving side of any sync service built on that client (the README notes Lightless 3.3.0.0 behaves the same). Sending isn't blocked (`CacheCreationService` only pauses while zoning or `HaltCharaDataCreation`), which is why pairing works outside GPose.
- **GPose Together** uses `GposeLobbyPushPoseData` on Player Sync's server. Every 10 s it sends the local player's own GPose pose, read via Brio, as a delta with a full pose every 12th cycle, and applies received poses with `Brio.SetPoseAsync`. It's an internal channel with no IPC, and one character per sender.
- **What exists in CoPose (v0.2.2):** a Dalamud-free core (`TagSync`, `SceneState` LWW registers, `Pairing`, `CoPoseClient`, `IntervalGate`, `ActorIndex`) behind `ITagChannel`, `IActorRegistry`, `IPoseReader` and `IPoseWriter`, plus the plugin's Havok reader, Ktisis writer, SimpleHeels channel and GPose registry. 69 tests.
- **Cloudflare:** wrangler 4.129 on this machine is logged in (OAuth) as `raylapetal@gmail.com`, account `533aeb0b142cf61410e38ad6ba986205`, with Workers write access. xiv-collar's `oathbound-relay` Worker (D1/R2, no Durable Objects) lives there. CoPose adds a separate Worker and doesn't touch it.

## Goals / Non-Goals

**Goals:**
- Live (sub-second) posing of both characters while both players are in GPose.
- Self-healing sync: no manual Push needed; reconnects and late readiness catch up automatically.
- Reset characters to their starting pose, on both clients.
- Zero setup for players; one Worker deployed by the owner; free-tier friendly.

**Non-Goals:**
- Replacing SimpleHeels-based discovery and pairing (it works outside GPose and gives trust via sync-service pairing).
- More than two players per room; root/world transforms; permissions.
- End-to-end encryption of pose data. Room ids are secret, and the relay is the owner's; see Risks.

## Decisions

### D1. Relay: one Worker, one Durable Object per room, Hibernation WebSockets
`CoPose.Relay/` (TypeScript, same tooling as xiv-collar's worker: wrangler, vitest with `@cloudflare/vitest-pool-workers`, eslint, tsc).
- `GET /` returns `{ service: "copose-relay", protocol: 1 }` (health).
- `GET /v1/room/<roomId>?p=<participantId>` with `Upgrade: websocket` is validated (roomId: 64 hex chars; participantId: 32 hex chars) and routed to `POSE_ROOM.idFromName(roomId)`.
- Durable Object `PoseRoom` is SQLite-backed (`new_sqlite_classes` migration, which the Workers free plan requires). It uses the Hibernation API: `ctx.acceptWebSocket(ws, [participantId])`, `webSocketMessage`, `webSocketClose`. Idle rooms cost nothing.
- A third distinct participant is refused (close 4003 "room full"). The same participant id closes its older socket (4000 "replaced").
- Stored state is `ctx.storage` key `state:<participantId>`. When the room empties, an alarm deletes all storage after 10 minutes, and it's cancelled if someone reconnects.
- Limits are enforced per socket, with counters kept in the socket attachment so they survive hibernation: more than 64 KB means close 1009 "too large"; more than 30 messages per rolling 1 s window means close 4008 "rate limited".
*Alternative:* a plain Worker with KV polling. Rejected: no push, KV is slow to propagate, and the free KV write limits are low.

### D2. Wire framing: 1-byte kind prefix, opaque body
Binary WebSocket frames: `[kind][body]`. The relay reads only `kind`:

| kind | sender | relay action |
|---|---|---|
| `0x01` FULL | client | store as that participant's latest, then forward |
| `0x02` LIVE | client | forward only (deltas, control) |
| `0x10` PEER_JOINED | relay | sent to the other participant |
| `0x11` PEER_LEFT | relay | sent to the other participant |

On connect, the relay sends `PEER_JOINED` if the other participant is connected, then that participant's stored FULL frame, if any. Bodies are Brotli-compressed MessagePack `SceneMessage` (CoPose.Protocol), a union of:
- `StateMessage { Self, Ready, Resolved[], Clock, Actors[] }`: `Actors` uses the existing `TagActor` shape (authored bones only). FULL frames carry *all* authored bones; LIVE frames carry only bones changed since the last send.
- `StopMessage { Reason }`, as LIVE.

### D3. Rooms from paired secrets
Each client generates a 16-byte random **session nonce** per plugin load and publishes it in its tag (tag protocol v3, prefix `CP3:`, and the tag no longer has `Actors`). When the tags pair up:
```
roomId        = hex(SHA256("copose-room-v1" | keyLow | nonceOf(keyLow) | keyHigh | nonceOf(keyHigh)))   // 64 hex
participantId = hex(SHA256("copose-participant-v1" | ownNonce))[0..32]
```
Here `keyLow`/`keyHigh` are the two actor keys in ordinal order. Only players who can read both tags (paired in the sync service and nearby) can compute the room. A new nonce after a plugin reload means a new room; the partner learns it from the tag once tags flow again (outside GPose). That limitation is documented in the UI and README.

### D4. Plugin transport: `ISceneChannel` + `RelayChannel`
Core gets `ISceneChannel { State Connection; Send(kind, body); TryReceive(out SceneEvent) }`, where `SceneEvent` is Received(StateMessage), Stop, PeerJoined, PeerLeft, Connected or Disconnected(reason). `TagSync` stops reading tags; `CoPoseClient` feeds it from the scene channel.
`RelayChannel` (Core, Dalamud-free) wraps `System.Net.WebSockets.ClientWebSocket` with a read loop and a send channel (the same pattern as v0.1.0's `FrameConnection`). It reconnects with exponential backoff (1, 2, 4… up to 30 s) while a room is set, and reports status and last error. Tests use an in-memory fake relay implementing the D2 semantics. The real Worker is tested in vitest, plus a manual `wrangler dev` smoke test.

### D5. Send policy
- Sample at 20 Hz as today. Local edits accumulate into a pending LIVE delta, which is sent when non-empty, at most every **100 ms** (10/s).
- A FULL state is sent every **3 s** while in a session and connected, on (re)connect, on readiness change, and after Reset or Push. That's the automatic resync, and FULL is what the relay stores for catch-up.
- Merge is per-bone LWW, so deltas and fulls can arrive in any order, be repeated or be lost: correctness only depends on the periodic FULL.
- Size: FULL holds about 300 own-seed bones plus edits, around 10–25 KB compressed (measured 25 KB base64 in v0.2.2, about 19 KB binary), well under 64 KB. LIVE while dragging is a few bones, well under 1 KB.

### D6. Session lifecycle over tags + relay
- **Start:** tags mutual (as now), then derive the room and connect.
- **Presence and ready:** from `StateMessage.Ready`/`Resolved`, replacing tag-based ready.
- **Stop:** send `StopMessage` (LIVE) and clear the tag partner. On receiving Stop, the client ends the session and clears its own choice.
- **Partner gone:** `PEER_LEFT`, and no reconnect or tag naming me within 30 s. `Pairing`'s tag-based end detection stays for when tags do flow (outside GPose).
- Plugin unload sends Stop, closes the socket, then removes the tag (spec: Clean shutdown).

### D7. Reset to starting pose
On each transition to ready, `TagSync` copies both characters' baseline read (`LocalActor.Rebuild`) into `StartPose[actor]` (names and samples). `Reset(actor)` records every bone of that start pose with one fresh version authored by self, marks those bones dirty so they're applied locally, and sends a FULL. `Reset both` does both actors. Start poses are cleared when the session ends. The UI adds "Reset" per character and "Reset both".

### D8. Deployment
`CoPose.Relay/wrangler.toml`: `name = "copose-relay"`, `[env.local]` and `[env.production]`, a Durable Object binding `POSE_ROOM` → `PoseRoom`, and `[[migrations]] tag = "v1", new_sqlite_classes = ["PoseRoom"]`. No secrets, and no account id in the file: wrangler uses the logged-in account. Deploy with `npx wrangler deploy --env production` from `CoPose.Relay` once the owner approves at that step. The resulting `https://copose-relay.<account-subdomain>.workers.dev` goes in the plugin's default relay URL (config key `RelayUrl`, overridable in the debug panel).

**Free-tier budget** (my reading of current Cloudflare terms; to confirm on the dashboard at deploy): 100k Durable Object requests a day, with incoming WebSocket messages counted at 20:1. A session with both players dragging at 10 LIVE/s plus FULL every 3 s is about 21 msg/s → about 3,800 counted requests an hour, so about 26 hours of continuous two-player dragging a day. Idle sessions are FULL every 3 s × 2 → about 120 counted requests an hour. Storage is a couple of values per room, deleted 10 minutes after the room empties.

### D9. CI
The `pr-build` workflow gains a job that runs `npm ci`, lint, typecheck and `vitest` in `CoPose.Relay`. The release workflow doesn't deploy the Worker. Worker deploys stay manual and deliberate.

## Risks / Trade-offs

- [Cloudflare outage or free-tier exhaustion stops live sync] → The UI shows "relay unreachable" and keeps retrying. Pairing is unaffected. Usage is far below the quota.
- [Pose data passes through Cloudflare unencrypted beyond TLS] → The room id needs both secret nonces, the relay is the owner's own, and nothing is kept after rooms empty. End-to-end encryption can be added later using the shared nonces as key material.
- [A plugin reload during GPose changes the nonce, and the partner can't learn the new room until tags flow] → Keep the nonce for the whole plugin lifetime (not per session). The UI says "re-pair outside GPose" when the partner reports a different nonce.
- [Rate or size limits cut off a legitimate client] → FULL is at most about 25 KB against the 64 KB cap. The client sends at most 10 LIVE plus occasional FULL per second, against the cap of 30.
- [Durable Object hibernation drops in-memory counters] → Counters are kept in socket attachments.
- [Deploying to the owner's account by mistake overwrites something] → A distinct Worker name and a separate project folder. The deploy is an explicit, approved task. `oathbound-relay` isn't touched.

## Migration Plan

1. Build and test the Worker locally (`wrangler dev --env local`), and point a dev build of the plugin at it.
2. With the owner's approval, deploy `copose-relay` to production, then set the default relay URL.
3. Release plugin 0.3.0 (tag v0.3.0). Both players update. 0.2.x peers appear as "needs the same CoPose version".

Rollback: players reinstall 0.2.2 from its release, and the Worker can be deleted with `wrangler delete --env production` without affecting xiv-collar.

## Open Questions

- The exact `workers.dev` subdomain of the account. It's known after the first deploy and doesn't affect the design.
