## 1. Solution scaffolding

- [x] 1.1 Rename `SamplePlugin/` → `CoPose/` (folder, `.csproj`, namespace, `SamplePlugin.slnx` → `CoPose.slnx`). Set the manifest fields (Name "CoPose", Author, Punchline, Description, Tags) and remove the `goat.png` content item and image usage. Verify `dotnet build CoPose.slnx -c Debug` succeeds and produces `CoPose/bin/x64/Debug/CoPose.dll` (the Dalamud SDK dev-output path; the packaged zip goes to `CoPose/bin/x64/<Config>/CoPose/`).
- [x] 1.2 Add `CoPose.Protocol` (net10.0 class lib, `MessagePack` package), `CoPose.Core` (net10.0, references Protocol) and `CoPose.Tests` (xUnit, references Protocol and Core) to the solution, with x64 platform mapping where needed. Verify `dotnet build` and `dotnet test` (empty) both succeed.
- [x] 1.3 Reference Protocol and Core from `CoPose`, and add the `Mono.Nat` package. Verify the built plugin output folder contains the MessagePack, Mono.Nat, Protocol and Core DLLs, and that no ASP.NET Core assemblies are referenced.
- [x] 1.4 Update `.github/workflows/pr-build.yml` for the new solution name, artifact path and a `dotnet test` step. Verify the YAML paths match the built output.

## 2. Protocol

- [x] 2.1 In `CoPose.Protocol`, define `ProtocolVersion` const, `ActorKey(Name, HomeWorldId)`, `PeerInfo`, `MsgType` enum, `Envelope(Type, SenderId, Seq, Body)`, `BoneTransform` (name + flat pos/rot/scale floats), `BoneDelta(Actor, LocalSeq, Bones)`, `FullSnapshot(Actor, PoseJsonBrotli)`, `Presence(Ready, Resolved[])`, `SnapshotRequest(Actor)`, and the `Frame` union (`Hello`, `Welcome`, `Reject`, `PeerJoined`, `PeerLeft`, `Send`, `Deliver`, `Ping`, `Bye`) with reject reason codes (design D2). Use MessagePack `[Key]`/`[Union]` attributes. Verify with round-trip serialization tests for every type.
- [x] 2.2 Implement the frame codec: `uint32` little-endian length prefix + MessagePack payload, async read/write over `Stream`, rejecting length > 128 KB before allocation. Verify with tests for round-trip over a `Pipe`/`MemoryStream`, partial reads, and oversized length rejected without allocating the body.
- [x] 2.3 Implement the `Invite` codec (D4): version, port, 8-byte secret, 1–2 IPv4 addresses, CRC-8, Crockford base32 with `CP1-` prefix and 4-character groups, tolerant decoding (case, hyphens, whitespace, `O→0`, `I/L→1`). Verify with tests for round-trip, a single-character typo caught by CRC, a wrong prefix/version rejected, and decoding of lowercase and extra whitespace.
- [x] 2.4 Add Brotli helpers for snapshot JSON compress/decompress. Verify with a round-trip test on a ~80 KB sample Ktisis pose JSON that also asserts the compressed size is < 64 KB.

## 3. Session host and client (`CoPose.Core`)

- [x] 3.1 Implement `Connection` (one reader loop + one writer loop fed by `Channel<Frame>`, `NoDelay`, ping after 5 s of outbound idle, close after 15 s of inbound silence, graceful `Bye`). Verify with a loopback TCP test that an idle connection stays alive via pings and a silent peer is dropped at ~15 s (with the timeouts injectable for a fast test).
- [x] 3.2 Implement `SessionHost`: listen on a configurable port on `0.0.0.0` (clear error on port in use), generate a per-session secret, a 5 s hello timeout, constant-time secret check, a version check, at most one guest (`Reject` reasons: `invalid_invite`, `version_mismatch`, `session_full`), `Welcome` with participants, and `PeerJoined`/`PeerLeft` to the host's own side. Verify with loopback tests for each scenario in `specs/relay-rooms` (host started, port in use, successful join, wrong secret, no hello, version mismatch, session full, guest leaves, silent connection, stop hosting disconnects guest and old invite fails).
- [x] 3.3 Implement the host `Sequencer` (D3): a single-consumer channel of `Send`s from the guest connection and the host's local loopback transport. It assigns `++Seq` and fans `Deliver` out to every participant including the sender, and rejects bodies > 64 KB. Verify with a loopback test in which host-local and guest each send 1,000 envelopes concurrently, and both observe identical, strictly increasing sequences that include their own echoes.
- [x] 3.4 Implement `SessionClient` (decode invite, try addresses in order with a 3 s connect timeout each, send `Hello`, handle `Welcome`/`Reject`, surface "could not reach host" / reject reasons / session ended) and a `LocalHostTransport` giving the host the same `ISessionTransport` interface without a socket. Verify with loopback tests: the first bad address falls through to the second, a rejected join surfaces its reason, and host stop surfaces "session ended".

