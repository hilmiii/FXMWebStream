"use strict";

const test = require("node:test");
const assert = require("node:assert");
const { once } = require("node:events");
const WebSocket = require("ws");
const { createRelayServer } = require("../server");

function open(url) {
  return new Promise((resolve, reject) => {
    const socket = new WebSocket(url);
    socket.once("open", () => resolve(socket));
    socket.once("unexpected-response", (_request, response) => {
      reject(new Error(`HTTP ${response.statusCode}`));
    });
    socket.once("error", reject);
  });
}

function nextBinary(socket, timeoutMs = 3000) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      socket.off("message", onMessage);
      reject(new Error("timed out waiting for a binary message"));
    }, timeoutMs);

    function onMessage(data, isBinary) {
      if (!isBinary) {
        return;
      }
      clearTimeout(timer);
      socket.off("message", onMessage);
      resolve(Buffer.from(data));
    }

    socket.on("message", onMessage);
  });
}

async function startRelay(options = {}) {
  const relay = createRelayServer({ log: () => {}, ...options });
  relay.server.listen(0, "127.0.0.1");
  await once(relay.server, "listening");
  const { port } = relay.server.address();
  const base = `ws://127.0.0.1:${port}${relay.path}`;
  return { relay, base };
}

test("forwards frames from one sender to every receiver and control back", async () => {
  const { relay, base } = await startRelay();
  const sender = await open(`${base}?room=alpha&role=sender`);
  const receiverA = await open(`${base}?room=alpha&role=receiver`);
  const receiverB = await open(`${base}?room=alpha&role=receiver`);

  try {
    const frame = Buffer.from([0xff, 0xd8, 0xff, 0xe0, 1, 2, 3]);
    const aWait = nextBinary(receiverA);
    const bWait = nextBinary(receiverB);
    sender.send(frame, { binary: true });

    assert.deepStrictEqual(await aWait, frame);
    assert.deepStrictEqual(await bWait, frame);

    const control = Buffer.concat([Buffer.from("FXC1"), Buffer.from([1, 2, 3, 4])]);
    const senderWait = nextBinary(sender);
    receiverA.send(control, { binary: true });
    assert.deepStrictEqual(await senderWait, control);
  } finally {
    sender.close();
    receiverA.close();
    receiverB.close();
    await relay.close();
  }
});

test("players receive each other but not their own state", async () => {
  const { relay, base } = await startRelay();
  const playerA = await open(`${base}?room=world&role=player`);
  const playerB = await open(`${base}?room=world&role=player`);
  const playerC = await open(`${base}?room=world&role=player`);

  try {
    let echoed = false;
    playerA.on("message", () => {
      echoed = true;
    });

    const packet = Buffer.from("FXG1-state");
    const waitB = nextBinary(playerB);
    const waitC = nextBinary(playerC);
    playerA.send(packet, { binary: true });

    assert.deepStrictEqual(await waitB, packet);
    assert.deepStrictEqual(await waitC, packet);
    await new Promise((resolve) => setTimeout(resolve, 150));
    assert.strictEqual(echoed, false, "the relay must not echo state to the sender");
  } finally {
    playerA.close();
    playerB.close();
    playerC.close();
    await relay.close();
  }
});

test("player state stays out of the video room's sender and receivers", async () => {
  const { relay, base } = await startRelay();
  const sender = await open(`${base}?room=shared&role=sender`);
  const receiver = await open(`${base}?room=shared&role=receiver`);
  const player = await open(`${base}?room=shared&role=player`);

  try {
    let senderGotState = false;
    let receiverGotState = false;
    sender.on("message", () => {
      senderGotState = true;
    });
    receiver.on("message", () => {
      receiverGotState = true;
    });

    player.send(Buffer.from("FXG1-state"), { binary: true });
    await new Promise((resolve) => setTimeout(resolve, 200));

    assert.strictEqual(senderGotState, false);
    assert.strictEqual(receiverGotState, false);

    const frame = Buffer.from([0xff, 0xd8, 0xff]);
    const wait = nextBinary(receiver);
    sender.send(frame, { binary: true });
    assert.deepStrictEqual(await wait, frame);
  } finally {
    sender.close();
    receiver.close();
    player.close();
    await relay.close();
  }
});

test("keeps rooms isolated", async () => {
  const { relay, base } = await startRelay();
  const sender = await open(`${base}?room=room-a&role=sender`);
  const receiver = await open(`${base}?room=room-b&role=receiver`);

  try {
    let received = false;
    receiver.on("message", () => {
      received = true;
    });

    sender.send(Buffer.from([1, 2, 3]), { binary: true });
    await new Promise((resolve) => setTimeout(resolve, 250));
    assert.strictEqual(received, false);
  } finally {
    sender.close();
    receiver.close();
    await relay.close();
  }
});

test("rejects an invalid token", async () => {
  const { relay, base } = await startRelay({ token: "secret" });

  try {
    await assert.rejects(() => open(`${base}?room=alpha&role=receiver&token=wrong`), /HTTP 401/);
  } finally {
    await relay.close();
  }
});

test("a newer sender takes over the room after a reconnect", async () => {
  const { relay, base } = await startRelay();
  const first = await open(`${base}?room=alpha&role=sender`);
  const receiver = await open(`${base}?room=alpha&role=receiver`);
  const closed = once(first, "close");
  const second = await open(`${base}?room=alpha&role=sender`);

  try {
    const [code] = await closed;
    assert.strictEqual(code, 4001);

    const frame = Buffer.from([9, 8, 7]);
    const wait = nextBinary(receiver);
    second.send(frame, { binary: true });
    assert.deepStrictEqual(await wait, frame);
  } finally {
    second.close();
    receiver.close();
    await relay.close();
  }
});

test("forwards PCM16 audio packets unchanged", async () => {
  const { relay, base } = await startRelay();
  const sender = await open(`${base}?room=audio&role=sender`);
  const receiver = await open(`${base}?room=audio&role=receiver`);

  try {
    const audio = Buffer.concat([
      Buffer.from("FXA1"),
      Buffer.from([1, 0, 20, 0, 7, 0, 0, 0, 0, 0, 0, 0, 0x80, 0xBB, 1, 0, 16, 0, 1, 2]),
      Buffer.from([0, 0, 0x10, 0x00, 0xf0, 0x7f])
    ]);
    const wait = nextBinary(receiver);
    sender.send(audio, { binary: true });
    assert.deepStrictEqual(await wait, audio);
  } finally {
    sender.close();
    receiver.close();
    await relay.close();
  }
});

test("health endpoint reports room and peer counts", async () => {
  const { relay, base } = await startRelay();
  const sender = await open(`${base}?room=alpha&role=sender`);
  const receiver = await open(`${base}?room=alpha&role=receiver`);
  const httpBase = base.replace("ws://", "http://").replace(relay.path, "");

  try {
    const response = await fetch(`${httpBase}/healthz`);
    const body = await response.json();
    assert.strictEqual(response.status, 200);
    assert.strictEqual(body.status, "ok");
    assert.strictEqual(body.rooms, 1);
    assert.strictEqual(body.senders, 1);
    assert.strictEqual(body.receivers, 1);
    assert.strictEqual(body.players, 0);
    assert.strictEqual(body.tokenRequired, false);
  } finally {
    sender.close();
    receiver.close();
    await relay.close();
  }
});
