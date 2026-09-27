## ADDED Requirements

### Requirement: Per-bone logical-clock last-writer-wins
Every bone of each session actor that either participant has set SHALL carry a version made of a logical clock value and an author (the author's actor key). Versions SHALL be compared by clock, then by author key in ordinal order. A local edit SHALL be given a clock value greater than any clock the client has seen. On receiving the partner's state, the client SHALL adopt every bone whose received version is newer than its own, and SHALL advance its clock past the largest received clock. The result SHALL NOT depend on the order, repetition or loss of intermediate tag updates.

#### Scenario: Concurrent edits converge
- **WHEN** both users edit the same bone at about the same time and then stop
- **THEN** both clients end with the same value for that bone, the one with the newer version

#### Scenario: Missed intermediate update
- **WHEN** A edits a bone three times and B's client only receives A's last tag
- **THEN** B ends with A's final value

#### Scenario: Ongoing local drag wins
- **WHEN** A is still dragging a bone when B's older edit to that bone arrives
- **THEN** A's next sample gives A's value a newer version, and both clients end with A's value

### Requirement: Initial sync and Push pose
When a session starts, or when a participant becomes ready, each participant SHALL stamp its own character's current bones into the shared state as its own edits, unless a newer version for those bones already exists. The user SHALL be able to "Push pose" for any resolved actor, which stamps that actor's current bones with new versions so they win on both clients.

#### Scenario: Pair mid-scene
- **WHEN** A has already posed A's character and then pairs with B, and both are ready
- **THEN** B's client shows A's character in A's current pose without A touching any bone

#### Scenario: Push pose to partner
- **WHEN** user A loads a `.pose` file onto B's character locally and clicks "Push pose" for B's actor
- **THEN** B's client shows B's character in that pose

## MODIFIED Requirements

### Requirement: Detect and send local bone edits on any session actor
While the local participant is ready, the plugin SHALL sample the bone transforms of both session actors, including the partner's character, at up to 20 Hz. Each sample covers local-space position and rotation plus scale. Bones that changed beyond small tolerances since the last committed state SHALL be recorded in the shared state as local edits. The plugin SHALL republish its tag with the full shared state when it has changed, at most twice per second, and SHALL NOT republish when nothing has changed.

#### Scenario: Posing the partner
- **WHEN** user A drags a bone on user B's character with a Ktisis gizmo
- **THEN** A's tag is republished within a second with that bone's new value and version for B's actor key

#### Scenario: Idle scene sends nothing
- **WHEN** neither user is changing any bone
- **THEN** the tag is not republished (the debug publish counter stays unchanged)

#### Scenario: Continuous drag is rate-limited
- **WHEN** a user drags a bone continuously for ten seconds
- **THEN** the tag is republished no more than twice per second

#### Scenario: Not ready
- **WHEN** the local participant is not ready (not in GPose, posing off, or actors unresolved)
- **THEN** no bone edits are recorded or published

### Requirement: Apply remote bone edits
On receiving the partner's tag while ready, the plugin SHALL merge it by the version rule and apply every bone whose value changed as a result to the resolved actor through Ktisis. It SHALL skip bones whose names don't exist on the local skeleton. If an actor can't be resolved or the participant is not ready, its bones SHALL NOT be applied; they SHALL be applied once the participant becomes ready.

#### Scenario: Mirror a drag
- **WHEN** user A moves a bone on either character and pauses
- **THEN** within about 2 seconds the same bone on the same character moves to the same local transform on user B's client

#### Scenario: Unknown bone
- **WHEN** the partner's state contains a bone name the local skeleton does not have
- **THEN** that bone is skipped and the rest are applied

### Requirement: Echo suppression
After applying remote values, the plugin SHALL treat the values the skeleton actually holds as already synced, keeping the received versions. Applying a remote update SHALL NOT create a new local edit.

#### Scenario: No feedback loop
- **WHEN** user A makes a single bone edit and it is applied on B
- **THEN** B records no local edit for that bone, and once both tags carry the same state neither client republishes again

### Requirement: Scope limits of this version
This version SHALL sync bone transforms only, and only between two mutually paired players. Actor root (world) position/rotation, soft locks, per-partner permissions and live (sub-second) dragging SHALL NOT be provided. Mutual pairing SHALL be treated as consent for the partner to pose the player's character for the length of the session.

#### Scenario: Root move not synced
- **WHEN** user A moves B's character root with the Ktisis actor gizmo
- **THEN** B's character does not move on B's client

### Requirement: Diagnostics
The window SHALL show the number of tag publishes and receives, the size of the last published tag, the time since the partner's last tag, and the last sync error, so the pipeline can be checked in-game.

#### Scenario: Counters update
- **WHEN** a user moves bones during a session
- **THEN** that user's publish counter increases and the partner's receive counter increases

## REMOVED Requirements

### Requirement: Host-ordered last-writer-wins
**Reason**: There is no host to order messages. Tags carry state, so convergence now comes from per-bone logical-clock versions.
**Migration**: Replaced by "Per-bone logical-clock last-writer-wins".

### Requirement: Full-pose snapshot on join and on demand
**Reason**: A partner's tag always holds the full shared state, so there is nothing to request, and Ktisis pose-file snapshots are no longer sent.
**Migration**: Replaced by "Initial sync and Push pose". "Request resync" is removed.
