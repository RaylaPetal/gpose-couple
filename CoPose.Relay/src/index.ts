import type { Env } from "./env";
import { PARTICIPANT_ID, PROTOCOL, ROOM_ID } from "./protocol";

export { PoseRoom } from "./room";

const ROOM_PATH = /^\/v1\/room\/([^/]+)$/;

export default {
  async fetch(request, env): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname === "/" && request.method === "GET") {
      return Response.json({ service: "copose-relay", protocol: PROTOCOL });
    }

    const match = url.pathname.match(ROOM_PATH);
    if (!match) {
      return new Response("not found", { status: 404 });
    }

    const roomId = match[1] ?? "";
    const participantId = url.searchParams.get("p") ?? "";
    if (!ROOM_ID.test(roomId)) {
      return new Response("bad room id", { status: 400 });
    }
    if (!PARTICIPANT_ID.test(participantId)) {
      return new Response("bad participant id", { status: 400 });
    }
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
      return new Response("expected websocket", { status: 426 });
    }

    const room = env.POSE_ROOM.get(env.POSE_ROOM.idFromName(roomId));
    return room.fetch(request);
  },
} satisfies ExportedHandler<Env>;
