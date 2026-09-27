## Purpose

Shared bone posing between paired CoPose clients. Either participant can pose either character with Ktisis, and the resulting bone transforms are mirrored on the other client at interactive rates. The two clients converge through last-writer-wins ordering and full-pose snapshots.

## ADDED Requirements

### Requirement: Detect and send local bone edits on any session actor
While the local participant is ready, the plugin SHALL sample the bone transforms of every resolved session actor, including the partner's character, at up to 20 Hz. Each sample covers local-space position and rotation plus scale. The plugin SHALL send only the bones that changed beyond small tolerances since the last committed state, grouped per actor into a bone delta addressed by actor key and bone name.

#### Scenario: Posing the partner
- **WHEN** user A drags a bone on user B's character with a Ktisis gizmo
- **THEN** A's client sends bone deltas for B's actor key containing that bone

#### Scenario: Idle scene sends nothing
- **WHEN** neither user is changing any bone and no remote updates are arriving
- **THEN** no bone deltas are sent (the debug counter for sent messages stays at 0)

#### Scenario: Not ready
- **WHEN** the local participant is not ready (not in GPose, posing off, or actors unresolved)
- **THEN** no bone deltas are sent

### Requirement: Apply remote bone edits
On receiving a bone delta while ready, the plugin SHALL resolve the actor key locally and apply the received bone transforms to that actor through Ktisis. It SHALL skip bones whose names don't exist on the local skeleton. If the actor can't be resolved or the participant is not ready, the delta SHALL be discarded.

#### Scenario: Mirror a drag
- **WHEN** user A drags a bone on either character
- **THEN** within 200 ms over a LAN connection the same bone on the same character moves to the same local transform on user B's client

#### Scenario: Unknown bone
- **WHEN** a delta contains a bone name the local skeleton does not have
- **THEN** that bone is skipped and the rest of the delta is applied

### Requirement: Host-ordered last-writer-wins
Bone updates SHALL be ordered by the session host's sequence number. For each bone of each actor, the value from the highest-sequence message SHALL be the final value on every client. While a local edit to a bone has been sent but its own echo from the host has not come back yet, the plugin SHALL ignore remote updates for that bone, because any remote update arriving earlier has a lower sequence number.

#### Scenario: Concurrent edits converge
- **WHEN** both users edit the same bone at about the same time and then stop
- **THEN** both clients end with the value from whichever edit the host sequenced last

#### Scenario: Local drag not overridden by older remote data
- **WHEN** user A is dragging a bone and a remote update for that bone arrives before A's own in-flight edit has been echoed back
- **THEN** A's client does not apply the remote value to that bone

#### Scenario: Own echo is not re-applied
- **WHEN** the host echoes A's own bone delta back to A
- **THEN** A records the sequence number as acknowledged and does not re-apply the values

### Requirement: Echo suppression
After applying remote values, the plugin SHALL treat the values the skeleton actually holds as already synced. Applying a remote update SHALL NOT cause the receiver to send that update back.

#### Scenario: No feedback loop
- **WHEN** user A makes a single bone edit and it is applied on B
- **THEN** B sends no bone delta for that bone as a result, and both clients return to zero traffic

### Requirement: Full-pose snapshot on join and on demand
The plugin SHALL send a full pose snapshot (a Ktisis pose export) for its own character when it becomes ready in a session and whenever a participant joins or becomes ready. The user SHALL be able to push a full snapshot of any resolved actor ("Push pose") and to ask the partner for one ("Request resync"). The receiver SHALL apply a received snapshot (rotation, position and scale) to the resolved actor. It SHALL then treat the resulting skeleton as the synced state, and it SHALL do so in the same host order as bone deltas.

#### Scenario: Join mid-scene
- **WHEN** user B joins a session where A has already posed A's character, and B becomes ready
- **THEN** B's client shows A's character in A's current pose without A touching any bone

#### Scenario: Push pose to partner
- **WHEN** user A loads a `.pose` file onto B's character locally and clicks "Push pose" for B's actor
- **THEN** B's client shows B's character in that pose

#### Scenario: Request resync
- **WHEN** user B clicks "Request resync"
- **THEN** A sends a snapshot of A's character, and B applies it

### Requirement: Scope limits of this version
This version SHALL sync bone transforms only. Actor root (world) position/rotation, soft locks, per-partner permissions, and automatic reconnect SHALL NOT be synced or enforced. Joining a session SHALL be treated as consent for the session partner to pose the joiner's character for the length of the session.

#### Scenario: Root move not synced
- **WHEN** user A moves B's character root with the Ktisis actor gizmo
- **THEN** B's character does not move on B's client

### Requirement: Diagnostics
The window SHALL show sent and received messages per second, the average bones per delta, and the last sync error, so the pipeline can be checked in-game.

#### Scenario: Counters update
- **WHEN** a user drags a bone continuously
- **THEN** the sent messages-per-second counter is non-zero on that client and the received counter is non-zero on the partner's client
