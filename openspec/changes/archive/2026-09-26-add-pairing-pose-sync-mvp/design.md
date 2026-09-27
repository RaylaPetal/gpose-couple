## Context

The repo is the unmodified Dalamud `SamplePlugin` template: `Dalamud.NET.Sdk/15.0.0` targeting `net10.0-windows`, a single `SamplePlugin.slnx`, static `[PluginService]` properties in `Plugin.cs`, a `WindowSystem` with `MainWindow`/`ConfigWindow`, and a Windows CI workflow that downloads Dalamud and runs `dotnet build`. `LICENSE.md` is AGPL-3.0. The full architecture is in [DESIGN.md](../../../DESIGN.md). This change implements the pairing and bone-sync slice (roughly milestones M0 and M2, plus the join snapshot from M4). DESIGN.md's standalone relay (§9) is replaced with a relay that runs inside the host player's plugin; see proposal.md. Behavioural requirements are in `specs/relay-rooms` (the in-plugin host), `specs/pairing` and `specs/pose-sync`.

Hard constraints:
- **Dalamud's bundled runtime** (`%AppData%\XIVLauncher\runtime\shared`) has only `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App`. ASP.NET Core (Kestrel, the SignalR server) can't be loaded in a plugin. `HttpListener` needs an admin URL ACL for non-localhost prefixes. That leaves raw `System.Net.Sockets` as the only practical way to listen.
- **Ktisis** (DESIGN.md §3):
  - Bone writes only stick while Ktisis posing mode is on.
  - `ApplyAbsolutePoses(uint idx, Dictionary<string, Matrix4x4>)` resolves bones by name, taking the **first** match across partials, and returns a `Task<bool>`.
  - `SavePose`/`LoadPoseExtended` give full-pose JSON round-trips.
  - GPose object indices differ between clients.
- **Home networks:** the host is almost always behind a NAT router. An inbound connection only works with a UPnP/NAT-PMP mapping, a manual port-forward, a shared LAN, or a VPN overlay (such as Tailscale). Carrier-grade NAT can't be traversed without a third party.

## Goals / Non-Goals

**Goals:**
- Pairing with no server anyone has to run: one player clicks Host, the other pastes an invite.
- A solution layout that keeps all sync and session logic out of Dalamud-dependent code, so it can be unit-tested headless (including host and client over loopback TCP).
- A correct convergence model for two clients editing the same bones concurrently.
- A hot path (read + diff) under 0.2 ms per tick for 2 actors at 20 Hz.

**Non-Goals:**
- NAT hole-punching, STUN/TURN, or any rendezvous/relay service. Hosts behind carrier-grade NAT need a manual workaround (a VPN overlay).
- Transport encryption. The session secret authenticates the guest, but pose data is sent in plaintext. It's low-sensitivity, and TLS without a CA adds certificate UX for no real gain in the MVP.
- Direct Havok writes (`HavokDirectPoseWriter`); the IPC writer is the only backend.
- A loopback/second-local-actor mode or a replay client. Manual testing needs two game clients.

## Decisions

### D1. Solution layout: four projects, no server
```
CoPose.slnx
├─ CoPose/            Dalamud plugin (renamed SamplePlugin): Plugin.cs, Interop/, Net/UpnpMapper, UI/
├─ CoPose.Protocol/   net10.0 lib: DTOs + MessagePack attributes, MsgType, ProtocolVersion, Invite codec, frame codec
├─ CoPose.Core/       net10.0 lib: SessionHost, SessionClient, Sequencer, SceneSync, SceneState, PoseDiff, interfaces
└─ CoPose.Tests/      xUnit: Protocol, Core, and host↔client over 127.0.0.1
```
**Why:** `SessionHost`/`SessionClient` only need `System.Net.Sockets`, so they live in Core and are fully testable without the game. `SceneSync` is behind `IPoseReader`, `IPoseWriter`, `IActorRegistry`, `ISessionTransport` and `IClock`, so the whole pipeline (diff → send → sequence → receive → apply → echo-suppress) can be tested with two fake skeletons over a real loopback socket. UPnP stays in the plugin (`Net/UpnpMapper`) because it has side effects on the user's router and isn't needed for tests. *Alternatives:* a SignalR/ASP.NET relay (DESIGN.md §9), which needs a server and can't run in-plugin; WebSockets via `HttpListener`, which needs admin rights for LAN/public binding.