## 4. Sync core (`CoPose.Core`)

- [x] 4.1 Define the interfaces `IPoseReader` (fill preallocated `BoneSample[]` + names for an actor), `IPoseWriter` (apply bones / apply snapshot → `Task<bool>`, export snapshot → `Task<string?>`), `IActorRegistry` (resolve `ActorKey` → handle, own key, present keys), `ISessionTransport` (send envelope, inbound event, participant events) and `IClock`. Verify the project builds with no Dalamud references.
- [x] 4.2 Implement `PoseDiff` with the D7 tolerances over preallocated buffers. Verify with unit tests for each threshold edge, for quaternion sign flip (q vs −q = unchanged) and for zero allocations in the steady state (a `GC.GetAllocatedBytesForCurrentThread` check).
- [x] 4.3 Implement `SceneState` per actor/bone: committed value, `PendingLocalSeq` and in-flight flag. Include the D3 rules (own echo clears matching pending; remote skips pending bones) and the D8 rules (in-flight bones excluded from diff, commit-after-reread). Verify with unit tests for each rule.
- [x] 4.4 Implement `SceneSync.Tick()`: drain the inbound queue → apply via writer (mark in-flight) → complete in-flight re-reads → at 20 Hz read + diff + send `BoneDelta` per actor (or `FullSnapshot` if > 150 bones changed). Gate everything on readiness, and drop data for unresolved actors and unknown bone names. Verify with unit tests using fake reader/writer/transport: a local change sends once, an idle state sends nothing, a remote apply doesn't echo back, and not-ready sends/applies nothing.
- [x] 4.5 Implement snapshot and presence logic (D9): broadcast `Presence` on join, on participant join and on readiness change; send own-character snapshot on becoming ready and when the partner reports ready; answer `SnapshotRequest` only for own actor; "push pose" for any resolved actor. Verify with unit tests for each trigger.
- [x] 4.6 Add a two-client convergence test: a `SessionHost` + `SessionClient` over loopback TCP, each driving a `SceneSync` with a fake skeleton, making concurrent edits to the same bones. Verify both fake skeletons end identical and equal to the last-sequenced value, and that traffic drops to zero (pings aside) after edits stop.
- [x] 4.7 Add diagnostics counters in `SceneSync` (sent/received msgs per second excluding own echoes, average bones per delta, last error). Verify with a unit test using the fake clock.

## 5. Plugin interop

- [ ] 5.1 Implement `KtisisIpc` (`ApiVersion`, `IsPosing`, `PosingChanged`, `SavePose`, `LoadPoseExtended`, `ApplyAbsolutePoses`) with an `Available` flag that is true only when Ktisis is loaded and reports API major version 1. Verify in-game that the window shows the Ktisis version and a live posing state that flips when posing is toggled, and "Ktisis not available" with Ktisis disabled. _(Code complete and builds; awaiting the in-game verification step.)_
- [ ] 5.2 Implement `GposeActorRegistry : IActorRegistry` (scan the GPose object-table range for player characters, key by name + home world, own key from the local player, re-scan triggers from D10, 1 s miss throttle). Verify in-game that the window lists both characters' keys as resolved when both players are in GPose, and the partner as absent when out of range. _(Code complete and builds; awaiting the in-game verification step.)_
- [ ] 5.3 Implement `HavokPoseReader : IPoseReader` (unsafe walk from D6, synced local T/R + model scale, first occurrence per bone name, name-table cache keyed by partial resource). Enable `AllowUnsafeBlocks` if the SDK doesn't. Verify in-game with a debug button that logs the bone count and a few named bone values for each resolved actor, and that the debug panel shows read time < 0.2 ms per tick. _(Code complete and builds; awaiting the in-game verification step.)_
- [ ] 5.4 Implement `KtisisIpcPoseWriter : IPoseWriter` (matrix composition from D8 → `ApplyAbsolutePoses`; snapshots via `SavePose` / `LoadPoseExtended(rot,pos,scale=true)`). Verify in-game with a debug button that copies own pose onto the partner's local actor via the writer and shows it matches. _(Code complete and builds; awaiting the in-game verification step.)_

