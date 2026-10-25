# FXM WebStream Relay

A small WebSocket relay that lets a sender and one or more receivers connect **outbound**
from anywhere, including behind NAT and mobile carrier networks. It forwards JPEG frames,
PCM16 audio packets (magic `FXA1`), and control messages; it does not decode, transcode,
or store media.

## Run locally

```bash
npm install
FXM_RELAY_TOKEN=change-me npm start
```

Windows PowerShell:

```powershell
npm install
$env:FXM_RELAY_TOKEN="change-me"
npm start
```

Health check: `http://127.0.0.1:8787/healthz`.

## Configuration

| Variable | Default | Purpose |
| --- | --- | --- |
| `PORT` | `8787` | HTTP and WebSocket port. |
| `HOST` | `0.0.0.0` | Bind address. |
| `FXM_RELAY_TOKEN` | empty | Shared secret. Set it in production. |
| `FXM_RELAY_PATH` | `/fxm-relay` | WebSocket path. |
| `FXM_MAX_RECEIVERS` | `32` | Receiver limit per room. |
| `FXM_MAX_BUFFERED_BYTES` | `4194304` | Per-receiver backpressure limit. |
| `FXM_ALLOWED_ORIGINS` | empty | Comma-separated browser origins. Empty allows all. |
| `FXM_HEARTBEAT_MS` | `30000` | Ping interval for dead-connection cleanup. |

## URL contract

```
ws://host:8787/fxm-relay?room=demo&role=sender&token=change-me
wss://relay.example.com/fxm-relay?room=demo&role=receiver&token=change-me
wss://relay.example.com/fxm-relay?room=world&role=player&token=change-me
```

- `room` must match `[A-Za-z0-9_-]{1,64}`.
- `role` is `sender`, `receiver`, or `player`.
- `player` is a symmetric gameplay channel: every player state packet is forwarded to
  all other players in the room and never echoed back. Players do not receive video,
  and video senders/receivers do not receive player state, so a walking-sim room can
  share the same room name as a stream without mixing traffic.
- Audio uses the same binary channel as video; the relay does not need a separate port.
- One sender per room. A newer sender replaces the old connection (close code 4001), which
  makes reconnects safe.
- Receivers can join before the sender.

## Deploy

```bash
docker build -t fxm-relay .
docker run -d --name fxm-relay \
  -e FXM_RELAY_TOKEN=change-me \
  -p 8787:8787 \
  fxm-relay
```

Terminate TLS with a reverse proxy and use `wss://`. Example Caddyfile:

```
relay.example.com {
    reverse_proxy 127.0.0.1:8787
}
```

## Test

```bash
npm test
```

The integration suite covers fan-out to two receivers, room isolation, token rejection,
sender takeover, PCM16 audio forwarding, player-state fan-out and isolation, and health
reporting. For the Unity transport
path, also run:

```bash
cd ../RelayHarness
dotnet run -c Release
```
