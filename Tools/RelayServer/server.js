"use strict";

const http = require("http");
const crypto = require("crypto");
const { WebSocketServer, WebSocket } = require("ws");

const DEFAULT_PATH = "/fxm-relay";
const CONTROL_MAGIC = Buffer.from("FXC1");
const ROOM_PATTERN = /^[A-Za-z0-9_-]{1,64}$/;

function timingSafeEqual(a, b) {
  const left = Buffer.from(String(a));
  const right = Buffer.from(String(b));
  if (left.length !== right.length) {
    return false;
  }
  return crypto.timingSafeEqual(left, right);
}

function sendJson(socket, statusCode, body) {
  const payload = JSON.stringify(body);
  socket.write(
    `HTTP/1.1 ${statusCode} ${statusCode === 200 ? "OK" : "Error"}\r\n` +
      "Content-Type: application/json\r\n" +
      `Content-Length: ${Buffer.byteLength(payload)}\r\n` +
      "Connection: close\r\n\r\n" +
      payload
  );
  socket.destroy();
}

function createRelayServer(options = {}) {
  const path = options.path || process.env.FXM_RELAY_PATH || DEFAULT_PATH;
  const token = options.token !== undefined ? options.token : process.env.FXM_RELAY_TOKEN || "";
  const maxReceivers = Number(options.maxReceivers || process.env.FXM_MAX_RECEIVERS || 32);
  const maxBufferedBytes = Number(
    options.maxBufferedBytes || process.env.FXM_MAX_BUFFERED_BYTES || 4 * 1024 * 1024
  );
  const heartbeatMs = Number(options.heartbeatMs || process.env.FXM_HEARTBEAT_MS || 30000);
  const log = options.log || ((message) => console.log(`[relay] ${message}`));

  const allowedOrigins = String(options.allowedOrigins || process.env.FXM_ALLOWED_ORIGINS || "")
    .split(",")
    .map((value) => value.trim())
    .filter(Boolean);

  /** @type {Map<string, {sender: WebSocket|null, receivers: Set<WebSocket>, players: Set<WebSocket>}>} */
  const rooms = new Map();

  const server = http.createServer((request, response) => {
    if (request.url === "/healthz") {
      let receivers = 0;
      let senders = 0;
      let players = 0;
      for (const room of rooms.values()) {
        if (room.sender) senders += 1;
        receivers += room.receivers.size;
        players += room.players.size;
      }
      const body = JSON.stringify({
        status: "ok",
        rooms: rooms.size,
        senders,
        receivers,
        players,
        tokenRequired: Boolean(token),
        uptimeSeconds: Math.round(process.uptime())
      });
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(body);
      return;
    }

    response.writeHead(404, { "Content-Type": "text/plain" });
    response.end("FXM WebStream relay");
  });

  const wss = new WebSocketServer({ noServer: true, maxPayload: 16 * 1024 * 1024 });

  function getRoom(name) {
    let room = rooms.get(name);
    if (!room) {
      room = { sender: null, receivers: new Set(), players: new Set() };
      rooms.set(name, room);
    }
    return room;
  }

  function removeSocket(socket) {
    const roomName = socket.fxmRoom;
    if (!roomName) {
      return;
    }

    const room = rooms.get(roomName);
    if (!room) {
      return;
    }

    if (socket.fxmRole === "sender" && room.sender === socket) {
      room.sender = null;
      log(`sender left room ${roomName}`);
    } else if (room.receivers.delete(socket)) {
      log(`receiver left room ${roomName} (${room.receivers.size} left)`);
    } else if (room.players.delete(socket)) {
      log(`player left room ${roomName} (${room.players.size} left)`);
    }

    if (!room.sender && room.receivers.size === 0 && room.players.size === 0) {
      rooms.delete(roomName);
    }
  }

  function forward(target, payload, isControl) {
    if (!target || target.readyState !== WebSocket.OPEN) {
      return false;
    }

    if (!isControl && target.bufferedAmount > maxBufferedBytes) {
      return false;
    }

    try {
      target.send(payload, { binary: true });
      return true;
    } catch (error) {
      log(`forward failed: ${error.message}`);
      return false;
    }
  }

  wss.on("connection", (socket, request, context) => {
    const { roomName, role } = context;
    const room = getRoom(roomName);
    socket.fxmRoom = roomName;
    socket.fxmRole = role;
    socket.isAlive = true;

    if (role === "sender") {
      if (room.sender && room.sender !== socket) {
        log(`sender takeover in room ${roomName}`);
        try {
          room.sender.close(4001, "replaced by a newer sender");
        } catch {
          room.sender.terminate();
        }
      }

      room.sender = socket;
      log(`sender joined room ${roomName} (${room.receivers.size} receiver(s))`);
    } else if (role === "player") {
      if (room.players.size >= maxReceivers) {
        socket.close(1013, "room is full");
        return;
      }

      room.players.add(socket);
      log(`player joined room ${roomName} (${room.players.size} player(s))`);
    } else {
      if (room.receivers.size >= maxReceivers) {
        socket.close(1013, "room is full");
        return;
      }

      room.receivers.add(socket);
      log(`receiver joined room ${roomName} (${room.receivers.size} receiver(s))`);
    }

    socket.on("pong", () => {
      socket.isAlive = true;
    });

    socket.on("message", (data, isBinary) => {
      if (!isBinary) {
        return;
      }

      const payload = Buffer.isBuffer(data) ? data : Buffer.from(data);
      const isControl = payload.length >= 4 && payload.subarray(0, 4).equals(CONTROL_MAGIC);

      if (role === "sender") {
        for (const receiver of room.receivers) {
          forward(receiver, payload, isControl);
        }
      } else if (role === "player") {
        // Gameplay state is symmetric: every player receives every other player's
        // updates, and the relay never echoes a packet back to its sender.
        for (const player of room.players) {
          if (player !== socket) {
            forward(player, payload, isControl);
          }
        }
      } else if (room.sender) {
        forward(room.sender, payload, isControl);
      }
    });

    socket.on("close", () => removeSocket(socket));
    socket.on("error", (error) => {
      log(`socket error in room ${roomName}: ${error.message}`);
      removeSocket(socket);
    });
  });

  server.on("upgrade", (request, socket, head) => {
    socket.on("error", () => socket.destroy());

    let url;
    try {
      url = new URL(request.url, "http://relay.local");
    } catch {
      sendJson(socket, 400, { error: "invalid URL" });
      return;
    }

    if (url.pathname !== path) {
      sendJson(socket, 404, { error: "unknown relay path" });
      return;
    }

    if (allowedOrigins.length > 0 && request.headers.origin) {
      if (!allowedOrigins.includes(request.headers.origin)) {
        sendJson(socket, 403, { error: "origin not allowed" });
        return;
      }
    }

    const roomName = url.searchParams.get("room") || "";
    const role = url.searchParams.get("role") || "";
    const providedToken = url.searchParams.get("token") || "";

    if (!ROOM_PATTERN.test(roomName)) {
      sendJson(socket, 400, { error: "invalid room" });
      return;
    }

    if (role !== "sender" && role !== "receiver" && role !== "player") {
      sendJson(socket, 400, { error: "invalid role" });
      return;
    }

    if (token && !timingSafeEqual(providedToken, token)) {
      sendJson(socket, 401, { error: "invalid token" });
      return;
    }

    wss.handleUpgrade(request, socket, head, (websocket) => {
      wss.emit("connection", websocket, request, { roomName, role });
    });
  });

  const heartbeat = setInterval(() => {
    for (const socket of wss.clients) {
      if (socket.isAlive === false) {
        socket.terminate();
        continue;
      }

      socket.isAlive = false;
      try {
        socket.ping();
      } catch {
        socket.terminate();
      }
    }
  }, heartbeatMs);

  heartbeat.unref();

  function close() {
    clearInterval(heartbeat);
    for (const socket of wss.clients) {
      try {
        socket.close(1001, "relay shutting down");
      } catch {
        socket.terminate();
      }
    }
    return new Promise((resolve) => {
      wss.close(() => server.close(() => resolve()));
    });
  }

  return { server, wss, rooms, close, path };
}

if (require.main === module) {
  const port = Number(process.env.PORT || 8787);
  const host = process.env.HOST || "0.0.0.0";
  const relay = createRelayServer();

  relay.server.listen(port, host, () => {
    console.log(`[relay] listening on ws://${host}:${port}${relay.path}`);
    console.log(`[relay] token ${process.env.FXM_RELAY_TOKEN ? "required" : "not required"}`);
    console.log("[relay] senders and receivers join the same room; one sender per room");
    console.log("[relay] players join role=player and receive each other's state; video is unchanged");
  });

  const shutdown = () => {
    console.log("[relay] shutting down");
    relay.close().then(() => process.exit(0));
  };

  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}

module.exports = { createRelayServer };