### D2. Transport: TCP with length-prefixed MessagePack frames
Each frame is a `uint32` little-endian length followed by a MessagePack-serialized `Frame` union: `Hello`, `Welcome`, `Reject(reason)`, `PeerJoined`, `PeerLeft`, `Send(MsgType, body)`, `Deliver(Envelope{Type, SenderId, Seq, Body})`, `Ping` and `Bye`. The reader rejects a length above 128 KB **before** allocating, and disconnects the peer. `NoDelay = true` avoids Nagle latency on small deltas. One reader loop and one writer loop per connection, the writer fed by a `Channel<Frame>`, so writes never interleave. Keepalive: send `Ping` after 5 s without outbound traffic, and drop the connection after 15 s without inbound. `Vector3`/`Quaternion` are sent as flat `float` fields on `BoneTransform`, so no custom resolver is needed.
**Why:** It's the simplest thing that works with the base runtime, and MessagePack is already chosen. TCP gives the reliable, in-order delivery the ordering model (D3) depends on. *Alternative:* UDP with a custom reliability layer, which is only worth it for hole-punching (a non-goal).

### D3. Ordering: host sequencer echoes to everyone; pending-local-wins
`SessionHost` owns a `Sequencer`. All inbound `Send` frames, from the guest connection **and** from the host's own in-process `SessionClient`-equivalent (a loopback `ISessionTransport` that skips the socket), go into one `Channel`. A single consumer task assigns `seq = ++Seq` and enqueues `Deliver` to every participant's outbound channel, including the sender's. Each connection's writer drains in FIFO order, so every participant sees envelopes in strictly increasing seq order.

On the client, `SceneState` keeps two things per `(ActorKey, boneName)`:
- `Value`: the last committed value.
- `PendingLocalSeq`: the local send-counter of the newest unacked local edit for this bone.

Each outgoing `BoneDelta` carries a client-local `LocalSeq`.
- **Own echo** (`SenderId == me`): for each bone whose `PendingLocalSeq == delta.LocalSeq`, clear the pending mark. Don't apply anything.
- **Remote delta:** skip bones with a pending mark, and apply the rest.

**Why this converges:** delivery is in total seq order on every connection. A remote message that reaches A before A's own echo has a lower seq than A's in-flight edit, so A's edit is "last", and B will apply it because B's own (lower-seq) echo already came back. The host goes through exactly the same path as the guest, so there's no host advantage and no special case. *Rejected:* optimistic `(seq, clientId)` versions (DESIGN.md §6.2), because the sender never learns its own seq, so the two sides can diverge.

### D4. Invite code
`Invite{Version=1, Secret(8 bytes), Endpoints[1..3] (IPv4 + port each)}` is encoded as bytes and Crockford base32, with a `CP2-` prefix and a CRC-8 check byte, grouped in blocks of 4 characters for readability (about 30–50 characters). Endpoints are listed LAN first, then the user-entered public/tunnel address, then the UPnP public address. Each endpoint carries its own port because tunnels (playit.gg, bore, ngrok) assign a public port that differs from the local listen port. `CP2` replaced the single-port `CP1` layout before any release, so there's no compatibility path. Decoding ignores case, hyphens and whitespace, and maps `O→0` and `I/L→1`. The secret is compared in constant time.
**Why:** It's one string to copy/paste over Discord or tells. The LAN address makes same-household play work without hairpin NAT. The CRC catches paste errors before a 3-second connect timeout. *Alternative:* separate IP/port/password fields, which are more error-prone for users.

