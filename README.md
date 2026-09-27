# CoPose

Pose together in GPose. Two players pair up with an invite code, and either of them can pose **either** character
with Ktisis. Bone edits are mirrored live on both clients.

> Early release (MVP). Syncs bone posing only: moving characters' root positions, soft locks, per-partner
> permissions and auto-reconnect are not in this version.

## Requirements

- [Ktisis](https://github.com/ktisis-tools/Ktisis) with posing mode on, for both players.
- Both players in the same instance/area, both in GPose.
- Both players on the same CoPose version.

## Install

1. In game, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Add `https://raw.githubusercontent.com/RaylaPetal/gpose-couple/master/repo.json`, tick **Enabled**, and save.
3. Open `/xlplugins`, search for **CoPose**, and install it.

## Use

1. **Host:** open `/copose`, click **Host**, and send your partner the invite code (`CP2-…`).
2. **Partner:** paste the code in **Join a session** and click **Join** (or `/copose join <code>`).
3. Both of you enter GPose and turn on Ktisis posing. When both show **Ready**, drag bones on either character.

Commands: `/copose` (window), `/copose host`, `/copose join <invite>`, `/copose leave`.

### If your partner can't connect

The host's PC has to be reachable. The host window shows what worked:

- **Reachable from internet (UPnP):** nothing else to do.
- **LAN/VPN only:** your router or ISP blocks incoming connections. Pick one:
  - **Tunnel (only the host installs anything):** run a TCP tunnel such as [playit.gg](https://playit.gg) to local
    port 47715, and paste its address (e.g. `name.gl.at.ply.gg:34567`) into **Public address** before clicking **Host**.
  - **VPN:** both players install [Tailscale](https://tailscale.com), and the host puts their `100.x.y.z` address
    in **Public address**.
  - **Port-forward:** forward TCP 47715 on your router, and put your public IP in **Public address**.

If Windows Firewall asks about FINAL FANTASY XIV when you host, allow it.

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
