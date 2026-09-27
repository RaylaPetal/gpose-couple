## Why

In the first real two-player test of v0.2.1, pairing worked, but in GPose with Ktisis posing on, CoPose showed both characters as "character absent" and neither player became ready, so nothing synced. The cause is a bug in the GPose actor lookup: it never scans the object table. A review of the rest of the detection and posing path found more defects that the fake-based unit tests can't catch, plus diagnostics that made the failure hard to read. This change fixes them so the behaviour already specified in `sync-via-heels-tags` (actor resolution, readiness, applying edits, diagnostics) actually works in game.

## What Changes

- **Fix: characters are never found in GPose.** `GposeActorRegistry` starts its "last scan" time at `long.MinValue`, so `now - lastScan` overflows to a negative number and the one-second rescan throttle never lets a scan run, both at start and after every `Invalidate()`. All lookups fail, so both actors are "absent", nobody is ready, and no pose data is recorded, seeded or applied.
- **Fix: a clone of your character hides the original.** When two GPose actors share a name (a Brio/Ktisis clone), the name-only fallback treats the name as ambiguous and gives up. If the GPose copy's home world differs from the overworld one, which is why the fallback exists, lookup then fails. Resolve to the lowest object index instead: the original GPose copy comes before spawned clones.
- **Fix: attachment roots of partial skeletons are read as edits.** For partial skeletons after the body (face, hair, tail and so on), bones with no parent in their partial are positioned by Ktisis, which re-attaches them to the body. The reader reports their model-space transform as a "local" value, so any movement of the parent body bone looks like an edit of those bones: spurious edits, publishes and applies. Skip parentless bones in partials after the first.
- **Fix: misleading "Partner tags received: 0 / never".** Received-tag stats only count updates that arrive after the session object exists, and reset when it's created. Count every tag update from the chosen partner, from the moment they're chosen.
- **Diagnostics for detection:** the debug panel lists the GPose actors CoPose sees (index, name, home world), and for each session character says *why* it's absent: not in GPose, no GPose actor with that name, or no skeleton yet.
- **Prevent this class of bug:** replace the hand-rolled "last time" throttles (registry, tag channel, session status refresh, tag publisher) with one tested, overflow-safe interval helper in `CoPose.Core`.
- Release as **0.2.2**.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
<!-- none: these are defects against requirements already specified in `sync-via-heels-tags` (Local actor resolution, Readiness and presence, Apply remote bone edits, Diagnostics). The extra debug detail is diagnostics tooling, not a new requirement. -->

## Impact

- **Code:** `CoPose/Interop/GposeActorRegistry.cs`, `CoPose/Interop/HavokPoseReader.cs`, `CoPose/Interop/HeelsTagChannel.cs`, `CoPose/Session/SessionManager.cs`, `CoPose/Windows/MainWindow.cs`, `CoPose.Core/CoPoseClient.cs`, `CoPose.Core/Sync/SyncStats.cs`, plus a new `CoPose.Core` interval helper and its tests.
- **Compatibility:** there are no protocol or tag-format changes; 0.2.2 pairs with 0.2.0 and 0.2.1. Both players should still update, since the detection fix is needed on each side.
- **Relation to `sync-via-heels-tags`:** that change stays open for its in-game verification tasks. This change is a prerequisite for them.
