## REMOVED Requirements

### Requirement: Start and stop hosting
**Reason**: CoPose no longer hosts sessions; pose data travels in SimpleHeels tags through the players' sync service.
**Migration**: Pair with "Pose with <name>" instead of hosting.

### Requirement: Invite code
**Reason**: Partners are discovered from nearby players' `CoPose` tags, so no invite is needed.
**Migration**: None. `CP2-` invite codes are no longer accepted.

### Requirement: Guest authentication
**Reason**: There are no incoming connections to authenticate. Only players paired with each other in the sync service can receive each other's tags, and a session needs both players to choose each other.
**Migration**: None.

### Requirement: Leave and disconnect
**Reason**: Session end is now detected from the partner's tag disappearing or no longer naming me.
**Migration**: See "Mutual pairing" in `pairing`.

### Requirement: Ordered message forwarding
**Reason**: There is no host to order messages; convergence comes from per-bone logical-clock versions.
**Migration**: See "Per-bone logical-clock last-writer-wins" in `pose-sync`.

### Requirement: Frame size limit
**Reason**: No TCP frames are used. Tag size is bounded by the tag budget in the design.
**Migration**: None.
