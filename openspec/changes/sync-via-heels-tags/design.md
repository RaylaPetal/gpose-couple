## Context

See proposal.md for why hosting is being replaced. v0.1.0 (archived change `2026-09-26-add-pairing-pose-sync-mvp`) already provides a Havok bone reader, a Ktisis IPC writer that sends full poses, diffing, echo suppression by re-reading after applies, GPose actor lookup by name and world, and readiness gating. Those stay.

What the source of the channel's two hops shows:

**SimpleHeels** (`Caraxi/SimpleHeels`, `ApiProvider.cs`, API 2.5):
- IPC: `SimpleHeels.ApiVersion () -> (int,int)`, `SimpleHeels.SetTag (int objectIndex, string tag, string value)`, `SimpleHeels.GetTag (int objectIndex, string tag) -> string?`, `SimpleHeels.RemoveTag (int objectIndex, string tag)`, and the event `SimpleHeels.TagChanged (int objectIndex, string tag, string? value)`.
- Tags can be set on any player character, but only tags on **object index 0** trigger `OnChanged`, which is what reports local data to sync services.
- `OnChanged` restarts a **250 ms** timer on every call before it sends `SimpleHeels.LocalChanged`. The payload is the whole local config, tags included.
- For a remote player, `RegisterPlayer(index, data)` (called by the sync service) diffs the incoming tags and raises `TagChanged` for each changed or removed tag.

**Player Sync** (`universalconquistador/MareSynchronosClient`):
- A heels change becomes `PlayerChanges.Heels`. `CacheCreationService.AddPlayerAddonPluginChangesToUpdate` restarts a **100 ms** timer, then the "addon plugin changes" fast path pushes to visible paired users.
- On the receiving side, `PairHandler.HandleOptionalPluginDataAsync` applies heels data **with no GPose check**. The full character-data path is blocked in GPose (`PairHandler.cs:299`), but heels data goes through the fast path.
- The fast path only applies once the pair has cached data (`_cachedData`), meaning the two players were synced normally before entering GPose. That's the usual case.
- Lightless is a fork of the same client and is expected to behave the same. That's unverified.

Not yet known: how large a tag Player Sync's server accepts, and whether it throttles frequent addon updates. Task group 1 measures both before the rest is built.

## Goals / Non-Goals

**Goals:**
- Zero network setup: if both players already sync through Player Sync or Lightless, CoPose works.
- Both characters are editable by both players, and the clients converge without any central ordering.
- Low load on the community-run sync server: compressed state, changed-only publishing, at most 2 publishes per second.

**Non-Goals:**
- Smooth live dragging. Updates arrive in steps, roughly every 0.5–1.5 s.
- Players who don't use a sync service. There is no fallback transport in this change.
- More than two players, root/world transforms, locks and permissions (unchanged from v0.1.0).

## Decisions

### D1. Channel: one `CoPose` tag on object 0; read by event and by scan
The local tag is always written to object index 0 (never a GPose copy), because only that one is reported to the sync service. Partner tags come from `TagChanged`, on whichever overworld object index SimpleHeels reports. A discovery scan every 2 s calls `GetTag(i, "CoPose")` for player objects in the overworld range (index < 200). It catches players whose tags were delivered before CoPose subscribed or loaded. Tag data is attributed to the overworld character's `ActorKey` (name, home world), which the existing registry maps to the GPose actor by name.
*Alternative:* a tag per actor on the GPose copies. Rejected, because tags on anything other than object 0 are never sent.

### D2. Tag payload: a versioned scene state, compressed
Value = `"CP{version}:" + base64(Brotli(MessagePack(TagState)))`. The protocol version (currently 2) is in the prefix, so a tag from another version is recognised without decoding its body:
```
TagState {
  Version        protocol version (mismatch = "needs the same CoPose version")
  Self           ActorKey of the tag owner
  Partner        ActorKey? the owner has chosen (null = not pairing)
  Ready, Resolved[]                presence (replaces the Presence message)
  Clock          highest clock among the owner's authored bones
  Actors[]  { Key, Names string[], Values float[10*n], Clocks uint[n] }
}
```
*Refined during implementation:* a tag holds **only the bones its owner currently authors**, meaning the bones whose newest version was written by the owner. The author of every entry is therefore `Self`, with no per-bone author field.
- The partner's edits are already in the partner's own tag, so the two tags together hold the whole scene.
- Each tag is still a complete statement of its owner's edits, so loss tolerance is kept.
- This roughly halves the tag: typically one character's seeded pose plus whatever was edited on the other.

`Clock` is the owner's highest *authored* clock, not the Lamport counter, so merging the partner's edits never changes my tag and doesn't trigger a republish. Actors are only included while paired.