## 6. Plugin networking, wiring and UI

- [ ] 6.1 Implement `UpnpMapper` (D5): Mono.Nat discovery with a 5 s cap, TCP mapping `port→port` "CoPose" with a 1 h lease renewed every 30 min, external IP read and classified public vs private/CGNAT, LAN IP from the default-gateway NIC, and synchronous removal on stop/dispose (2 s cap). If Mono.Nat fails to load in Dalamud, replace it with a minimal SSDP + SOAP `AddPortMapping`/`DeletePortMapping` client. Verify in-game on a UPnP-enabled router that the mapping appears in the router's UPnP table while hosting and disappears after Stop, and that the status shows "LAN/VPN only" when UPnP is disabled on the router. _(Code complete and builds; awaiting the in-game verification step.)_
- [ ] 6.2 Wire `Plugin.cs`: construct the services, subscribe `SceneSync.Tick` to `IFramework.Update`, register `/copose`, `/copose host`, `/copose join <invite>` and `/copose leave`, and dispose everything (leave/stop session, close sockets, remove UPnP mapping, unhook) in `Dispose`. Verify that disabling the plugin while hosting makes the guest see "session ended", removes the mapping, and leaves no errors in `/xllog`. _(Code complete and builds; awaiting the in-game verification step.)_
- [ ] 6.3 Build `MainWindow`:
  - Host port (persisted in `Configuration`, default 47715) and an optional manual public address.
  - Host / Join (invite field) / Leave.
  - Invite code with a copy button.
  - Reachability status with guidance text (UPnP / LAN-VPN only / CGNAT / Windows Firewall hint).
  - Connection errors, participants with ready state, actors with resolved/absent state.
  - Per-actor "Push pose", "Request resync".
  - A debug section with the counters.

  Remove `ConfigWindow`. Verify in-game that each control works and the port persists across a plugin reload. _(Code complete and builds; awaiting the in-game verification step.)_

## 7. End-to-end verification

- [ ] 7.1 Manual two-client test (two game clients in the same instance, both in GPose with Ktisis posing on). Run it once with both clients on the same LAN (invite's LAN address) and, if possible, once over the internet via UPnP or Tailscale. Check each `pose-sync` scenario: pose partner, pose self, mirror within ~200 ms, zero traffic when idle, concurrent drags converge, join mid-session receives pose, push pose after loading a `.pose` on the partner, request resync, root moves not synced, posing off → not ready. Also check the `pairing` host/join/leave and reachability scenarios. Record results and any issues in the change folder as `verification.md`.

## 8. Tunnel support (host-only reachability)

- [x] 8.1 Change the invite to `CP2`: one to three endpoints, each an IPv4 address with its own port (LAN, then user-entered public/tunnel, then UPnP public). Update `SessionHost.CreateInvite` and `SessionClient` to use per-endpoint ports. Verify with invite round-trip/typo/malformed tests and a loopback test where the endpoints use different ports.
- [ ] 8.2 Accept `address[:port]` (IPv4 or host name) in the public address field, resolve it to IPv4 when hosting starts, always include it in the invite, and show parse/resolve errors in the window. Verify with unit tests for the parser and resolver (IP, IP:port, name:port, bad input) and an in-game host through a playit.gg or bore tunnel that a guest joins with nothing installed. _(Code and unit tests complete; awaiting the in-game tunnel test.)_