### D5. Router reachability: Mono.Nat UPnP/NAT-PMP, best effort
When hosting starts, `UpnpMapper` runs discovery for up to 5 s. When a device is found, it creates a TCP mapping `port→port` described as "CoPose" with a 1-hour lease, renewed every 30 min, and reads the external IP. The external IP is classified as `public`, or as `private/CGNAT` (RFC 1918 or `100.64.0.0/10`). Only a public IP goes into the invite. The mapping is deleted on stop and on plugin dispose, and it's stored in memory so dispose can remove it synchronously with a 2 s cap. The primary LAN address is picked from the NIC that has the default gateway. A user-entered public address (config) is always included, whatever the UPnP result. It is parsed as `address[:port]` (the port defaults to the host port), and a host name is resolved to IPv4 with `Dns.GetHostAddressesAsync` when hosting starts. That covers manual port-forwards, Tailscale IPs, and host-only tunnels such as playit.gg or bore, which let a guest join with nothing installed even when the host is behind carrier-grade NAT. The name is resolved on the host, not in the invite, which keeps invites short. The cost is that a tunnel whose IP changes needs the host to restart hosting.
**Why:** UPnP is enabled by default on most consumer routers, so most hosts will need zero setup. Mono.Nat is MIT-licensed and targets netstandard. *Alternative:* Open.NAT, which is unmaintained.

### D6. Reading bones: unsafe Havok walk, first occurrence per name
`HavokPoseReader` (plugin) walks `Character* → DrawObject (CharacterBase*) → Skeleton → PartialSkeletons[i].GetHavokPose(0)`. For each bone, it derives the local-space T/R **from model space**: `local.pos = inv(parent.rot) · (model.pos − parent.pos)`, `local.rot = inv(parent.rot) · model.rot`. It also reads the model-space scale. *Found during implementation:* Ktisis gizmos edit `ModelPose` only, so `LocalPose` is stale for anything posed locally. This relation is the exact inverse of how Ktisis's `HavokPosing.SyncModelSpace` rebuilds model space (scale-free), so values round-trip through the writer. It keeps **only the first occurrence of each bone name, in partial order**, which is exactly what `ApplyAbsolutePoses` can address. Name tables are cached per `(objectIndex, partialIndex, skeletonResourceId)` and rebuilt when the partial changes. Values go into preallocated `BoneSample[]` buffers that Core diffs in place.
**Why:** Sending a later duplicate would be written onto the *first* one on the receiver. *Rejected:* diffing via `Ktisis.SavePose` JSON every tick, which is async, allocates heavily, and costs milliseconds.

### D7. Diff tolerances and grouping
A bone counts as changed when any of these hold: `|Δpos|∞ > 1e-4`, `1 − |dot(qa, qb)| > 1e-5`, or `|Δscale|∞ > 1e-4`. Changed bones for one actor go out in a single `BoneDelta` per tick. If more than 150 bones changed in one tick, the client sends a `FullSnapshot` of that actor instead.

### D8. Applying: IPC writer with in-flight exclusion
`KtisisIpcPoseWriter` builds `Matrix4x4.CreateScale(s) * CreateFromQuaternion(r) * CreateTranslation(p)` per bone and calls `ApplyAbsolutePoses` on the framework thread. While the task is in flight, those bones (or the whole actor, for a snapshot `LoadPoseExtended`) are marked **in-flight**, and the diff skips them. On the first tick after completion, the reader re-reads them and commits the actual Havok values into `SceneState.Value` without sending. This is DESIGN.md §6.3 echo suppression, adapted to an async writer. *Found during implementation:* `ApplyAbsolutePoses` writes `LocalPose` for the given bones and then rebuilds model space for **all** bones from `LocalPose`. Sending only the changed bones would revert every locally gizmo-posed bone to its stale `LocalPose`. The writer therefore sends the actor's full current pose (a fresh D6 read) with the received bones overriding it. Zero scales are clamped to 0.001 as Ktisis does, because a zero-scale matrix can't be decomposed. `ApplyAbsolutePoses` has no awaits and completes synchronously on the framework thread.

