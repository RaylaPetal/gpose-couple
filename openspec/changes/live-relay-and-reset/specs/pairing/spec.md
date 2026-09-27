## ADDED Requirements

### Requirement: Relay connection
While in a session, the plugin SHALL connect to the relay room derived from both participants' session nonces, and SHALL reconnect automatically with increasing delays (up to 30 seconds) after a failure. The window SHALL show whether the relay is connected, connecting, or unreachable, and why. The relay address SHALL be configurable, with the deployed CoPose relay as the default. Outside a session the plugin SHALL NOT keep a relay connection open.

#### Scenario: Session connects
- **WHEN** A and B become paired
- **THEN** both clients connect to the same relay room within a few seconds and show "relay connected"

#### Scenario: Relay down
- **WHEN** the relay can't be reached
- **THEN** the window shows "relay unreachable" with the error, and the plugin keeps retrying without blocking the game

#### Scenario: Room derivation
- **WHEN** two sessions involve different player pairs or different session nonces
- **THEN** they use different relay rooms

## MODIFIED Requirements

### Requirement: Participant identity in tags
Each participant SHALL be identified in tag data by an actor key made of their character name and home world id. GPose object indices SHALL NOT be written into tags. Tag data from a player SHALL be attributed to the overworld character SimpleHeels reports it on. The tag SHALL carry only pairing data (protocol version, own key, chosen partner, and a random session nonce of at least 128 bits, generated per plugin load) and SHALL NOT carry bone data.

#### Scenario: Tag contents
- **WHEN** the plugin publishes its tag
- **THEN** the tag carries the local player's actor key, the session nonce and, when paired, the partner's actor key, and no object index or bone data

### Requirement: Mutual pairing
The user SHALL be able to choose one listed player as their partner ("Pose with <name>") and to stop ("Stop posing together"). The choice SHALL be published in the tag. A session SHALL start when both players' tags name each other. Once the relay is connected, the session SHALL continue until either player stops, or until the partner has been absent from the relay room and their tag has not named me for 30 seconds. A stop SHALL be sent over the relay as well as the tag, so it takes effect in GPose. When another player has chosen me but I haven't chosen them, the window SHALL show a request I can accept. Only one partner at a time SHALL be allowed. Choosing a player SHALL count as consent for them to pose my character during the session. Pairing needs tags to flow, so the window SHALL advise pairing before entering GPose.

#### Scenario: Request and accept
- **WHEN** A clicks "Pose with B" and B then clicks "Accept" on A's request, both outside GPose
- **THEN** both windows show that A and B are posing together

#### Scenario: One-sided choice
- **WHEN** A has chosen B but B has not chosen A
- **THEN** A's window shows "Waiting for B", B's window shows A's request, and no pose data is applied on either side

#### Scenario: Stop
- **WHEN** either player clicks "Stop posing together", even while both are in GPose
- **THEN** both clients leave the session within a few seconds, stop applying each other's pose data, and clear session state

#### Scenario: Partner goes away
- **WHEN** the partner disconnects from the relay and does not return for 30 seconds (they unload the plugin, crash, or go offline)
- **THEN** the session ends on my side and the window says the partner is gone

### Requirement: Readiness and presence
A participant SHALL be ready when they're in GPose, Ktisis reports posing is on, and both participants' actor keys resolve locally. While the relay is connected, the plugin SHALL send its ready flag and resolved actor keys over the relay whenever they change. The window SHALL show each participant's ready state.

#### Scenario: Becoming ready
- **WHEN** a user in a session enters GPose and turns Ktisis posing on, and both actors resolve
- **THEN** the partner's window shows that user as ready within a second, even though the partner is in GPose

#### Scenario: Posing turned off
- **WHEN** a ready user turns Ktisis posing off
- **THEN** the partner's window shows that user as not ready within a second

### Requirement: Clean shutdown and tag removal
When the plugin is unloaded or disposed, it SHALL send a stop over the relay if connected, close the relay connection, remove its `CoPose` tag from the local player's character, and unregister all IPC subscriptions, commands, UI and framework handlers.

#### Scenario: Unload while posing together
- **WHEN** a player disables the plugin during a session, even in GPose
- **THEN** the partner's session ends within a few seconds, the tag is removed, and no background work keeps running
