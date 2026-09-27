## 1. Relay Worker (`CoPose.Relay/`)

- [x] 1.1 Scaffold `CoPose.Relay` (package.json, tsconfig, eslint, vitest with `@cloudflare/vitest-pool-workers`, `wrangler.toml` per D8 with `local`/`production` envs, a `POSE_ROOM` Durable Object binding and the `new_sqlite_classes` migration). Verify that `npm ci`, `npm run lint`, `npm run typecheck` and an empty `npm test` all pass.
- [x] 1.2 Implement the Worker entry: `GET /` health, and `GET /v1/room/<roomId>?p=<participantId>` validation and upgrade routed to `PoseRoom` (D1). Verify with vitest: health response, bad room id rejected without upgrade, bad participant id rejected, valid request upgraded.
- [x] 1.3 Implement `PoseRoom` (D1/D2): hibernating sockets tagged by participant, max two participants, replace-on-reconnect, forwarding without echo, FULL storage and replay on connect, PEER_JOINED/PEER_LEFT, size and rate limits via socket attachments, and a 10-minute empty-room cleanup alarm. Verify with vitest for every `pose-relay` scenario: two join, third refused, reconnect replaces, forward and no echo, partner absent, catch-up replay, only latest kept, abandoned room purged, partner leaves, oversized, flooding.
- [x] 1.4 Add the relay job to `.github/workflows/pr-build.yml` (Node setup, `npm ci`, lint, typecheck, test in `CoPose.Relay`) (D9). Verify that the workflow file is valid and the same commands pass locally.

## 2. Protocol

- [x] 2.1 Tag protocol v3 (`CP3:`): remove `Actors` from `TagState` and add `Nonce` (16 bytes). Add `SceneMessage` (`StateMessage`, `StopMessage`), a scene codec (MessagePack + Brotli, binary, decompression limit), and the frame kinds (D2). Verify with round-trip and malformed-input tests, a size test (a 300-bone FULL encodes under 32 KB), and a v2 tag reported as a version mismatch.
- [x] 2.2 Add `RoomId`/`ParticipantId` derivation (D3). Verify with tests: symmetric for both participants, different for different pairs or nonces, and the correct length and hex format.

## 3. Core

- [x] 3.1 Add `ISceneChannel`, `SceneEvent`, and an in-memory fake relay implementing the D2 semantics (store FULL, forward, presence, replay) for tests. Verify that it compiles, with a unit test of the fake's replay and presence behaviour.
- [x] 3.2 Implement `RelayChannel` over `ClientWebSocket` with a read loop, send queue, and exponential-backoff reconnect up to 30 s (D4). Verify with a test against a local `HttpListener`-based WebSocket echo/relay stub that sends and receives frames, reconnects after the server drops, and reports status and last error.
- [x] 3.3 Rework `CoPoseClient` and `TagSync`: tags drive pairing only; on pairing derive the room and connect; merge `StateMessage`s from the scene channel; LIVE deltas at most every 100 ms and FULL every 3 s, on connect, on readiness change and after Push/Reset (D5); ready and presence from state messages. Verify with fake-relay tests: a drag reaches the partner within one send interval, idle sends only FULL every 3 s, a continuous drag stays at or below 10 LIVE/s, late readiness catches up from replay without Push, a lost LIVE is healed by the next FULL, concurrent edits converge, and there's no echo.
- [x] 3.4 Session lifecycle over the relay (D6): Stop over the relay ends both sides even with stale tags; PEER_LEFT plus 30 s ends the session; a reconnect within 30 s keeps it. Verify with fake-relay tests for stop from either side, partner gone, and brief disconnect.
- [x] 3.5 Reset (D7): capture start poses on ready; `Reset(actor)` and `ResetBoth()` record them as new edits and send a FULL. Verify with tests: after both players pose, Reset of one character restores its start pose on both clients, and Reset both restores both.
- [x] 3.6 Diagnostics (relay state, sent/received counts, last message size, partner message age, last error). Verify with a fake-clock test.

## 4. Plugin

- [x] 4.1 Wire `RelayChannel` into `SessionManager`/`Plugin` with a configurable `RelayUrl` (default set in task 5.2), sending Stop and closing on dispose. Remove bone data from `HeelsTagChannel` publishing. Verify that the solution builds and all tests pass.
- [ ] 4.2 Window: relay status line; "Reset" per character and "Reset both"; Push pose shown as optional; "pair before entering GPose" hint; debug fields from 3.6 plus a relay URL override. Verify in game against `wrangler dev`: both show relay connected, a drag mirrors live in GPose, and Reset works.

## 5. Deploy and release

- [x] 5.1 Run the Worker locally (`npm run dev` in `CoPose.Relay`) and smoke-test two plugin instances or a script client against it. Verify that forwarding, replay and presence work end to end.
- [x] 5.2 **With the owner's explicit approval at this step**, deploy `copose-relay` to the logged-in Cloudflare account (`npx wrangler deploy --env production`), confirm `GET /` on the workers.dev URL, and set it as the plugin's default `RelayUrl`. Verify that the health check returns `copose-relay` and `oathbound-relay` is unchanged (`wrangler deployments list` for it shows no new deployment).
- [ ] 5.3 Update the README (relay, live posing, Reset, pair before GPose, v0.2 incompatibility; replace the uncommitted Lightless note with the confirmed deferral explanation), bump to 0.3.0, commit, push and tag `v0.3.0`. Verify that the Release workflow succeeds and `repo.json` shows 0.3.0.
- [ ] 5.4 Two-player test in GPose: pair outside GPose, enter GPose, both ready and relay connected; each poses both characters live; the partner re-entering GPose catches up automatically; Reset per character and both; Stop in GPose ends both sessions. Record results in `verification.md`.
