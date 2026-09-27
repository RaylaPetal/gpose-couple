import type { PoseRoom } from "./room";

export interface Env {
  POSE_ROOM: DurableObjectNamespace<PoseRoom>;
  RELAY_ENVIRONMENT: string;
}
