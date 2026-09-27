# CoPose — Shared-Control GPose Scenes for Ktisis

> Working name: **CoPose**. A standalone Dalamud plugin plus a tiny relay server.
> Two (later N) players in GPose can edit **any** actor in the shared scene, not just their own:
> bones, root position/rotation and full poses, all mirrored live on every participant's client.

Status: design, pre-implementation · Target: Dalamud API / `Dalamud.NET.Sdk` matching current Ktisis (`15.0.0`, `net10.0-windows` at time of writing — **check against the Ktisis `.csproj` when starting**).

---

## 1. Background: what exists today and why it isn't enough

| Tool | What it does | Limitation |
|---|---|---|
| **Player Sync / Mare "GPose Together"** (`CharaDataGposeTogetherManager.cs`) | Lobby over Mare's SignalR hub. Each client reads **its own** pose via `Brio.Actor.Pose.GetPoseAsJson` and pushes it (`GposeLobbyPushPoseData`). Receivers apply it to their local copy of you via Brio `LoadFromJson`. World transform is sent the same way. | **One-way ownership**: you only ever send your own character. Poll interval is **10 s**. Depends on Brio and on Mare's server (you can't add hub methods). Spawns a Brio clone with your Mare files. |
| **Lightless** | Mare fork; assumed to be the same lobby model (not checked, since its git host was unreachable). | Same as above. |
| **LivePose** | Brio fork for posing **outside** GPose. Serializes your pose into a SimpleHeels tag (`LivePose_v2`) on object 0; Mare/Lightless already sync heels tags, so the pose reaches others. | Self-only (you can only tag your own object). Update rate is bound to Mare's heels sync, so there's no live dragging. |
| **Ktisis** | The posing tool we build on. | Single-user. |

**What CoPose adds:** the scene is **shared state**. Any participant may edit any actor (subject to permissions). Edits stream at interactive rates (~20 Hz) instead of every 10 s.

## 2. Goals / Non-goals

### Goals (v1)
1. Two players standing **in the same instance/area**, both in GPose with Ktisis posing on, join a room with a code.
2. Either player can pose **either** character with Ktisis's normal gizmos. The other client sees it within ~100–200 ms.
3. Either player can move/rotate **either** character's root (actor world transform).
4. Full-pose operations (loading a `.pose` file onto the partner, resetting) propagate.
5. Per-partner permissions: "may pose me", "may move me".
6. Soft locking: while one player is dragging bones on an actor, the other player's edits to those bones are suppressed.
7. Clean leave/disconnect; no state left behind in Ktisis.

### Non-goals (v1)
- Players in different zones/instances (that would need Mare-style clone spawning + mod file transfer).
- Appearance sync. Assume Mare/Lightless/Player Sync is already running and handles it.
- Outside-GPose posing (LivePose territory; possible v3).
- Brio support (possible v2 adapter; the core is tool-agnostic by design).
- Syncing Ktisis undo history. Remote edits are not undoable locally.
- Camera, lights, props, expressions (v2+ candidates).

## 3. Key facts from the source (verified)

These are the hooks the design depends on. File paths are relative to each repo.

### Ktisis IPC — `Ktisis/Interop/Ipc/IpcProvider.cs` (API version `(1, 0)`)
| Name | Signature | Use |
|---|---|---|
| `Ktisis.ApiVersion` | `() → (int, int)` | Handshake / feature check |
| `Ktisis.IsPosing` | `() → bool` | Readiness |
| `Ktisis.PosingChanged` | event `bool` | Readiness changes |
| `Ktisis.RefreshActors` | `() → bool` | After actor list changes |
| `Ktisis.SavePose` | `(uint objectIndex) → Task<string?>` | Full snapshot (Ktisis pose JSON) |
| `Ktisis.LoadPose` | `(uint objectIndex, string json) → Task<bool>` | Apply full snapshot (rotation only) |
| `Ktisis.LoadPoseExtended` | `(uint, string, bool rot, bool pos, bool scale) → Task<bool>` | Apply full snapshot with chosen channels |
| `Ktisis.SelectedBones` | `() → Task<Dictionary<int objectIndex, HashSet<string boneName>>>` | **Soft-lock source** |
| `Ktisis.ApplyAbsolutePoses` | `(uint objectIndex, Dictionary<string, Matrix4x4>) → Task<bool>` | Writes **local** pos/rot and **model-space** scale per bone, then `SyncModelSpace` per partial |

Gotchas found in the Ktisis source:
- `ApplyAbsolutePoses` looks bones up with `EntityPose.FindBoneByName`, which returns the **first** match across all partial skeletons. Partial skeletons (face, hair, tail…) repeat their connection bone names, so a bone that appears in two partials can only be addressed in the first one through this IPC. **Consequence:** v1 may use this IPC; v1.1 writes Havok directly, keyed by `(partialIndex, boneIndex)` (see §6.4).
- `GetEntityForIndex` matches on `ObjectIndex`, and GPose actor indices are **different on each client**. Never put object indices on the wire.
- Posing mode is required: Ktisis hooks `AnimFrozen`, `UpdatePos`, `SetBoneModelSpace`, etc. With posing **off**, the animation system overwrites whatever we write.
- Actor root transform: Ktisis `CharaEntity.SetTransform` → base entity writes the **DrawObject**'s `Position/Rotation/Scale`. When "update actor camera positions" is on, `ActorEntity.UpdateGameObjectTransform` then mirrors that to the GameObject. We write the DrawObject the same way. Watch for attachments (`CharaEntity` handles an `Attach` case; skip attached actors in v1).
- Havok access pattern (`Editor/Posing/HavokPosing.cs`, `Scene/Entities/Skeleton/EntityPose.cs`): `character->Skeleton` (`RenderSkeleton*`) → `PartialSkeletons[i].GetHavokPose(0)` (`hkaPose*`) → `LocalPose.Data[boneIdx]` / `ModelPose.Data[boneIdx]` (`hkQsTransformf`). Bone names come from `pose->Skeleton->Bones[boneIdx].Name`.

### Brio IPC (for reference / future adapter) — `Brio/BrioAPI_V2.cs`
`Brio.Actor.Pose.GetPoseAsJson`, `Brio.Actor.Pose.LoadFromJson`, `Brio.Actor.SetModelTransform`, `Brio.Actor.GetModelTransform`, `Brio.Actor.Spawn*`.

### Mare GPose Together protocol (for reference) — `MareSynchronosAPI/SignalR/IMareHub.cs`
`GposeLobbyCreate/Join/Leave`, `GposeLobbyPushCharacterData`, `GposeLobbyPushPoseData`, `GposeLobbyPushWorldData`. Worth copying: delta + periodic full resend, `ForceResend` on GPose enter/exit, reconnect-rejoin.

## 4. Architecture

```
┌──────────────── Client A (Dalamud) ────────────────┐        ┌─────────── Relay ───────────┐        ┌──── Client B ────┐
│ Ktisis  ◄─IPC─►  KtisisAdapter                      │        │  ASP.NET Core SignalR hub   │        │   (mirror of A)  │
│                     │                               │        │  rooms by code              │        │                  │
│ Havok  ◄─unsafe─► PoseReader / PoseWriter           │        │  forwards messages,         │        │                  │
│                     │                               │  WSS   │  stamps sender + server seq │  WSS   │                  │
│               SceneSync (core) ◄──── RelayClient ◄──┼───────►│  no game logic              │◄───────┼─►                │
│                     │                               │        └─────────────────────────────┘        │                  │
│               ActorRegistry  (ActorKey ⇄ local idx) │                                               │                  │
│               PermissionService, LockService        │                                               │                  │
│               UI (ImGui window)                     │                                               │                  │
└─────────────────────────────────────────────────────┘                                               └──────────────────┘
```

Principle: **replicated scene, last-writer-wins per bone, soft locks on top.** No client is authoritative for "its own" actor. The relay stays dumb: it only orders and forwards.

### 4.1 Project layout
```
CoPose.sln
├─ CoPose/                      (Dalamud plugin)
│  ├─ Plugin.cs                 DI root, command /copose
│  ├─ Config/Configuration.cs
│  ├─ Interop/
│  │  ├─ KtisisIpc.cs           typed subscribers + availability/version check
│  │  ├─ HavokPoseAccess.cs     unsafe read/write of local pose & root transform
│  │  └─ GposeActors.cs         enumerate GPose actors, resolve ActorKey → IGameObject
│  ├─ Sync/
│  │  ├─ ActorKey.cs            record struct (Name, HomeWorldId)
│  │  ├─ BoneId.cs              record struct (PartialSlot, BoneName, Occurrence)
│  │  ├─ SceneState.cs          per-actor snapshot: bones, root, per-bone version
│  │  ├─ SceneSync.cs           tick loop: diff → send; receive → apply
│  │  ├─ LockService.cs         local selections → leases; remote leases → filter
│  │  └─ PermissionService.cs
│  ├─ Net/
│  │  ├─ RelayClient.cs         SignalR client, reconnect, rejoin
│  │  └─ Messages.cs            shared DTOs (link from CoPose.Protocol)
│  └─ UI/MainWindow.cs
├─ CoPose.Protocol/             netstandard2.1 / net10 class lib, DTOs + MessagePack attrs
└─ CoPose.Relay/                ASP.NET Core minimal app, SignalR hub
```

## 5. Identity & coordinate model

### 5.1 ActorKey
`ActorKey = (string Name, ushort HomeWorldId)`, taken from `IPlayerCharacter.Name` and `HomeWorld.RowId`.
- Each client builds `ActorRegistry: ActorKey → local GPose object index` by scanning the GPose actor range of the object table (the same range Mare scans with `GetGposeCharacterFromObjectTableByName`). Rebuild on GPose enter, on `Ktisis.RefreshActors`, and whenever a lookup misses.
- If a key is missing locally (partner out of range), mark the actor "absent" in the UI and drop edits for it. Never guess.

### 5.2 Bone identity
`BoneId = (byte PartialSlot, string BoneName)`, where `PartialSlot` is the partial skeleton index in `RenderSkeleton.PartialSkeletons`.
- Index 0 is always the body. Other slots depend on equipped models, which normally match across clients because Mare syncs appearance. On the wire, send `PartialSlot` together with the **partial's skeleton ID** (Ktisis `GetPartialId`; mirror that logic) and drop edits where the ID differs.
- In v1 (IPC path), bones are sent by name only and `PartialSlot` is ignored; the first-match behaviour is accepted.

### 5.3 Coordinates
- **Bones:** local-space transforms (parent-relative). They don't depend on world position or the client, which is exactly what Ktisis's `ApplyAbsolutePoses` consumes. Scale is the exception: Ktisis writes it into model space to avoid racial offsets. Match that (§6.4).
- **Roots:** positions are **relative to a room anchor** so small per-client drift is irrelevant. Anchor = the room creator's actor root transform at join time, re-sent on request. `wire = inverse(anchor) * worldRoot`; `apply = anchorLocal * wire`, where `anchorLocal` is the anchor actor's root on the receiving client.

## 6. Core algorithms

### 6.1 Tick loop (framework thread)
Runs from `IFramework.Update`, throttled to `SendHz` (default 20). All Havok reads/writes happen on the framework thread.

```
if !(InGpose && KtisisPosing && Room.Connected) return
for actor in registry.PresentActors:
    live = PoseReader.ReadLocal(actor)            // bones + root
    changed = diff(live, state[actor], epsPos=1e-4, epsRot=1e-5 (1-|dot|), epsScale=1e-4)
    changed -= locks.RemoteHeld(actor)             // don't fight a remote drag
    changed = permissions.FilterOutgoing(actor, changed)
    if changed.any:
        seq = ++localSeq
        send BoneDelta(actor, seq, changed values)
        state[actor].Commit(changed, version=(seq, myClientId))
```
`permissions.FilterOutgoing` means: if I'm not allowed to pose the partner, my local edits to them aren't sent, and the next resync snaps them back.

### 6.2 Receive
```
on BoneDelta d:
    if !permissions.AllowIncoming(d.Sender, d.Actor, kind=Bones) return
    idx = registry.Resolve(d.Actor) ?? return
    accepted = d.Bones.where(b => d.Version > state[d.Actor].Version(b))   // LWW
    accepted -= locks.LocalHeld(d.Actor)                                    // I'm dragging; mine wins
    queue ApplyBones(idx, accepted)            // applied on the next framework tick
    state[d.Actor].Commit(accepted, d.Version) // echo suppression: the diff won't see it as a local change
```
Versions are ordered by `(serverSeq, clientId)`. The relay stamps a monotonically increasing `serverSeq` per room, which gives a total order without clock sync.

### 6.3 Echo suppression
After `ApplyBones`, re-read the applied bones and store **the values Havok actually holds** in `state` (not the values that were received). This absorbs float round-trip noise and scale remapping, so the next diff reads as clean. Apply and commit in the same tick, before the diff runs.

### 6.4 Writing bones — two backends behind `IPoseWriter`
1. **`KtisisIpcPoseWriter` (v1):** `Ktisis.ApplyAbsolutePoses(idx, Dictionary<string, Matrix4x4>)`, matrices built with `Matrix4x4.CreateScale(s) * CreateFromQuaternion(r) * CreateTranslation(p)`. Pros: zero reverse-engineering. Cons: first-match bone names, and it's an async `Task` (await it on the framework thread, or fire it and re-read on the next tick).
2. **`HavokDirectPoseWriter` (v1.1):** port the ~40 lines of `ApplyAbsolutePoses` (write `LocalPose` translation/rotation → `HavokPosing.SyncModelSpace` per partial → write `ModelPose` scale), keyed by `(partial, boneIndex)`. This removes the duplicate-name problem. Copy the `SyncModelSpace` approach from `Ktisis/Editor/Posing/HavokPosing.cs` (keep the license attribution; Ktisis is GPL-3.0, so CoPose must be GPL-compatible if it copies code).

Pick the backend with a config flag so they can be A/B tested.

### 6.5 Reading bones — `PoseReader`
Unsafe walk: `Character* → DrawObject → CharacterBase->Skeleton` → for each partial `GetHavokPose(0)` → for each bone read `LocalPose[i]` (T, R) and `ModelPose[i].Scale`. Cache the name table per `(actor, partialId)`; rebuild it when the partial ID changes (gear swap). Allocation-free hot path: preallocated arrays, compare in place.

Budget: ~2 actors × ~250 bones × 10 floats at 20 Hz is trivial. Reading and diffing must stay under 0.2 ms per tick.

### 6.6 Root transform
Read and write `DrawObject->Object.Position/Rotation/Scale` (the same field Ktisis's entity `SetTransform` uses). Send as `RootDelta` using the same LWW scheme with a single pseudo-bone `__root`. Skip actors with an active attach (Ktisis `CharaEntity.GetAttach()->IsActive()`), and mirror to the GameObject position only if needed for camera targeting (optional).

### 6.7 Soft locks
- Every 100 ms: `Ktisis.SelectedBones()` → map object index → ActorKey → the set of bone names I currently hold.
- A selection alone isn't a drag. Only **assert a lease** for bones that changed locally in the last 500 ms **and** are selected. Send `LockLease(actor, bones, ttlMs=750)`, and renew it while the drag continues.
- Remote leases expire on their own (TTL), so a crashed client can't hold a lock forever.
- UI: show "🔒 <partner> is posing <actor>: <n> bones".

### 6.8 Full-pose operations & resync
- `FullSnapshot(actor, poseJson)` uses `Ktisis.SavePose(idx)`. It's sent on: join, GPose enter, partner reconnect, explicit "Push pose to partner", and every 30 s while the scene has changes (safety net, like Mare's 12-cycle full resend).
- On receive: `Ktisis.LoadPoseExtended(idx, json, rot:true, pos:true, scale:true)`, then re-read everything into `state` (§6.3).
- Loading a `.pose` file onto the partner locally is just a big diff; the tick loop sends it without special handling. Optionally send it as a snapshot when the diff has more than 100 bones.

### 6.9 Readiness
A participant is **ready** when they're in GPose, `Ktisis.IsPosing` is true, and all room ActorKeys resolve locally. Broadcast `Presence(ready, actorsResolved[])` on change. Don't send or apply bone deltas to a client that isn't ready; send it a snapshot once it becomes ready.

## 7. Permissions
Stored per partner (by ActorKey) in plugin config and exchanged on join:

| Permission | Default | Effect |
|---|---|---|
| `AllowPoseMe` | ask on first join | Accept bone deltas/snapshots targeting **my** ActorKey from this partner |
| `AllowMoveMe` | ask on first join | Accept root deltas targeting my ActorKey |
| `AllowPoseOthers` | on | Accept edits from this partner to third-party actors (N-player rooms, v2) |

Enforcement is **receiver-side only** (never trust the sender). The sender also filters so its UI can show "partner doesn't allow this" instead of silently snapping back.

## 8. Wire protocol

Transport: SignalR over WSS, MessagePack protocol (`Microsoft.AspNetCore.SignalR.Protocols.MessagePack`). Separate DTO library shared by client and relay.

### Client → Relay (hub methods)
| Method | Payload |
|---|---|
| `CreateRoom(ClientHello)` → `RoomInfo` | `ClientHello{ProtocolVersion, ClientId(Guid), Self: ActorKey, DisplayName}` |
| `JoinRoom(code, ClientHello)` → `RoomInfo` | |
| `LeaveRoom()` | |
| `Broadcast(Envelope)` | opaque `byte[]` body + `MsgType`; the relay stamps it |

### Relay → Client
| Method | Payload |
|---|---|
| `OnPeerJoined(PeerInfo)` / `OnPeerLeft(ClientId)` | |
| `OnMessage(Envelope)` | `{Type, SenderId, ServerSeq, Body}` |

### Message bodies (`MsgType`)
```csharp
record BoneDelta(ActorKey Actor, BoneTransform[] Bones, bool IsRoot);
record BoneTransform(byte Partial, uint PartialId, string Name,
                     Vector3 Pos, Quaternion Rot, Vector3 Scale);   // ~50 bytes each
record FullSnapshot(ActorKey Actor, string KtisisPoseJson, RootXf Root);
record LockLease(ActorKey Actor, string[] Bones, ushort TtlMs);
record Presence(bool Ready, ActorKey[] Resolved, Permissions Granted);
record AnchorSet(ActorKey AnchorActor);
record SnapshotRequest(ActorKey Actor);
```
Bone names can be interned per room later (name → ushort table sent once) if bandwidth ever matters. It won't at 2 players.

Rate limits (relay side): max 60 msgs/s per client, max 64 KB per message, max 8 clients per room.

## 9. Relay server (`CoPose.Relay`)
- ASP.NET Core minimal API + one `Hub`. In-memory `ConcurrentDictionary<string code, Room>`; `Room` holds its peers and a `long seq` (`Interlocked.Increment`).
- Room codes: 6 characters, Crockford base32. Rooms are deleted when empty for 60 s.
- Stateless otherwise: no persistence, no accounts in v1. Optional shared-secret header (`X-CoPose-Key`) for private deployments.
- Run locally with `dotnet run` on `http://localhost:5157` for development. Deploy with a Dockerfile behind Caddy (automatic TLS) on any small VPS.
- Reconnect: the client keeps `ClientId` and `RoomCode` and rejoins automatically (Mare's `ConnectedMessage → JoinGPoseLobby(isReconnecting)` pattern). Peers get `OnPeerJoined` again and send snapshots.

## 10. UI (ImGui)
- **Connection:** relay URL, Create / Join (code field), Leave, status light.
- **Room:** peers with ready state, resolved actors, latency (ping via SignalR).
- **Per-partner permissions:** toggles, with a first-join consent prompt.
- **Actors:** list of synced actors with 🔒 lock indicators, plus "Push full pose", "Request resync" and "Set as anchor".
- **Debug tab:** sent/received msgs/s, bones per delta, apply time, last error.

Commands: `/copose`, `/copose join <code>`, `/copose leave`.

## 11. Implementation plan (milestones with acceptance criteria)

**M0 – Skeleton plugin + Ktisis IPC probe**
- Plugin loads, window opens, `Ktisis.ApiVersion` and `IsPosing` shown live, `PosingChanged` event logged.
- ✅ Toggling Ktisis posing updates the UI instantly.

**M1 – Local-only pose reader/writer (no network)**
- `PoseReader` dumps actor bones; the debug button "copy pose from actor A to actor B" uses `IPoseWriter`.
- A "loopback" mode runs the diff → fake-send → apply path onto a **second local GPose actor** (e.g. a Ktisis/Brio-spawned clone of yourself). This exercises the whole sync pipeline without a second player.
- ✅ Dragging a bone on A moves the same bone on B with no drift or oscillation; no echo loop (debug counters show 0 sends while idle).

**M2 – Relay + two clients**
- `CoPose.Relay` runs locally; two game clients (or one client plus a headless test client replaying recorded deltas) join the same room.
- ActorKey registry and bone deltas both ways.
- ✅ Two players side by side: each can drag bones on both characters and it mirrors in under 200 ms on a LAN relay.

**M3 – Root transforms + anchor**
- ✅ Moving either actor's root with the Ktisis gizmo mirrors correctly, even when the two clients' GPose positions differ slightly.

**M4 – Snapshots, readiness, reconnect**
- ✅ Joining mid-scene gets the full current pose. Killing the network for 10 s and restoring it converges both clients. Toggling Ktisis posing off and on resyncs.

**M5 – Locks + permissions**
- ✅ Simultaneous drags on the same bone: the holder wins and the other side's gizmo doesn't fight it. Denying `AllowPoseMe` makes partner edits to me snap back with a UI notice.

**M6 – `HavokDirectPoseWriter`**
- ✅ Bones with duplicate names across partials (e.g. face partial root) sync correctly; a parity test against the IPC writer passes on body bones.

**M7 – Hardening & release**
- Gear/partial changes mid-session, actors leaving GPose range, Ktisis unloaded mid-session, relay rate limits, Docker deploy.

## 12. Testing
- **Unit (no game):** diff epsilon logic, LWW versioning, lock TTL expiry, anchor math (round-trip `inverse(anchor)*x`), DTO serialization. Plain xUnit project against `CoPose.Protocol` and `Sync/` classes kept free of Dalamud types behind interfaces (`IPoseReader`, `IPoseWriter`, `IActorRegistry`).
- **Relay:** integration tests with `WebApplicationFactory` + two `HubConnection`s: ordering, rate limits, room cleanup.
- **In-game loopback (M1):** the main tool for debugging without a partner.
- **Replay client:** a console app that joins a room and replays a recorded delta stream, so one person can test the two-client path.
- **Manual checklist:** different races/heights (Customize+ scaling), weapon drawn vs sheathed (weapon skeletons are separate objects, so they're out of scope v1), actor with an attachment, housing vs open world, high latency (relay on a remote VPS).

## 13. Risks & open questions
| Risk | Mitigation |
|---|---|
| Ktisis IPC changes (API `1.0` is young; `ApplyAbsolutePoses` is recent) | Version-check on load; isolate in `KtisisIpc.cs`; M6 removes the hot-path dependency on it |
| Duplicate bone names across partials | §6.4 direct writer keyed by partial/bone index |
| Partner has a different skeleton setup (mods adding bones, different partials) | Drop unknown bones; verify `PartialId` on the wire; log mismatches in the debug tab |
| Customize+ / racial scaling makes identical local values look different | Accept for v1 (Ktisis already scales in model space); document |
| Posing off on one side, so animation overwrites applied values | Readiness gating (§6.9) + snapshot on ready |
| GPL: copying Ktisis code | License CoPose GPL-3.0 (Ktisis and Brio are both GPL-3.0, verified) or only call IPC |
| Weapons/props | Out of scope v1. Mare syncs `MainHand/OffHand` bone sets; v2 could do the same via their separate objects |
| Abuse (a partner moving you without consent) | Receiver-side permissions, consent prompt, per-room only, nothing persists |

Open questions to settle during M0–M1:
1. Does calling `ApplyAbsolutePoses` every tick interfere with an **active** Ktisis gizmo drag on the same bone on the receiving side? (The lock design assumes the local drag wins; confirm Ktisis doesn't cache the drag start transform in a way that snaps back.)
2. Which object-table range holds GPose actors in the current Dalamud version? (Mirror Mare's `GetGposeCharacterFromObjectTableByName`.)
3. Is DrawObject root position enough for Ktisis's gizmos to follow, or must the GameObject be updated too (`UpdateActorCameraPositions` path)?
4. Would Ktisis maintainers accept a PR adding partial-aware IPC (`ApplyPartialPoses(uint idx, (int partial, int bone)[] ...)`) and a transform IPC? That would let CoPose stay IPC-only.

## 14. Future (v2+)
- **Brio adapter:** implement `IPoseReader/IPoseWriter` over `Brio.Actor.Pose.*` and `Brio.Actor.Set/GetModelTransform`.
- **LivePose / outside GPose:** the same sync core on top of LivePose's `LivePose.GetPose/SetPose` IPC.
- **Scene extras:** expressions, weapon/prop bones, Ktisis lights and camera.
- **N-player rooms** and spectator mode.
- **Serverless fallback:** encode commit-style updates (not live drags) in a SimpleHeels tag, the way LivePose does, for users without the relay.

## 15. Reference files
- Mare/Player Sync: `PlayerSync/Services/CharaData/CharaDataGposeTogetherManager.cs`, `PlayerSync/Interop/Ipc/IpcCallerBrio.cs`, `MareSynchronosAPI/SignalR/IMareHub.cs`
- Ktisis: `Ktisis/Interop/Ipc/IpcProvider.cs`, `Ktisis/Editor/Posing/HavokPosing.cs`, `Ktisis/Editor/Posing/PosingModule.cs` (hooks), `Ktisis/Scene/Entities/Skeleton/EntityPose.cs`, `Ktisis/Scene/Entities/Character/CharaEntity.cs`, `Ktisis/Scene/Entities/Game/ActorEntity.cs`, `Ktisis/Scene/SceneManager.cs`
- Brio: `BrioAPI_V2.cs`
- LivePose: `LivePose/IPC/HeelsService.cs`, `LivePose/IPC/IpcService.cs`
