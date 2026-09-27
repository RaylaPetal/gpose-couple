## Context

See proposal.md. Everything below the `IActorRegistry`/`IPoseReader`/`IPoseWriter` interfaces is plugin code that the unit tests replace with fakes, so it first ran for real in the v0.2.1 test. What that test showed: pairing worked, the debug panel said "Rayla Velvet@55: not found in GPose" for both characters while Ktisis listed them, the tag stayed at 0.1 KB (announcement only), and "Partner tags received: 0". World 55 is Lamia's row id, so the keys were correct.

What review of the source found:
- **Actor lookup** (`GposeActorRegistry`): `lastScanMs = long.MinValue`, and `Invalidate()` resets to the same value. `now - long.MinValue` overflows to a negative number, which is always below the 1000 ms throttle, so `Rescan` is unreachable. `v0.2.0`'s `CoPoseClient` had the same bug in its publish throttle, fixed then by making the timestamp nullable. `HeelsTagChannel` and `SessionManager` use `long.MinValue / 2`, which is safe but fragile.
- **Name fallback**: `byName[name] = byName.ContainsKey(name) ? null : i` marks duplicate names as ambiguous.
- **Partial-skeleton roots** (`HavokPoseReader`): bones with `parent < 0` keep their model-space transform as "local". In partials after the body, those bones are placed by Ktisis's `HavokPosing.ParentSkeleton`, which re-attaches the connection bone to the body bone. `ApplyAbsolutePoses` → `SyncModelSpace` starts at bone 1 and never uses their `LocalPose`. So they carry no posing information, yet their model transform moves whenever the head or spine moves. Connection bones that repeat a body bone's name (for example `j_kao`) are already dropped as duplicates. Uniquely named roots and extra multi-root roots are not.
- **Receive stats** (`CoPoseClient.Receive`): `Stats.Received` only runs when `Session != null && tag.Owner == Session.Partner`, and `Stats.Reset()` clears the timestamp when the session starts. The partner's tags before or at pairing are never counted.
- **Player Sync sending side** (checked, fine): heels changes are only suppressed while zoning, or while Player Sync applies character data *outside* GPose (`HaltCharaDataCreation`). Tags are sent from inside GPose.

## Goals / Non-Goals

**Goals:**
- Characters resolve in GPose, including when a clone of either character exists.
- No spurious edits from attachment-driven bones.
- Diagnostics that make "why is my character absent?" answerable from the debug panel.
- Make overflow-prone "time since last X" code impossible to reintroduce unnoticed.

**Non-Goals:**
- Protocol, tag format or pairing changes.
- Unit-testing Dalamud-bound code. The registry and reader stay plugin-side; their pure logic moves to Core where that's cheap.

## Decisions

### D1. One overflow-safe interval helper in Core
`IntervalGate` (Core, Dalamud-free) holds `long? last`. `TryPass(now)` returns true (and records `now`) when there's no last time or `now - last >= interval`. `Reset()` clears `last`, so the next call passes. Every throttle uses it: the registry rescan, the tag channel scan, the session status refresh, and the `CoPoseClient` publish throttle. Unit tests cover first call, within interval, at interval, after reset, and very large timestamps.
*Alternative:* patch the two `long.MinValue` literals. That was rejected because the same bug has now shipped twice.

### D2. Name fallback picks the lowest index
Rescan records `byName[name]` only the first time a name is seen (`TryAdd`), so the lowest object index wins, matching the existing `byKey` rule. GPose creates the originals before any spawned clones.
To keep the rule testable, the choice goes into a tiny pure function in Core, `ActorIndex.Build(IEnumerable<(uint Index, string Name, ushort World)>)`, which returns both maps with tests. The plugin feeds it from the object table.

### D3. Skip parentless bones in partial skeletons after the first
In `HavokPoseReader`, for partial index `p > 0`, bones whose parent index is negative are not returned. Partial 0 (the body) is unchanged; its root is `n_root`. On the writer side, those bones are no longer in the read either, so the full-pose write leaves Ktisis's re-attachment alone.
*Alternative:* filter by name against a known list. Rejected, because the list would be brittle across races and modded skeletons.

### D4. Count partner tags from the moment they're chosen
`CoPoseClient.Receive` calls `Stats.Received(now)` for any decoded tag from `Pairing.Chosen`, not only once a session exists. `Stats.Reset()` no longer clears the last-received time on session start.

### D5. Detection diagnostics
`GposeActorRegistry` exposes the last scan's actors (index, name, home world) and an `Explain(ActorKey)` result: `NotInGpose`, `NoActorNamed`, `Found(index)`. The debug panel shows the list and, for each session character, the reason, plus "no skeleton" when the reader fails on a found actor. It's read-only UI over data the registry already has.

## Risks / Trade-offs

- [Skipping partial roots drops a bone someone intentionally posed] → They can't be posed through `ApplyAbsolutePoses` anyway (`SyncModelSpace` ignores bone 0, and Ktisis re-parents the partial), so nothing that worked is lost.
- [The lowest index isn't the original on some setups] → Name plus world is still tried first. The fallback only applies when worlds differ, and the debug list now shows which index was chosen.
- [The detection fix exposes the next unknown: whether full-pose writes behave in game] → The in-game tasks in `sync-via-heels-tags` (5.x, 6.2) follow this release.

## Migration Plan

Release 0.2.2 through the tag workflow. There's no data or format migration, and it's compatible with 0.2.0/0.2.1 tags.