### D9. Snapshots
Snapshot bodies are `FullSnapshot{Actor, PoseJsonBrotli: byte[]}`: Ktisis `SavePose` JSON, Brotli-compressed to fit the 64 KB body limit. A snapshot is sent for **own** character when this client becomes ready and when the partner reports `Ready=true`. It's also sent for any actor through "Push pose", and in response to `SnapshotRequest(actor)` (only the owner of that character answers). Received snapshots follow the same pending/in-flight rules at actor granularity.

### D10. Actor registry, identity, threading, wiring
- **Registry:** `GposeActorRegistry` scans the GPose actor range of `IObjectTable` (mirroring Mare's `GetGposeCharacterFromObjectTableByName`) and matches on `(Name.TextValue, HomeWorld.RowId)`. It re-scans on GPose enter, on participant change, on `PosingChanged`, and on a lookup miss (throttled to once per second).
- **Identity:** the client id is `Guid.NewGuid()` per plugin load.
- **Threading:** socket loops run on the thread pool and push inbound envelopes into a `ConcurrentQueue`. `SceneSync.Tick()` runs from `IFramework.Update`. Each tick it (1) drains and applies the queue, (2) completes in-flight re-reads, and (3) at the 20 Hz throttle reads, diffs and sends. No Havok or IPC access happens off the framework thread.
- **Wiring:** keep the template's static `[PluginService]` pattern and `WindowSystem`, and drop `ConfigWindow`. Commands: `/copose`, `/copose host`, `/copose join <invite>` and `/copose leave`.

## Risks / Trade-offs

- [The host is behind carrier-grade NAT or has UPnP disabled, so the guest can't connect] → The status clearly shows "LAN/VPN only" with guidance. Either player can host, so swapping roles often fixes it. Tailscale is the documented fallback, and the manual public-address override supports it. A later change can add hole-punching or an optional public relay.
- [UPnP maps a port but the ISP or firewall still blocks inbound traffic] → The join error hint covers it. Windows Firewall will prompt on first listen for `ffxiv_dx11.exe`, and the UI tells the host to allow it on private/public networks as needed.
- [An open inbound port on the host] → A 64-bit random secret per session, constant-time compare, a 5 s hello timeout, one guest max, size checks before allocation, and a listener that exists only while hosting. The port mapping is removed on stop.
- [No transport encryption: pose data and the secret travel in plaintext] → Accepted for the MVP (low sensitivity, and a new secret each session). TLS with a pinned self-signed certificate hash in the invite is a straightforward follow-up.
- [Ktisis `ApplyAbsolutePoses` may fight an active gizmo drag on the receiver (DESIGN.md open Q1)] → The pending-local-wins rule means the receiver never applies to bones it's currently editing. Verify in manual testing.
- [Bones with duplicate names across partials only sync through their first occurrence] → Accepted (D6), and fixed by the later direct Havok writer.
- [A snapshot applied while the local user holds pending edits on that actor overwrites them] → Snapshots are rare, and the dragger's next diff re-sends the live gizmo value.
- [No auto-reconnect: a dropped connection ends the session] → The guest re-joins with the same invite while the host is still hosting.

## Migration Plan

Greenfield, nothing to migrate. Development flow: build, then load `CoPose/bin/x64/Debug/CoPose/CoPose.dll` as a dev plugin on both game clients. One clicks Host and the other pastes the invite. Two clients on the same PC can use the LAN address in the invite. Rollback is just disabling the plugin; stopping the host removes its UPnP mapping.

## Open Questions

- The exact GPose object-table index range in the current Dalamud build (DESIGN.md open Q2). Mirror Mare's lookup at implementation time.
- Whether `ApplyAbsolutePoses` completes synchronously when called on the framework thread. D8 handles either case.
- Whether Mono.Nat loads cleanly in Dalamud's plugin load context. If it doesn't, the fallback is a minimal SSDP + `AddPortMapping` SOAP client (about 200 lines, `HttpClient` + `UdpClient`) without changing the design.
