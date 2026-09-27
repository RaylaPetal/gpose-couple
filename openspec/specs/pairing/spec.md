# pairing Specification

## Purpose
Client-side pairing for the CoPose plugin. One player hosts a session from inside the plugin and shares an invite code, and the other joins with it. The plugin tracks the partner and their readiness, and resolves every participant's character to a local GPose actor, identifying each by name and home world rather than a client-specific index.

## Requirements

### Requirement: Local actor resolution
The plugin SHALL resolve each participant's actor key to a local GPose actor by matching name and home world among the GPose actors on this client. It SHALL re-resolve when entering GPose, when the participant list changes, and when a lookup fails. An actor that can't be resolved SHALL be shown as "absent", and pose data for it SHALL be dropped.

#### Scenario: Both actors present
- **WHEN** both players are in the same area and in GPose
- **THEN** the window shows both actor keys as resolved on each client

#### Scenario: Partner not in range
- **WHEN** the partner's character is not among the local GPose actors
- **THEN** the partner's actor is shown as absent, and incoming pose data for it is discarded without error

### Requirement: Readiness and presence
A participant SHALL be ready when they're in GPose, Ktisis reports posing is on, and both participants' actor keys resolve locally. The plugin SHALL publish its ready flag and resolved actor keys in its tag whenever they change. The window SHALL show each participant's ready state.

#### Scenario: Becoming ready
- **WHEN** a user in a session enters GPose and turns Ktisis posing on, and both actors resolve
- **THEN** the partner's window shows that user as ready within a few seconds

#### Scenario: Posing turned off
- **WHEN** a ready user turns Ktisis posing off
- **THEN** the partner's window shows that user as not ready within a few seconds

### Requirement: Ktisis availability check
On load and whenever it tries to become ready, the plugin SHALL check that Ktisis is installed and exposes IPC API major version 1. If Ktisis is missing or incompatible, the plugin SHALL show that in the window and SHALL never report ready.

#### Scenario: Ktisis missing
- **WHEN** Ktisis is not installed or not loaded
- **THEN** the window shows "Ktisis not available" and the user is never marked ready

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