Budget: at most **16 KB** encoded; a 300-bone character pose encodes well under it (TagCodec tests). If the size test (task 1.2) finds a lower limit, positions stay `float32` and rotations are quantised to 3×`int16` (smallest-three), with the budget set from the measurement. A tag over budget is still published, and the debug panel reports it.
*Alternative:* deltas instead of state. Rejected: a tag only shows its latest value, so a skipped update would lose edits, whereas full state tolerates loss.

### D3. Convergence: per-bone last-writer-wins registers with Lamport clocks
Each (actor, bone) holds a value and a version `(clock, authorKey)`, ordered by clock, then author key ordinal. A local edit sets `clock = localClock + 1` and becomes that bone's version. Merging a partner tag takes each bone whose version is newer, then advances `localClock` past every received clock. Merging is commutative, associative and idempotent, so loss, repetition and reordering of tags don't matter, and both sides converge.
An ongoing local drag wins naturally: the next sample after a merge creates a new version above everything seen. This replaces v0.1.0's host sequence plus pending marks.

### D4. Publishing: changed-only, throttled to 500 ms
The tag is rewritten when the encoded state changes, at most every **500 ms**. That's longer than SimpleHeels' 250 ms restarting timer, so each publish actually goes out even during a continuous drag, giving partners stepped updates about twice a second instead of nothing until the drag stops. No rewrite happens when nothing changed. The interval is a constant, to tune after the task 1.3 throttling measurement.

### D5. Pairing state machine
```
            choose X              X chooses me
  Idle ---------------> Waiting(X) ------------+
   ^  \                                        v
   |   +--- X chooses me ---> Incoming(X) --> Paired(X)
   |                             accept         |
   +---- stop / X's tag gone >10 s / X names someone else ----+
```
My tag's `Partner` field is my choice. The session is `Paired` only when X's tag names me and mine names X. A 10 s grace window covers brief tag gaps (zoning, redraws). Leaving `Paired` clears the scene state.

### D6. Initial seeding and Push pose
On entering `Paired`, and on each transition to ready, the client stamps every bone of **its own** character that has no version yet with a local edit. That brings the partner's view of me up to date without overwriting edits already made in the session. "Push pose" stamps all bones of the chosen actor with fresh versions. Ktisis pose-file snapshots (`SavePose`/`LoadPoseExtended`) are no longer used.

### D7. Code structure
- `CoPose.Protocol`: `TagState` plus `TagCodec` (prefix, base64, Brotli, MessagePack, size check). Frames, invite and the frame codec are removed.
- `CoPose.Core`:
  - `SceneState`: registers and merge.
  - `TagSync`: tick loop that samples, records edits, applies merged values with in-flight exclusion and re-read commit (as before), and publishes on a throttle.
  - `Pairing`: the D5 state machine.
  - All three sit behind `ITagChannel` (publish my tag; events for partner tags) and the existing reader, writer, registry and environment interfaces, so they're tested with fakes.
  - `Net/*` and `SceneSync`'s host-ordering logic are removed.
- Plugin: `SimpleHeelsIpc`, a `HeelsTagChannel : ITagChannel` (object-0 publish, `TagChanged` plus scan), a prerequisites check using `IDalamudPluginInterface.InstalledPlugins`, and a redesigned window. `CoPose/Net/*` and `Mono.Nat` are removed.

## Risks / Trade-offs

- [Player Sync's server rejects or truncates large heels payloads] → The size test runs first (task 1.2). Quantisation (D2) and including only versioned bones keep the tag small.
- [The server throttles or penalises frequent addon updates] → A 500 ms minimum interval, changed-only publishing, and a throttling test (task 1.3). The interval can be raised without changing the design.
- [Load on a community-run server] → The same channel LivePose uses. Traffic only happens during an active session, and only while posing changes.
- [Lightless differs from Player Sync] → Warn rather than block when only Lightless is present. Test with it when possible.
- [Everyone paired with me and nearby receives my tag] → It holds only pose data and my chosen partner's name. Pose data is only included while I'm choosing someone.
- [SimpleHeels changes its tag IPC] → Check API major version 2 at load; the window explains when it's incompatible.
- [Updates feel laggy compared with v0.1.0] → An accepted trade-off for zero setup, stated in the spec and the window.

## Migration Plan

Release as 0.2.0. Both players must update, because v0.1.0 invites and hosts no longer work and v0.1.0 ignores tags. Remove any playit.gg or port-forward set up for CoPose. Rollback: reinstall 0.1.0 from its GitHub release.

## Open Questions

- The exact Dalamud `InternalName`s of Player Sync and Lightless for the prerequisites check (read them from the manifests during task 5.3). This doesn't affect the design.
