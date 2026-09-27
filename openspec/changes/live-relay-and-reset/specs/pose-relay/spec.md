## Purpose

A small Cloudflare relay that connects the two players of a CoPose session over WebSockets, so session state and pose edits reach the partner immediately, including in GPose, where sync services defer pair data. It forwards opaque messages, remembers each participant's latest state, and knows nothing about poses.

## ADDED Requirements

### Requirement: Session rooms
The relay SHALL accept WebSocket connections at a room path identified by an opaque room id of at least 128 bits (32+ hexadecimal characters), plus a participant id. Each room SHALL admit at most two distinct participants at a time. A reconnect with the same participant id SHALL replace that participant's previous connection instead of counting as a third. Malformed room or participant ids SHALL be rejected before the WebSocket is accepted.

#### Scenario: Two participants join
- **WHEN** A and B connect to the same room id with different participant ids
- **THEN** both connections are accepted and each is told that the other is present

#### Scenario: Third participant refused
- **WHEN** a third distinct participant connects to a room that already has two
- **THEN** the connection is refused with a "room full" reason, and the existing two are unaffected

#### Scenario: Reconnect replaces
- **WHEN** A's connection drops and A reconnects with the same participant id
- **THEN** the new connection replaces the old one and B stays connected

#### Scenario: Bad room id
- **WHEN** a client connects with a room id shorter than 32 hexadecimal characters
- **THEN** the relay rejects the request without opening a WebSocket

### Requirement: Forwarding
The relay SHALL forward every message a participant sends to the other participant in the room, unchanged and in the order it was received. It SHALL NOT echo a message to its sender.

#### Scenario: Message reaches partner
- **WHEN** A sends a message while B is connected
- **THEN** B receives exactly that message, and A does not receive it back

#### Scenario: Partner not connected
- **WHEN** A sends a message while B is not connected
- **THEN** nothing is delivered now, and A's connection stays open

### Requirement: Latest state replay
The relay SHALL keep, per participant, the most recent message that participant marked as a state message. When a participant connects or reconnects, the relay SHALL send it the other participant's stored state, if any, before any newly forwarded messages. Stored state SHALL be discarded when a room has had no connected participants for 10 minutes.

#### Scenario: Catch up on reconnect
- **WHEN** A sent a state message while B was disconnected, and B then connects
- **THEN** B receives A's latest state message immediately after connecting

#### Scenario: Only the latest is kept
- **WHEN** A sends three state messages while B is away
- **THEN** B receives only the third on connecting

#### Scenario: Abandoned room
- **WHEN** both participants have been disconnected for 10 minutes
- **THEN** the room's stored state is gone, and a new connection finds no stored state

### Requirement: Presence notifications
The relay SHALL notify each connected participant when the other participant connects, and when the other participant's connection closes or is replaced by nothing.

#### Scenario: Partner leaves
- **WHEN** B's connection closes
- **THEN** A receives a notification that B left

### Requirement: Limits
The relay SHALL close a connection that sends a message larger than 64 KB, or more than 30 messages in any one-second window. It SHALL NOT store or forward the offending message. It SHALL answer a plain HTTP request on its root path with a small health response so the plugin and the owner can check that it's up.

#### Scenario: Oversized message
- **WHEN** a participant sends a 100 KB message
- **THEN** the relay closes that connection with a "too large" reason and forwards nothing

#### Scenario: Flooding
- **WHEN** a participant sends 50 messages within one second
- **THEN** the relay closes that connection with a "rate limited" reason

#### Scenario: Health check
- **WHEN** someone requests the relay's root path over HTTP
- **THEN** the relay returns a success response naming the service and its protocol version
