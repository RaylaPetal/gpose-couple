## 1. Shared interval helper

- [x] 1.1 Add `IntervalGate` to `CoPose.Core` (D1: nullable last time, `TryPass(now)`, `Reset()`). Verify with unit tests: first call passes, calls within the interval are blocked, a call exactly at the interval passes, a call after `Reset()` passes, and it behaves with timestamps near `long.MaxValue` and at 0.
- [x] 1.2 Use `IntervalGate` in `CoPoseClient` (publish throttle), `HeelsTagChannel` (scan), `SessionManager` (status refresh) and `GposeActorRegistry` (rescan), removing every `long.MinValue`-based timestamp. Verify that `grep long.MinValue` finds nothing in `CoPose*/` and the existing tests still pass.

## 2. Detection

- [x] 2.1 Add `ActorIndex.Build` to Core (D2): from (index, name, world) entries, produce the name+world map and the name-only map, the lowest index winning in both. Verify with unit tests: exact key match, a different world falls back to the name, a duplicate name (clone) picks the lowest index, and empty names are ignored.
- [ ] 2.2 Rework `GposeActorRegistry` to use `IntervalGate` and `ActorIndex`, and expose the last scan's actors and `Explain(ActorKey)` (D5). Verify that the plugin builds and, in game, that both characters resolve in GPose (the debug panel lists both, with index and world). _(Code complete and builds; awaiting the in-game check.)_

## 3. Posing

- [ ] 3.1 In `HavokPoseReader`, skip bones with a negative parent index in partial skeletons after the first (D3). Verify that the plugin builds and, in game, that "Log bones" still shows the body and face bones, and that moving only the head/neck of one character produces no edits on the other character's face or hair beyond what was moved (the debug publish count doesn't climb while idle after the move). _(Code complete and builds; awaiting the in-game check.)_
- [x] 3.2 Count partner tags from the moment the partner is chosen, and keep the last-received time across session start (D4). Verify with a unit test: a tag from the chosen partner before the session starts increments `Receives` and sets `LastPartnerTagAtMs`, and pairing doesn't clear it.

## 4. Diagnostics

- [ ] 4.1 In the debug panel, list the GPose actors CoPose sees (index, name, world), and for each session character show why it's absent (not in GPose / no GPose actor with that name / no skeleton). Verify in game with and without GPose. _(Code complete and builds; awaiting the in-game check.)_

## 5. Release

- [ ] 5.1 Bump to 0.2.2, run the full build and tests, then commit, push and tag `v0.2.2`. Verify that the Release workflow succeeds and `repo.json` shows 0.2.2.
- [ ] 5.2 Two-player check in GPose: both characters found, both players ready, and a bone moved on either character appears on the other client. Then continue with the in-game tasks in `sync-via-heels-tags` (5.x, 6.2).
