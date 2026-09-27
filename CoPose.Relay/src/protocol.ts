/** Relay protocol version, reported by the health check. */
export const PROTOCOL = 1;

/** Frame kinds: the first byte of every binary WebSocket frame. The rest is opaque to the relay. */
export const Kind = {
  /** Client full state: stored as the sender's latest, then forwarded. */
  Full: 0x01,
  /** Client live delta or control: forwarded only. */
  Live: 0x02,
  /** Relay: the other participant is connected. */
  PeerJoined: 0x10,
  /** Relay: the other participant disconnected. */
  PeerLeft: 0x11,
} as const;

export const MAX_MESSAGE_BYTES = 64 * 1024;
export const MAX_MESSAGES_PER_SECOND = 30;
export const EMPTY_ROOM_TTL_MS = 10 * 60 * 1000;
export const MAX_PARTICIPANTS = 2;

export const ROOM_ID = /^[0-9a-f]{64}$/;
export const PARTICIPANT_ID = /^[0-9a-f]{32}$/;

export const Close = {
  Replaced: 4000,
  RoomFull: 4003,
  RateLimited: 4008,
  TooLarge: 1009,
  Unsupported: 1003,
} as const;
