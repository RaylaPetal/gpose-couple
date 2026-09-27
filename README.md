# CoPose

Pose together in GPose. Pair up with someone nearby, and either of you can pose **either** character with Ktisis.
Edits reach your partner about a second after you make them. No servers, codes, ports or VPNs: CoPose rides on
the Player Sync or Lightless connection you already have.

> Early release. Syncs bone posing only. Moving characters' root positions, soft locks and per-partner permissions
> are not in this version, and dragging shows up on your partner's screen in steps rather than smoothly.

## Requirements (both players)

- [Ktisis](https://github.com/ktisis-tools/Ktisis), with posing mode on while you pose.
- [SimpleHeels](https://github.com/Caraxi/SimpleHeels): CoPose data travels in a SimpleHeels tag.
- **Player Sync or Lightless**, with the two of you **paired with each other** there.
- The same CoPose version, and being near each other (same instance/area).

**Lightless 3.3.0.0 compatibility:** its stock client defers SimpleHeels updates while in GPose.
That prevents live CoPose updates even when pairing works, and can make the last pose appear after leaving
and re-entering GPose. CoPose 0.2.3 fixes lost apply retries but does not remove this transport limitation.
It does not modify or replace Lightless. Raising the tag budget does not fix the transport limitation.

## Install

1. In game, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Add `https://raw.githubusercontent.com/RaylaPetal/gpose-couple/master/repo.json`, tick **Enabled**, and save.
3. Open `/xlplugins`, search for **CoPose**, and install it.

## Use

1. Both players open `/copose`. After a few seconds each of you appears in the other's **Nearby CoPose players** list.
2. One of you clicks **Pose with <name>**. The other sees the request and clicks **Accept**.
3. Both enter GPose and turn on Ktisis posing. When both show **ready**, pose either character.

- **Push pose** re-sends a character's whole current pose, for example after loading a `.pose` file onto it.
- **Stop posing together** (or `/copose stop`) ends the session for both of you.

### If your partner doesn't show up

- Check the top of the CoPose window: Ktisis, SimpleHeels and Player Sync/Lightless should all show green.
- You must be paired with each other in Player Sync/Lightless and near each other. If your partner's character
  looks right (their mods and glamour sync), the connection is working.
- The **Debug** section has a **Channel test**: publish a test tag and check that it arrives on your partner's side.

## Build from source

Requires the .NET 10 SDK and a Dalamud dev install (XIVLauncher with Dalamud run at least once).

```
dotnet build CoPose.slnx
dotnet test --project CoPose.Tests/CoPose.Tests.csproj
```

Load `CoPose/bin/x64/Debug/CoPose.dll` as a dev plugin (`/xlsettings` → Experimental → Dev Plugin Locations).

## Releasing

Push a version tag (`git tag v0.2.0 && git push origin v0.2.0`). The Release workflow builds, tests, publishes
`latest.zip` as a GitHub release, and updates `repo.json` on `master`.

## License

AGPL-3.0. See [LICENSE.md](LICENSE.md).
