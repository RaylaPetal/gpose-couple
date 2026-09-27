# CoPose

Pose together in GPose. Pair up with someone nearby, and either of you can pose **either** character with Ktisis.
Edits show up on your partner's screen live, while you're both in GPose. No codes, ports or VPNs: you pair through
the Player Sync or Lightless connection you already have, and pose data goes through a small CoPose relay.

> Syncs bone posing only. Moving characters' root positions, soft locks and per-partner permissions are not in this
> version.

## Requirements (both players)

- [Ktisis](https://github.com/ktisis-tools/Ktisis), with posing mode on while you pose.
- [SimpleHeels](https://github.com/Caraxi/SimpleHeels): used to find each other and pair.
- **Player Sync or Lightless**, with the two of you **paired with each other** there.
- The same CoPose version (0.3 doesn't work with 0.2), and being near each other (same instance/area).

## Install

1. In game, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Add `https://raw.githubusercontent.com/RaylaPetal/gpose-couple/master/repo.json`, tick **Enabled**, and save.
3. Open `/xlplugins`, search for **CoPose**, and install it.

## Use

1. **Pair before entering GPose.** Both players open `/copose`, and after a few seconds each of you appears in the
   other's **Nearby CoPose players** list. One clicks **Pose with <name>**, the other clicks **Accept**.
2. Both enter GPose and turn on Ktisis posing. The window shows **Live sync connected** and both players **ready**.
3. Pose either character. Your partner sees it within a fraction of a second.

- Sync is automatic, including catching up when one of you enters GPose later. **Push pose** is only an override.
- **Reset** returns a character to the pose it had when you became ready (on both screens). **Reset both** does
  both characters.
- **Stop posing together** (or `/copose stop`) ends the session for both of you, even inside GPose.

### Why pair outside GPose?

Player Sync and Lightless hold back updates from paired players while you're in GPose, and pairing travels that way.
Once you're paired, CoPose talks to your partner over its relay, which works in GPose. If one of you reloads the
plugin mid-session, pair again outside GPose.

### If something doesn't work

- The top of the CoPose window should show Ktisis, SimpleHeels and Player Sync/Lightless in green.
- You must be paired with each other in Player Sync/Lightless and near each other to see each other in the list.
- **Relay unreachable**: check your internet connection. CoPose keeps retrying on its own.
- The **Debug** section shows the relay state, messages sent and received, and character detection.

## Relay

`CoPose.Relay/` is a Cloudflare Worker with one Durable Object per two-player room. It forwards messages between the
two players and keeps each one's latest state so the other can catch up. It knows nothing about poses and deletes
a room 10 minutes after both players leave. The room id is derived from secrets only the two paired players know.

```
cd CoPose.Relay
npm ci
npm test                      # vitest in the Workers runtime
npm run dev                   # local relay on http://127.0.0.1:8787
npx wrangler deploy --env production
```

To test against a local relay, set **Debug → Relay URL override** in the plugin. The end-to-end test runs with
`COPOSE_RELAY_URL=http://127.0.0.1:8787 dotnet test --project CoPose.Tests/CoPose.Tests.csproj`.

## Build from source

Requires the .NET 10 SDK and a Dalamud dev install (XIVLauncher with Dalamud run at least once).

```
dotnet build CoPose.slnx
dotnet test --project CoPose.Tests/CoPose.Tests.csproj
```

Load `CoPose/bin/x64/Debug/CoPose.dll` as a dev plugin (`/xlsettings` → Experimental → Dev Plugin Locations).

## Releasing

Push a version tag (`git tag v0.3.0 && git push origin v0.3.0`). The Release workflow builds, tests, publishes
`latest.zip` as a GitHub release, and updates `repo.json` on `master`. Relay deploys are separate and manual.

## License

AGPL-3.0. See [LICENSE.md](LICENSE.md).
