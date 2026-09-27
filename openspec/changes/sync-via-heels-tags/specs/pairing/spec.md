## ADDED Requirements

### Requirement: Sync service prerequisites
The plugin SHALL check that SimpleHeels is loaded and exposes its tag IPC (API major version 2), and that a sync service that carries SimpleHeels data (Player Sync or Lightless) is installed and loaded. The window SHALL show what's missing and explain that both players must be paired with each other in that service. While SimpleHeels is unavailable, the plugin SHALL NOT offer pairing.

#### Scenario: SimpleHeels missing
- **WHEN** SimpleHeels is not installed or not loaded
- **THEN** the window says SimpleHeels is required and no partners are listed

#### Scenario: No sync service
- **WHEN** SimpleHeels is loaded but neither Player Sync nor Lightless is loaded
- **THEN** the window warns that a sync service is required for the partner to receive anything

### Requirement: Partner discovery
The plugin SHALL publish a `CoPose` SimpleHeels tag on the local player's character (object index 0) while the plugin is loaded and SimpleHeels is available, announcing its protocol version even when not paired. It SHALL list nearby player characters whose `CoPose` tag has the same protocol version as possible partners. Players with an incompatible version SHALL be listed as needing an update.

#### Scenario: Partner appears
- **WHEN** a player paired with me in the sync service, near me, and running CoPose is present
- **THEN** that player appears in my window's list of possible partners within a few seconds

#### Scenario: Version mismatch
- **WHEN** a nearby player's `CoPose` tag has a different protocol version
- **THEN** they are listed as "needs the same CoPose version" and can't be chosen

### Requirement: Mutual pairing
The user SHALL be able to choose one listed player as their partner ("Pose with <name>") and to stop ("Stop posing together"). The choice SHALL be published in the tag. A session SHALL exist exactly when both players have chosen each other. When another player has chosen me but I haven't chosen them, the window SHALL show a request I can accept. Only one partner at a time SHALL be allowed. Choosing a player SHALL count as consent for them to pose my character during the session.

#### Scenario: Request and accept
- **WHEN** A clicks "Pose with B" and B then clicks "Accept" on A's request
- **THEN** both windows show that A and B are posing together

#### Scenario: One-sided choice
- **WHEN** A has chosen B but B has not chosen A
- **THEN** A's window shows "Waiting for B", B's window shows A's request, and no pose data is applied on either side

#### Scenario: Stop
- **WHEN** either player clicks "Stop posing together"
- **THEN** both clients leave the session within a few seconds, stop applying each other's pose data, and clear session state

#### Scenario: Partner goes away
- **WHEN** the partner's `CoPose` tag disappears (they unload the plugin, leave the area, or unpair in the sync service)
- **THEN** the session ends on my side and the window says the partner is gone

### Requirement: Participant identity in tags
Each participant SHALL be identified in tag data by an actor key made of their character name and home world id. GPose object indices SHALL NOT be written into tags. Tag data from a player SHALL be attributed to the overworld character SimpleHeels reports it on.

#### Scenario: Tag contents
- **WHEN** the plugin publishes its tag
- **THEN** the tag carries the local player's actor key and, when paired, the partner's actor key, and no object index

### Requirement: Clean shutdown and tag removal
When the plugin is unloaded or disposed, it SHALL remove its `CoPose` tag from the local player's character, and unregister all IPC subscriptions, commands, UI and framework handlers.

#### Scenario: Unload while posing together
- **WHEN** a player disables the plugin during a session
- **THEN** their `CoPose` tag is removed, the partner's session ends, and no background work keeps running

## MODIFIED Requirements

### Requirement: Readiness and presence
A participant SHALL be ready when they're in GPose, Ktisis reports posing is on, and both participants' actor keys resolve locally. The plugin SHALL publish its ready flag and resolved actor keys in its tag whenever they change. The window SHALL show each participant's ready state.

#### Scenario: Becoming ready
- **WHEN** a user in a session enters GPose and turns Ktisis posing on, and both actors resolve
- **THEN** the partner's window shows that user as ready within a few seconds

#### Scenario: Posing turned off
- **WHEN** a ready user turns Ktisis posing off
- **THEN** the partner's window shows that user as not ready within a few seconds

## REMOVED Requirements

### Requirement: Host port configuration
**Reason**: There is no in-plugin host any more; pose data travels in SimpleHeels tags through the players' sync service.
**Migration**: None needed. The setting is ignored.

### Requirement: Host, join and leave
**Reason**: Replaced by partner discovery and mutual pairing, which need no invite codes or connections.
**Migration**: Use "Pose with <name>" in the window. `/copose host` and `/copose join` are removed.

### Requirement: Router reachability for the host
**Reason**: No incoming connections are needed, so UPnP, port-forwarding, VPNs and tunnels are no longer involved.
**Migration**: None needed. Remove any playit.gg/bore tunnel or port-forward set up for CoPose.

### Requirement: Participant identity
**Reason**: Identity now travels in tags rather than a connection hello; replaced by "Participant identity in tags".
**Migration**: None.

### Requirement: Clean shutdown
**Reason**: There are no sockets or port mappings to close; replaced by "Clean shutdown and tag removal".
**Migration**: None.
