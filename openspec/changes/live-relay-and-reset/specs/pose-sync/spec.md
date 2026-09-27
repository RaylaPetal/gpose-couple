## ADDED Requirements

### Requirement: Automatic resync
While in a session and connected to the relay, each participant SHALL re-send its full authored state every 3 seconds, even when nothing changed, as a state message the relay stores. On connecting or reconnecting, each participant SHALL merge the partner's stored state the relay replays. Two participants who are both ready SHALL therefore converge within a few seconds without anyone pressing "Push pose".

#### Scenario: Enter GPose after the partner posed
- **WHEN** B has posed both characters, and A then enters GPose and becomes ready
- **THEN** within a few seconds A's client shows both characters in B's pose, with no button pressed

#### Scenario: Lost message
- **WHEN** one of A's edit messages is lost or arrives while B's client is momentarily not ready
- **THEN** B still ends with A's value within about 3 seconds of becoming ready

### Requirement: Reset to starting pose
When a participant becomes ready in a session, the plugin SHALL remember each session character's pose as its starting pose. The user SHALL be able to reset either character, or both, to its remembered starting pose. A reset SHALL be recorded as new local edits of that character's bones, so it applies on both clients like any other edit. Leaving the session SHALL forget the starting poses.

#### Scenario: Reset a character
- **WHEN** A and B have posed B's character and A clicks "Reset" for B's character
- **THEN** B's character returns to the pose it had when A became ready, on both clients

#### Scenario: Reset both
- **WHEN** A clicks "Reset both"
- **THEN** both characters return to their starting poses on both clients

## MODIFIED Requirements

### Requirement: Detect and send local bone edits on any session actor
While the local participant is ready, the plugin SHALL sample the bone transforms of both session actors, including the partner's character, at up to 20 Hz. Each sample covers local-space position and rotation plus scale. Bones that changed beyond small tolerances since the last committed state SHALL be recorded in the shared state as local edits. The plugin SHALL send its authored state to the relay when it has changed, at most 10 times per second, and apart from the automatic resync SHALL NOT send when nothing has changed.

#### Scenario: Posing the partner
- **WHEN** user A drags a bone on user B's character with a Ktisis gizmo
- **THEN** within 200 ms A's client sends a state message containing that bone's new value and version for B's actor key

#### Scenario: Idle scene sends nothing
- **WHEN** neither user is changing any bone
- **THEN** the only messages sent are the automatic resyncs every 3 seconds

#### Scenario: Continuous drag is rate-limited
- **WHEN** a user drags a bone continuously for ten seconds
- **THEN** no more than 10 state messages per second are sent

#### Scenario: Not ready
- **WHEN** the local participant is not ready (not in GPose, posing off, or actors unresolved)
- **THEN** no bone edits are recorded or sent

### Requirement: Apply remote bone edits
On receiving the partner's state while ready, the plugin SHALL merge it by the version rule and apply every bone whose value changed as a result to the resolved actor through Ktisis. It SHALL skip bones whose names don't exist on the local skeleton. If an actor can't be resolved or the participant is not ready, its bones SHALL NOT be applied; they SHALL be applied once the participant becomes ready.

#### Scenario: Mirror a drag
- **WHEN** user A moves a bone on either character, and both users are in GPose and ready
- **THEN** within about half a second the same bone on the same character moves to the same local transform on user B's client, while both stay in GPose

#### Scenario: Unknown bone
- **WHEN** the partner's state contains a bone name the local skeleton does not have
- **THEN** that bone is skipped and the rest are applied

### Requirement: Echo suppression
After applying remote values, the plugin SHALL treat the values the skeleton actually holds as already synced, keeping the received versions. Applying a remote update SHALL NOT create a new local edit.

#### Scenario: No feedback loop
- **WHEN** user A makes a single bone edit and it is applied on B
- **THEN** B records no local edit for that bone, and apart from automatic resyncs neither client sends again

### Requirement: Per-bone logical-clock last-writer-wins
Every bone of each session actor that either participant has set SHALL carry a version made of a logical clock value and an author (the author's actor key). Versions SHALL be compared by clock, then by author key in ordinal order. A local edit SHALL be given a clock value greater than any clock the client has seen. On receiving the partner's state, the client SHALL adopt every bone whose received version is newer than its own, and SHALL advance its clock past the largest received clock. The result SHALL NOT depend on the order, repetition or loss of intermediate state messages.

#### Scenario: Concurrent edits converge
- **WHEN** both users edit the same bone at about the same time and then stop
- **THEN** both clients end with the same value for that bone, the one with the newer version

#### Scenario: Missed intermediate update
- **WHEN** A edits a bone three times and B's client only receives A's last state message
- **THEN** B ends with A's final value

#### Scenario: Ongoing local drag wins
- **WHEN** A is still dragging a bone when B's older edit to that bone arrives
- **THEN** A's next sample gives A's value a newer version, and both clients end with A's value

### Requirement: Initial sync and Push pose
When a session starts, or when a participant becomes ready, each participant SHALL stamp its own character's current bones into the shared state as its own edits, unless a newer version for those bones already exists. The user SHALL be able to "Push pose" for any resolved actor, which stamps that actor's current bones with new versions so they win on both clients. Push pose SHALL be an optional override: automatic resync keeps both clients in sync without it.

#### Scenario: Pair mid-scene
- **WHEN** A has already posed A's character and then pairs with B, and both are ready
- **THEN** B's client shows A's character in A's current pose without A touching any bone

#### Scenario: Push pose to partner
- **WHEN** user A loads a `.pose` file onto B's character locally and clicks "Push pose" for B's actor
- **THEN** B's client shows B's character in that pose

### Requirement: Scope limits of this version
This version SHALL sync bone transforms only, and only between two mutually paired players. Actor root (world) position/rotation, soft locks and per-partner permissions SHALL NOT be provided. Mutual pairing SHALL be treated as consent for the partner to pose the player's character for the length of the session.

#### Scenario: Root move not synced
- **WHEN** user A moves B's character root with the Ktisis actor gizmo
- **THEN** B's character does not move on B's client

### Requirement: Diagnostics
The window SHALL show the relay connection state, the number of state messages sent and received, the size of the last sent message, the time since the partner's last message, and the last sync error, so the pipeline can be checked in game.

#### Scenario: Counters update
- **WHEN** a user moves bones during a session
- **THEN** that user's sent counter increases and the partner's received counter increases, and both show the relay as connected
