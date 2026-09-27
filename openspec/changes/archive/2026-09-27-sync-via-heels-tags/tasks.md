## 1. Channel test (do first: sets the tag budget and publish rate)

- [ ] 1.1 Add a minimal `SimpleHeelsIpc` wrapper in the plugin (`ApiVersion`, `SetTag`, `GetTag`, `RemoveTag`, `TagChanged`) and a temporary debug panel: "publish test tag of N KB on object 0", plus a list of received `CoPose-test` tags with size, sender and arrival time. Verify in-game that setting a small test tag on one client appears in the other client's list while both are in GPose. _(Code complete and builds: Debug > Channel test. Awaiting the in-game check.)_
- [ ] 1.2 With a partner via Player Sync, publish test tags of 4, 8, 16, 32 and 64 KB and record which arrive intact, and the end-to-end latency, in `design.md` (Context). Set the D2 budget from the result. Verify that the recorded numbers are in the design.
- [ ] 1.3 Publish a changing 16 KB (or budget-sized) tag every 500 ms for 60 s and record whether all, some or none arrive, and any Player Sync warnings. Confirm or adjust the D4 interval in the design. Verify that the result is recorded.

## 2. Remove hosting

- [x] 2.1 Delete `CoPose.Core/Net/*`, `FrameCodec`/`Frames`/`Invite`/`Wire` frame parts in `CoPose.Protocol`, `CoPose/Net/*`, the `Mono.Nat` package, hosting UI, the host-port and public-address config, `/copose host|join|leave`, and their tests. Verify that `dotnet build CoPose.slnx` succeeds and the remaining tests pass.

## 3. Protocol

- [x] 3.1 Add `TagState`/`TagActor` (D2) with MessagePack keys and `TagCodec` (`CP1:` prefix, base64, Brotli, size check against the budget, and rejection of oversized or malformed input, including decompression limits). Verify with round-trip tests, a malformed-input test, and a test that a 300-bone character pose (what one tag carries once tags hold only their owner's edits, see D2) encodes under the budget.

## 4. Sync core

- [x] 4.1 Implement `SceneState` registers and the merge rule (D3) with a Lamport clock. Verify with tests for newer-wins, author tie-break, idempotence, commutativity, and convergence with reordered, duplicated and dropped tags (a randomized test with two replicas).
- [x] 4.2 Implement `TagSync.Tick` over `ITagChannel` and the reader, writer, registry and environment interfaces: sample at 20 Hz, record local edits, apply merged changes with in-flight exclusion and re-read commit, and publish changed-only at most every 500 ms. Verify with fake tests: idle publishes nothing, a continuous drag publishes at most 2 times per second, a remote apply creates no local edit, not-ready records and applies nothing (and applies once ready), unknown bones are skipped, and an ongoing drag wins.
- [x] 4.3 Implement seeding and Push pose (D6). Verify with tests: pairing mid-scene shows the owner's pose on the partner, seeding doesn't overwrite existing versions, and Push pose wins on both sides.
- [x] 4.4 Implement the `Pairing` state machine (D5) with a 10 s grace window. Verify with tests for request/accept, one-sided waiting, stop from either side, partner tag gone, partner choosing someone else, and version mismatch listing.
- [x] 4.5 Add diagnostics (publishes, receives, last tag size, time since the partner's last tag, last error). Verify with a fake-clock test.

## 5. Plugin

- [ ] 5.1 Implement `HeelsTagChannel : ITagChannel` (publish on object 0, `TagChanged` subscription, 2 s discovery scan of overworld players, removal on dispose). Verify in-game that a partner running the new build appears in the list. _(Code complete and builds; awaiting the in-game check.)_
- [ ] 5.2 Wire `Plugin.cs` and `SessionManager` to `Pairing` + `TagSync`, keeping `HavokPoseReader`, `KtisisIpcPoseWriter` and `GposeActorRegistry`. Commands: `/copose` and `/copose stop`. Verify that the build succeeds and that unloading the plugin removes the tag (the partner's session ends). _(Code complete and builds; awaiting the in-game check.)_
- [ ] 5.3 Add the prerequisites check (SimpleHeels API 2.x; Player Sync or Lightless loaded, using the `InternalName`s read from their manifests). Verify in-game with SimpleHeels disabled that the window explains what's missing. _(Code complete and builds; awaiting the in-game check.)_
- [ ] 5.4 Redesign `MainWindow`:
  - Prerequisites status.
  - Possible partners list with "Pose with".
  - Incoming request with "Accept".
  - Waiting and paired states with "Stop posing together".
  - Ready state for both, and per-actor "Push pose".
  - Debug counters.
  - Text explaining that updates arrive about a second after changes.

  Verify each state in-game. _(Code complete and builds; awaiting the in-game check.)_

## 6. Release

- [ ] 6.1 Update the README (requirements: Player Sync/Lightless + SimpleHeels; how to pair; no networking section) and tag `v0.2.0`. Verify that CI publishes the release and `repo.json` shows 0.2.0.
- [ ] 6.2 Two-player test via Player Sync: discovery, request/accept, pose partner, pose self, concurrent edits converge, pair mid-scene, Push pose, root move not synced, stop, and unload ends the session. Record results in `verification.md` in this change folder.

## 7. Live-apply failure correction

- [x] 7.1 Retain and rate-limit retries of failed or canceled Ktisis applies, serialize full actor writes, and count successful writer acknowledgements. Verify partner-character recovery without another push or GPose restart, local supersession and non-overlapping writes with automated tests.
- [x] 7.2 Investigate the installed transport and document the Lightless 3.3.0.0 GPose limitation. Keep dependency replacement out of the CoPose release, as requested by the user.
