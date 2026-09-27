# pairing Specification

## Purpose
Client-side pairing for the CoPose plugin. One player hosts a session from inside the plugin and shares an invite code, and the other joins with it. The plugin tracks the partner and their readiness, and resolves every participant's character to a local GPose actor, identifying each by name and home world rather than a client-specific index.

## Requirements

### Requirement: Host port configuration
The plugin SHALL let the user set the TCP port used for hosting and SHALL persist it in the plugin configuration. The default SHALL be 47715.

#### Scenario: Port persisted
- **WHEN** the user changes the host port and reloads the plugin
- **THEN** the changed port is still set

### Requirement: Host, join and leave
The plugin SHALL let the user host a session, join a session by pasting an invite code, and leave the current session, through the main window and through the `/copose host`, `/copose join <invite>` and `/copose leave` commands. `/copose` with no arguments SHALL toggle the main window. Only one session at a time SHALL be allowed, whether hosting or joined.

#### Scenario: Host
- **WHEN** the user clicks "Host"
- **THEN** the window shows the invite code with a copy button and lists the user as the only participant

#### Scenario: Join by command
- **WHEN** the user types `/copose join CP1-…` with a valid invite for a reachable host
- **THEN** the plugin connects and the window lists both participants

#### Scenario: Try each address
- **WHEN** an invite contains several endpoints
- **THEN** the plugin tries them in the invite's order (LAN first), each with a 3-second timeout, and uses the first that succeeds

#### Scenario: Host unreachable
- **WHEN** none of the invite's addresses accept a connection
- **THEN** the window shows "could not reach host", with a hint that the host may need UPnP, a port-forward, or a shared LAN/VPN

#### Scenario: Join rejected
- **WHEN** the host rejects the join (invalid invite, session full, or version mismatch)
- **THEN** the window shows the rejection reason

#### Scenario: Leave
- **WHEN** the user clicks "Leave" or types `/copose leave`
- **THEN** the plugin leaves the session (or stops hosting), stops all sending and applying of pose data, and clears all session state

### Requirement: Router reachability for the host
When hosting starts, the plugin SHALL try to map the host port on the local router with UPnP or NAT-PMP, and SHALL learn the public IPv4 address from the router. The window SHALL show the result as one of: "reachable from internet (UPnP)", "LAN/VPN only" (mapping failed, or the router reports a private or carrier-grade-NAT address), or "mapping in progress". When the mapping fails, the window SHALL tell the user to forward the port manually or use a shared LAN/VPN. It SHALL let the user enter a public address as `address` or `address:port`, where the address is an IPv4 address or a host name (for example a Tailscale IP, a manual port-forward, or a tunnel such as playit.gg or bore). The port defaults to the host port. The plugin SHALL resolve a host name to IPv4 when hosting starts, and SHALL always include the resulting endpoint in the invite, whatever the UPnP result. If it can't be parsed or resolved, the window SHALL show why. The mapping SHALL be removed when hosting stops or the plugin unloads.

#### Scenario: UPnP success
- **WHEN** the router supports UPnP and the mapping succeeds
- **THEN** the status shows "reachable from internet (UPnP)" and the invite includes the router's public address

#### Scenario: UPnP unavailable
- **WHEN** no UPnP/NAT-PMP router responds within 5 seconds
- **THEN** the status shows "LAN/VPN only" with port-forward guidance, and the invite contains only the LAN endpoint (plus a user-entered public address, if set)

#### Scenario: Carrier-grade NAT detected
- **WHEN** the router reports a public address in a private or `100.64.0.0/10` range
- **THEN** the status shows "LAN/VPN only", explaining that the ISP's NAT blocks direct connections, and that address is not put in the invite

#### Scenario: Tunnel address
- **WHEN** the host enters `name.gl.at.ply.gg:34567` as the public address and starts hosting
- **THEN** the name is resolved to an IPv4 address, and the invite contains that address with port 34567 alongside the LAN endpoint on the host port

#### Scenario: Unresolvable public address
- **WHEN** the entered public address is malformed or its host name doesn't resolve
- **THEN** hosting still starts, the window shows the error, and the invite contains only the other endpoints

#### Scenario: Mapping removed
- **WHEN** the host stops hosting
- **THEN** the UPnP port mapping the plugin created is deleted

### Requirement: Participant identity
Each participant SHALL be identified on the wire by an actor key made of their character name and home world id. GPose object indices SHALL NOT be sent over the network. The client id SHALL be a random identifier generated each time the plugin loads.

#### Scenario: Hello contents
- **WHEN** the plugin joins a session
- **THEN** its hello carries the local player's name and home world id, a client id, a display name and the session secret, and no object index

### Requirement: Local actor resolution
The plugin SHALL resolve each participant's actor key to a local GPose actor by matching name and home world among the GPose actors on this client. It SHALL re-resolve when entering GPose, when the participant list changes, and when a lookup fails. An actor that can't be resolved SHALL be shown as "absent", and pose data for it SHALL be dropped.

#### Scenario: Both actors present
- **WHEN** both players are in the same area and in GPose
- **THEN** the window shows both actor keys as resolved on each client

#### Scenario: Partner not in range
- **WHEN** the partner's character is not among the local GPose actors
- **THEN** the partner's actor is shown as absent, and incoming pose data for it is discarded without error

### Requirement: Readiness and presence
A participant SHALL be ready when they're in GPose, Ktisis reports posing is on, and every participant's actor key resolves locally. The plugin SHALL broadcast its presence (ready flag and resolved actor keys) when it joins, when a participant joins, and whenever its readiness changes. The window SHALL show each participant's ready state.

#### Scenario: Becoming ready
- **WHEN** a user in a session enters GPose and turns Ktisis posing on, and both actors resolve
- **THEN** the partner's window shows that user as ready

#### Scenario: Posing turned off
- **WHEN** a ready user turns Ktisis posing off
- **THEN** the partner's window shows that user as not ready

### Requirement: Ktisis availability check
On load and whenever it tries to become ready, the plugin SHALL check that Ktisis is installed and exposes IPC API major version 1. If Ktisis is missing or incompatible, the plugin SHALL show that in the window and SHALL never report ready.

#### Scenario: Ktisis missing
- **WHEN** Ktisis is not installed or not loaded
- **THEN** the window shows "Ktisis not available" and the user is never marked ready

### Requirement: Clean shutdown
When the plugin is unloaded or disposed, it SHALL leave or stop any session, close all sockets, remove any UPnP mapping it created, and unregister all commands, UI and framework handlers.

#### Scenario: Unload while hosting
- **WHEN** the host disables the plugin while a guest is connected
- **THEN** the guest sees the session ended, the port mapping is removed, and no background work keeps running
