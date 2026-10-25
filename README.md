# FXM WebStream

Low-latency video streaming and a shared walkable world for **Unity 6 (URP)** that runs on
**Windows, macOS, Android, iOS, and WebGL** without a single native plugin.

Instead of an H.264/H.265 pipeline, FXM WebStream encodes **one JPEG per frame** and decodes
it straight into a `Texture2D`. There is no codec SDK to install, no platform-specific
decoder, and no container format — the same C# path runs in the Editor, on a phone, in a
browser, and in a desktop player. On top of that transport the package adds a start/watch
UI, playback control, PCM16 audio, camera/video/webcam sources, a Node.js internet relay,
and a third-person demo scene where every streaming device walks around the same city.

---

## Table of contents

- [Features](#features)
- [Why JPEG per frame](#why-jpeg-per-frame)
- [Requirements](#requirements)
- [Repository layout](#repository-layout)
- [Quick start](#quick-start)
- [Controls](#controls)
- [Streaming topologies](#streaming-topologies)
- [Transports](#transports)
- [Internet relay](#internet-relay)
- [Platform support](#platform-support)
- [Cross-platform recipes](#cross-platform-recipes)
- [Building](#building)
- [Tests](#tests)
- [Troubleshooting](#troubleshooting)
- [Documentation](#documentation)
- [Notes and credits](#notes-and-credits)

---

## Features

- **Camera, video clip, video URL, or webcam** as the frame source.
- **Async GPU readback** on desktop/mobile, worker-thread JPEG encoding, and a pooled
  buffer to keep GC spikes out of the render loop.
- **Three transports**: LAN UDP with chunking/reassembly, direct WebSocket (client and
  server), and an internet relay for NAT/carrier networks.
- **Start screen** (`StreamSetupPanel`): pick transport, room, token, source, resolution,
  FPS, JPEG quality, and audio — nothing is sent before you press start.
- **Fullscreen viewer** with Fit/Fill, fade-out control bar, audio toggle, and a grid for
  watching several senders at once.
- **Playback control** from the receiver: play, pause, stop, restart, seek, and live
  quality/FPS changes. The sender owns the state and republishes it every second.
- **PCM16 audio** (16 kHz mono voice, 24 kHz mono balanced, 48 kHz stereo music) carried on
  the same connection as video.
- **Many-to-one / many-to-many**: `MultiStreamReceiver` shows a clickable grid of streams;
  press 1–9 or tap a cell to select the active one.
- **Third-person walking demo**: WASD + mouse look on desktop, analogue stick + swipe on
  Android, idle/walk animation blending, solid city colliders, and networked remote players.
- **Responsive UI**: dark-only theme, safe-area aware, two columns on desktop and full
  screen on phones, settings remembered in `PlayerPrefs` (the relay token is never saved).

## Why JPEG per frame

| Aspect | JPEG per frame (this package) | Video codec (H.264/H.265) |
| --- | --- | --- |
| Compatibility | Every platform, including WebGL | Needs a hardware encoder/decoder and browser support |
| Latency | One frame | GOP, buffering, timestamp sync |
| Bandwidth | Higher | Much lower |
| Complexity | Simple, easy to debug on the wire | Container, timestamps, A/V sync |
| Audio | Optional PCM16 (16–48 kHz) | Built in, compressed |

It is the right trade-off for remote desktop, camera preview, LAN streaming, classroom
demos, and WebGL apps that need a live view at "good enough" quality rather than
broadcast-grade compression. If bandwidth is your bottleneck, lower `resolutionScale`,
`targetFps`, and `jpegQuality` — see [Transports](#transports) and the
[Usage Guide](Assets/FXMWebStream/Documentation/Usage-Guide.md).

## Requirements

| Requirement | Notes |
| --- | --- |
| Unity **6000.5.8f1** | Project version is pinned in `ProjectSettings/ProjectVersion.txt` |
| Universal Render Pipeline **17.5.0** | Used by the demo scene |
| Input System **1.20.0** | Touch stick, swipe look, and tests drive it directly |
| `com.unity.modules.imageconversion` | Required for `Texture2D.LoadImage` (Unity default) |
| Node.js **18+** | Only for the internet relay and its tests |
| Android Build Support | Only for building an APK (IL2CPP, ARM64) |

The runtime package lives entirely in `Assets/FXMWebStream` and has no third-party
dependency — you can copy that folder into another Unity 6 project (with URP and the Input
System) and it compiles as-is.

## Repository layout

```
FXMWebStream/
├── Assets/
│   ├── FXMWebStream/                   # the package
│   │   ├── Runtime/
│   │   │   ├── StreamingSender.cs      # capture → encode → fan-out + playback state
│   │   │   ├── StreamingReceiver.cs    # one source: decode into a RawImage
│   │   │   ├── MultiStreamReceiver.cs  # many sources, grid + audio follow
│   │   │   ├── VideoPlayerStreamSource.cs
│   │   │   ├── WebCamStreamSource.cs
│   │   │   ├── StreamAudioCapture.cs / StreamAudioPlayer.cs
│   │   │   ├── StreamPlaybackControls.cs
│   │   │   ├── StreamSetupPanel.cs     # start screen, watch mode, Many grid
│   │   │   ├── WalkingSim/             # demo: player, network, camera, HUD, protocol
│   │   │   ├── Core/                   # protocols, encoders, buffer pool, receiver core
│   │   │   └── Transports/             # UDP, WebSocket (client/server/relay), WebGL bridge
│   │   ├── Plugins/WebGL/FXMWebSocket.jslib
│   │   ├── Editor/                     # scene wizards (menu Tools ▸ FXM WebStream)
│   │   ├── Samples/
│   │   ├── Tests/                      # EditMode + PlayMode
│   │   └── Documentation/              # setup, usage, development, mentor quickstart
│   ├── Scenes/FXMWebStream.unity       # demo scene (walking sim)
│   ├── Prefabs/                        # Guy, LowPolyCity
│   ├── Models/                         # source FBX + textures
│   └── Videos/display1.mp4             # sample clip
├── Packages/                           # manifest + lock
├── ProjectSettings/                    # layers, quality, build scenes, player settings
└── Tools/
    ├── RelayServer/                    # Node.js WebSocket relay + integration tests
    └── RelayHarness/                   # C# console harness for the relay
```

## Quick start

### 1. Open the project

1. Install Unity **6000.5.8f1** with **Android Build Support** if you plan to build an APK.
2. Open the folder that contains `Assets/`, `Packages/`, and `ProjectSettings/`, and wait
   for the first import to finish.
3. Open `Assets/Scenes/FXMWebStream.unity` and press **Play**.

You get a third-person character in the LowPolyCity prefab with a HUD. Press **F1** to hide
the setup panel, or press **Start streaming** to hide it automatically and begin sending.

### 2. Stream between two devices (relay)

Start the relay on the machine that both devices can reach:

```bash
cd Tools/RelayServer
npm install
FXM_RELAY_TOKEN=change-me npm start
```

Then, on each device's **FXM Canvas** setup panel, choose **Stream** or **Watch**, select
**Relay**, and enter the same values:

| Field | Example |
| --- | --- |
| Relay URL | `ws://192.168.2.246:8787/fxm-relay` (LAN) or `wss://relay.example.com/fxm-relay` |
| Room | `demo` |
| Token | `change-me` (leave empty if the relay has no `FXM_RELAY_TOKEN`) |

Nobody needs to forward a port: both peers connect outbound. Check the relay at
`http://127.0.0.1:8787/healthz` — it reports `senders`, `receivers`, `players`, and
`tokenRequired`.

On a phone, never use `127.0.0.1`; that is the phone itself. Use the PC's Wi-Fi address.

### 3. Add the setup UI to another scene

1. Open a scene that already contains a `StreamingSender` or `StreamingReceiver`.
2. Choose **Tools ▸ FXM WebStream ▸ Add Setup UI (Start / Watch)**.
3. Press **Play**, configure, then press **Start streaming**, **Watch stream**, or **Many**.

### 4. One-minute loopback (no second device)

1. **Tools ▸ FXM WebStream ▸ Add Loopback Test Setup**.
2. Press **Play**: the camera renders into a RenderTexture, the receiver shows a rotating
   cube, and the overlay reports FPS, JPEG size, and encode/decode time.
3. Use **Play / Pause / Stop / Restart** below the view.

> The Game view can look empty while a camera is being streamed — the sender points the
> camera at its own RenderTexture. Hide the panel to see the preview fullscreen, watch the
> receiver, or stop the stream to restore the camera.

## Controls

The demo scene is playable on desktop and on a phone with the same build.

| Control | Desktop | Android / iOS |
| --- | --- | --- |
| Move | **WASD** or arrow keys | Left analogue stick |
| Look around | Move the **mouse** (third-person orbit) | **Swipe** anywhere outside the stick |
| Setup panel | **F1** or the **Setup** button | **Setup** in the fullscreen bar |
| Choose a source (Many) | Click a cell or press **1–9** | Tap a cell |
| Exit | **Exit** twice | **Exit** twice |

While streaming, the **FXM Canvas** playback overlay is hidden so the 3D world and the
scene skybox are fully visible; it returns when you switch to **Watch** or **Many**.

## Streaming topologies

| Topology | How to set it up |
| --- | --- |
| One-to-many (relay) | One sender, any number of receivers in the same room |
| One-to-many (UDP) | Fill `StreamingSender.udpReceivers` or call `AddUdpReceiver("host:port")` |
| One-to-many (WebSocket) | Sender runs a server; every connected client gets the broadcast |
| Many-to-one | **Many** in the setup panel, or `MultiStreamReceiver` with several sources |
| Many-to-many | Each node sends to its own room; everyone selects the other rooms under **Many** |
| Shared world | Every streaming node joins the same **Game Room** on the walking sim |

Video rooms and the gameplay room are independent: for many-to-one, give each sender its
own video room but the same **Game Room**, so viewers see the streams in a grid while the
characters share one world. A device that only watches does not enter the world — the
player channel starts with **Stream**.

## Transports

| | UDP | WebSocket | Relay |
| --- | --- | --- | --- |
| Scope | LAN | LAN or internet (port forwarding / reverse proxy) | Internet, works behind NAT |
| Who listens | Receiver | Sender (server mode) | The relay host |
| WebGL | Not available | Client only (via browser) | Yes |
| Audio | Same socket | Same socket | Same socket |
| Control channel | Yes | Yes | Yes |
| Best for | Same Wi-Fi, lowest latency | Known host, no relay | Phones, different networks |

The wire formats are deliberately small and versioned:

- **UDP**: `FXS1` header, sequence number, chunk index/count, reassembly with timeout and
  duplicate/loss handling.
- **Relay/WebSocket**: raw binary frames forwarded untouched; the relay only routes.
- **Control**: 28-byte messages (play, pause, stop, restart, seek, quality, FPS).
- **Audio**: PCM16 packets with an `FXA1` header.
- **Gameplay**: 32-byte `FXG1` state packet (position, yaw, animation flag), forwarded only
  to other players.

## Internet relay

`Tools/RelayServer` is a ~300-line Node.js WebSocket relay. It forwards frames, audio, and
control messages between a sender and its receivers, and gameplay state between players. It
never decodes, transcodes, or stores media, so it stays cheap to host.

### Run it

```bash
cd Tools/RelayServer
npm install
FXM_RELAY_TOKEN=change-me npm start
```

PowerShell:

```powershell
npm install
$env:FXM_RELAY_TOKEN="change-me"
npm start
```

### Configuration

| Variable | Default | Purpose |
| --- | --- | --- |
| `PORT` | `8787` | HTTP and WebSocket port |
| `HOST` | `0.0.0.0` | Bind address |
| `FXM_RELAY_TOKEN` | empty | Shared secret; set it in production |
| `FXM_RELAY_PATH` | `/fxm-relay` | WebSocket path |
| `FXM_MAX_RECEIVERS` | `32` | Receiver limit per room |
| `FXM_MAX_BUFFERED_BYTES` | `4194304` | Per-receiver backpressure limit |
| `FXM_ALLOWED_ORIGINS` | empty | Comma-separated browser origins; empty allows all |
| `FXM_HEARTBEAT_MS` | `30000` | Ping interval for dead-connection cleanup |

### URL contract

```
ws://host:8787/fxm-relay?room=demo&role=sender&token=change-me
wss://relay.example.com/fxm-relay?room=demo&role=receiver&token=change-me
wss://relay.example.com/fxm-relay?room=world&role=player&token=change-me
```

- `room` matches `[A-Za-z0-9_-]{1,64}`.
- `role` is `sender`, `receiver`, or `player`.
- One sender per room; a newer sender takes over (close code `4001`), which makes
  reconnects safe. Receivers may join before the sender.
- `player` traffic is forwarded to the other players in the room and never echoed back. It
  is kept separate from video, so a gameplay room can share a room name with a stream.

### Health and deploy

```bash
curl http://127.0.0.1:8787/healthz
# {"status":"ok","rooms":1,"senders":1,"receivers":2,"players":3,"tokenRequired":true,"uptimeSeconds":42}
```

```bash
docker build -t fxm-relay Tools/RelayServer
docker run -d --name fxm-relay -e FXM_RELAY_TOKEN=change-me -p 8787:8787 fxm-relay
```

Terminate TLS with a reverse proxy and use `wss://` from any HTTPS page. Example Caddyfile:

```
relay.example.com {
    reverse_proxy 127.0.0.1:8787
}
```

## Platform support

| Platform | UDP | WebSocket | Relay | AsyncGPUReadback | Background encode |
| --- | --- | --- | --- | --- | --- |
| Windows / macOS (standalone) | Yes | Yes (client & server) | Yes | Yes | Yes |
| Android / iOS | Yes | Yes (client; server may work) | Yes | Yes | Yes |
| WebGL | No | Yes (client, via browser) | Yes | No | No (`EncodeToJPG` on the main thread) |

WebGL specifics:

- The browser owns the socket; `Plugins/WebGL/FXMWebSocket.jslib` bridges it to C#.
- A page served over HTTPS must use `wss://` — plain `ws://` is blocked as mixed content.
- There is no UDP and no async readback, so keep `resolutionScale`, `targetFps`, and
  `jpegQuality` modest to avoid main-thread hitches.
- A WebGL peer is always a client: run the relay, or a WebSocket server on a desktop.

## Cross-platform recipes

- **Desktop → desktop**: relay on both, same room. Works across NAT.
- **Desktop → Android**: relay sender on desktop, relay receiver on the APK.
- **One laptop → two phones**: same receiver APK on both phones, same room and token.
- **Two cameras on one phone**: each sender uses its own room; list both under **Many**.
- **Android → desktop**: `WebCamStreamSource` + sender on the phone, relay receiver on the PC.
- **LAN only**: UDP, or a WebSocket server on the desktop; connect by LAN IP.
- **With audio**: enable **Stream audio** on the sender and **Play audio** on the receiver.

## Building

### Windows / macOS

1. **File ▸ Build Profiles ▸ Windows** (or macOS).
2. Add `Assets/Scenes/FXMWebStream.unity` to the scene list.
3. Build and run. For a LAN-only demo, you can build one player with a runtime toggle that
   starts either the sender or the receiver.

### Android

1. **File ▸ Build Profiles ▸ Android**, then **Switch Platform**.
2. **Edit ▸ Project Settings ▸ Player ▸ Android**:
   - **Internet Access**: `Require`
   - **Scripting Backend**: `IL2CPP`
   - **Target Architectures**: `ARM64` (add `ARMv7` only if you need it)
   - **Allow downloads over HTTP**: `Always` only while testing `http://`/`ws://`;
     prefer HTTPS/WSS
3. The manifest already declares `INTERNET` and `CAMERA`; the app asks for the camera at
   runtime when a webcam source starts.
4. Build, install, and start the sender first, then the receiver (or vice versa — receivers
   may wait for a sender).

### WebGL

1. **File ▸ Build Profiles ▸ WebGL**, build, and serve the output over HTTP(S) — a
   `file://` page cannot open a WebSocket reliably.
2. Select **Relay** and use `wss://` if the page is HTTPS, or a **Server URL** pointing at a
   desktop WebSocket server.

## Tests

| Suite | Location | What it covers |
| --- | --- | --- |
| EditMode (53 tests) | `Assets/FXMWebStream/Tests/Editor` | JPEG round-trip and orientation, async encoder, UDP reassembly (loss, duplicate, reorder, timeout), control protocol, relay URL parsing, local video paths, setup-panel validation, grid layout, audio packets, many-source validation, gameplay packets |
| PlayMode (6 tests) | `Assets/FXMWebStream/Tests/PlayMode` | Camera → readback → encode → UDP → decode → `RawImage`; play/pause/stop/restart; two senders into one `MultiStreamReceiver`; demo scene building the character and camera; simulated keys and mouse for movement, walk blending, and orbit look; remote avatars blocking the player; skybox clear mode and the FXM Canvas hiding in gameplay |
| Relay integration | `Tools/RelayServer/test` | Fan-out, audio forwarding, player-state fan-out and isolation, room isolation, token rejection, sender takeover, health |
| Relay harness | `Tools/RelayHarness` | Frame/control routing and automatic reconnect through the real relay using the Unity transport |

```bash
# Unity: Window ▸ General ▸ Test Runner, then Run All (EditMode / PlayMode)

cd Tools/RelayServer && npm test
cd Tools/RelayHarness && dotnet run -c Release
```

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| Menu missing | Wait for compilation; check the Console for errors |
| Black view | Assign a clip or URL; check VideoPlayer errors; allow camera permission |
| Webcam upside down | Enable `flipVertically` on `WebCamStreamSource` |
| Receiver stays black on relay | Same URL, room, and token; check `/healthz`; use `wss://` on HTTPS pages |
| Phone says “Watching. Waiting for the sender.” | Check **Relay: connected** and `/healthz`: `senders:0` means the sender never joined; `receivers:0` usually means `127.0.0.1`, a firewall, or a different Wi-Fi |
| Game view empty while streaming | Expected for camera senders — hide the panel or open the receiver |
| No analogue stick on Android | Hide the setup panel first; the stick draws over the game view |
| Look does not turn on a phone | Swipe outside the stick area; check that the build is Android/iOS, not desktop |
| Remote players never appear | Same **Game Room** and relay, and both devices pressed **Start streaming** |
| Video stretched on a phone | Keep `preserveAspectRatio` on; the panel forces it on |
| `401` on relay | Token mismatch — if the relay has no `FXM_RELAY_TOKEN`, leave **Token** empty on both sides |

## Documentation

| Document | Audience |
| --- | --- |
| [Setup Guide](Assets/FXMWebStream/Documentation/Setup-Guide.md) | First scene, video input, relay, Windows/Android builds |
| [Usage Guide](Assets/FXMWebStream/Documentation/Usage-Guide.md) | Field reference, topologies, tuning, troubleshooting |
| [Development Guide](Assets/FXMWebStream/Documentation/Development-Guide.md) | Architecture, protocols, threading, extension points |
| [Mentor Quickstart](Assets/FXMWebStream/Documentation/Mentor-Quickstart.md) | PC sender + Android receiver across two homes |
| [Relay README](Tools/RelayServer/README.md) | Relay configuration, deployment, and tests |
| [Package README](Assets/FXMWebStream/README.md) | Package-level overview and API pointers |

## Notes and credits

- The streamed camera renders into a RenderTexture — an empty Game view while streaming is
  by design.
- The control channel is unauthenticated. Enable `allowRemoteControl` only on trusted
  networks or behind a relay token.
- Demo art (`Guy`, `LowPolyCity`, sample video) is included for the scene to work out of the
  box; check the original asset licenses before redistributing it.
- Android and desktop players are built from the same scene; device-specific audio capture
  and the multi-source panel should be smoke-tested with one sender and two viewers before
  you hand out a build.
