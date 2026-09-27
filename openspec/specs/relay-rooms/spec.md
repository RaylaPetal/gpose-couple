# relay-rooms Specification

## Purpose
The session host that runs inside one player's CoPose plugin and acts as the relay. It accepts a single authenticated guest and forwards all session messages in one total order to every participant, so pairing needs no external server. It holds no game logic and persists nothing.

## Requirements

### Requirement: Start and stop hosting
A plugin SHALL be able to start hosting a session by listening for TCP connections on a configurable port (default 47715) on all local IPv4 interfaces. When hosting starts, a new random session secret of at least 64 bits SHALL be generated. Stopping hosting SHALL close the listener, disconnect any guest and invalidate the secret.

#### Scenario: Host started
- **WHEN** the user starts hosting and the port is free
- **THEN** the plugin listens on that port and the host is listed as the session's only participant

#### Scenario: Port in use
- **WHEN** the user starts hosting and the configured port is already in use
- **THEN** hosting fails with an error that names the port, and no session exists

#### Scenario: Stop hosting
- **WHEN** the host stops hosting while a guest is connected
- **THEN** the guest is disconnected and sees the session ended, and a later connection attempt with the old invite fails

### Requirement: Invite code
While hosting, the host SHALL produce an invite code that encodes the protocol version, the session secret and one to three endpoints, each an IPv4 address **with its own port**: the primary LAN address, a user-entered public/tunnel address, and the router's public address when known, in that order. The code SHALL be a single copyable string with a `CP2-` prefix and no characters that are easily confused. Decoding a malformed, wrong-format or wrong-version code SHALL fail with an error instead of connecting.

#### Scenario: Invite round-trip
- **WHEN** an invite code is produced and then decoded
- **THEN** the decoded version, secret and endpoints (addresses and ports) equal the originals

#### Scenario: Malformed invite
- **WHEN** a user tries to join with a string that isn't a valid invite code
- **THEN** the join fails immediately with "invalid invite code" and no connection is attempted

### Requirement: Guest authentication
A connecting guest SHALL send a hello containing the protocol version, the session secret, its client id, its actor key and a display name, within 5 seconds of connecting. The host SHALL close connections that send no hello in time, present a wrong secret, or have a different protocol version, and SHALL tell the guest the reason before closing. The host SHALL accept at most one guest at a time. On acceptance, the host SHALL reply with a welcome listing all participants (host and guest).

#### Scenario: Successful join
- **WHEN** a guest connects and sends a hello with the correct secret and version
- **THEN** the guest receives a welcome listing host and guest, and the host is notified that the guest joined

#### Scenario: Wrong secret
- **WHEN** a connection presents an incorrect secret
- **THEN** the host closes it with reason "invalid invite", and the session is unaffected

#### Scenario: No hello
- **WHEN** a connection sends nothing for 5 seconds
- **THEN** the host closes it

#### Scenario: Version mismatch
- **WHEN** a guest's hello carries a different protocol version
- **THEN** the host closes it with a reason that names both versions

#### Scenario: Session full
- **WHEN** a second guest tries to join while one guest is connected
- **THEN** the host rejects it with "session full", and the existing guest is unaffected

### Requirement: Leave and disconnect
The host SHALL remove the guest when the guest leaves explicitly, when its connection drops, or when no data (including keepalives) has been received from it for 15 seconds. The host SHALL then notify its own session of the departure. Both sides SHALL send a keepalive at least every 5 seconds while idle. A guest SHALL treat the session as ended when the host connection closes or has been silent for 15 seconds.

#### Scenario: Guest leaves
- **WHEN** the guest leaves the session
- **THEN** the host is notified that the guest left, and a new guest may join with the same invite

#### Scenario: Silent connection
- **WHEN** no data arrives from the guest for 15 seconds
- **THEN** the host drops the guest and notifies that it left

### Requirement: Ordered message forwarding
The host SHALL accept envelopes (message type plus opaque body) from any participant, including itself. It SHALL stamp each envelope with the sender's client id and a strictly increasing session sequence number. It SHALL deliver the stamped envelope to **every** participant including the sender (the sender's copy acts as an acknowledgement). It SHALL NOT interpret or modify the body. Every participant SHALL receive envelopes in increasing sequence order.

#### Scenario: Forward to all participants including sender
- **WHEN** the guest sends an envelope
- **THEN** both host and guest receive it with sender id = guest and the same sequence number

#### Scenario: Ordered delivery under concurrency
- **WHEN** host and guest each send many envelopes concurrently
- **THEN** the sequence numbers are strictly increasing in the order the host accepted them, and both participants receive all envelopes in that same order

### Requirement: Frame size limit
The transport SHALL reject frames larger than 128 KB and envelope bodies larger than 64 KB. A peer that sends an oversized frame SHALL be disconnected. An oversized body from the local participant SHALL be refused with an error before sending.

#### Scenario: Oversized frame from guest
- **WHEN** the guest sends a frame whose length prefix exceeds 128 KB
- **THEN** the host disconnects the guest without reading or allocating the frame body
