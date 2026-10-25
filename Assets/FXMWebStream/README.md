# FXM WebStream

Lightweight video streaming for Unity 6 (URP). It sends **one JPEG image per frame**
instead of using a hardware video codec (H.264), so the same pipeline runs on Windows,
macOS, Android, iOS, and WebGL without native plugins.

The package also provides **playback control** (play/pause/stop/restart/seek, quality/FPS
changes), **PCM16 audio**, **VideoPlayer** and **webcam** sources, **internet relay** mode,
and **one-to-many**, **many-to-one**, and **many-to-many** topologies. The setup panel can
watch several senders at once in a grid, so the same build works as a multistream viewer.

## Why JPEG per frame?

| Aspect | JPEG per frame (this package) | Video codec (H.264/H.265) |
| --- | --- | --- |
| Compatibility | Every platform, including WebGL | Needs hardware encoder/decoder and browser support |
| Latency | Low (one frame) | Higher (GOP, buffering) |
| Bandwidth | Higher | Much lower |
| Complexity | Simple and easy to debug | Large (container, timestamps, sync) |
| Audio | PCM16 (optional, 16-48 kHz) | Built in, compressed |

It fits remote desktop, camera preview, LAN streaming, and WebGL apps that need a
realtime view at "good enough" quality.

## Package layout

```
Assets/FXMWebStream/
├── Runtime/
│   ├── StreamingSender.cs         # Capture + async encoding + fan-out + playback control
│   ├── StreamingReceiver.cs       # One source: decode + RawImage + playback buttons
│   ├── MultiStreamReceiver.cs     # Many sources (many-to-one / many-to-many)
│   ├── StreamAudioCapture.cs      # AudioListener mix -> PCM16 packets
│   ├── StreamAudioPlayer.cs       # PCM16 -> streaming AudioClip + AudioSource
│   ├── VideoPlayerStreamSource.cs # VideoPlayer -> RenderTexture + seek/play/pause
│   ├── WebCamStreamSource.cs      # Webcam -> RenderTexture (Android/desktop camera)
│   ├── StreamPlaybackControls.cs  # Play/Pause/Stop/Restart buttons + seek slider
│   ├── StreamSetupPanel.cs        # Start screen: transport, audio, video input, Many + fullscreen viewer
│   ├── WalkingSim/
│   │   ├── WalkingSimBootstrap.cs # City + ground + Guy + camera + HUD setup
│   │   ├── WalkingSimPlayer.cs    # WASD, mouse look, Android stick + swipe, third-person camera
│   │   ├── WalkingSimNetwork.cs   # Relay player room, remote avatars, solid collisions
│   │   ├── WalkingSimProtocol.cs  # 32-byte gameplay state packet (FXG1)
│   │   ├── GuyAnimator.cs         # Idle/walk Playable blend for the Guy model
│   │   └── WalkingSimHud.cs       # Controls, relay status and player count
│   ├── Core/
│   │   ├── INetworkTransport.cs   # Transport contract, config, stats, factory
│   │   ├── StreamControlProtocol.cs # Play/pause/stop/seek messages (28 bytes)
│   │   ├── StreamAudioProtocol.cs # PCM16 packet header and parsing
│   │   ├── IStreamPlayback.cs     # Playback controller + video source interfaces
│   │   ├── StreamReceiverCore.cs  # JPEG decode + texture reuse (1 or N receivers)
│   │   ├── JpegEncoder.cs         # Pure C# baseline JPEG encoder (worker-thread safe)
│   │   ├── AsyncJpegEncoder.cs    # Worker threads + queues + buffer pool
│   │   ├── FrameBufferPool.cs     # byte[] pool (avoids GC spikes)
│   │   └── UdpFrameProtocol.cs    # UDP header, chunking, reassembly
│   └── Transports/
│       ├── UDPTransport.cs        # UDP + chunking + multi-target + control channel
│       ├── WebSocketTransport.cs  # ClientWebSocket + TcpListener server + relay client
│       ├── WebSocketProtocol.cs   # RFC 6455 handshake and framing
│       └── WebGLWebSocketBridge.cs
├── Plugins/WebGL/FXMWebSocket.jslib  # Browser WebSocket bridge for WebGL
├── Editor/StreamSceneWizard.cs    # Loopback, video, webcam, relay, and topology wizards
├── Editor/WalkingSimSceneWizard.cs # Replaces the test cube with the playable demo
├── Samples/DemoSpinner.cs         # Rotating cube for loopback/test scenes
├── Tests/                         # EditMode and PlayMode tests
└── Documentation/                 # Setup, usage, and development guides
```

The relay tooling lives outside the Unity package:

```
Tools/RelayServer/                 # Node.js WebSocket relay + integration tests
Tools/RelayHarness/                # C# harness: relay frames, control, reconnect
```

## Quick start

### 1. Start screen (recommended)

1. Open a scene that already has a sender or receiver.
2. Use **Tools > FXM WebStream > Add Setup UI (Start / Watch)**.
3. Press **Play** and configure the connection and video source.
4. Press **Start streaming**, **Watch stream**, or, for several senders, **Many**.
   Nothing is sent before that.

The panel supports **Relay**, **WebSocket**, and **UDP**, plus **Camera**, **Clip**,
**URL**, and **Webcam** inputs, with a live preview. It uses a responsive, dark-only theme
and sends optional PCM16 audio; it can also watch several relay rooms or WebSocket URLs
in a clickable grid.

Watching starts in a fullscreen viewer that fills the screen over a black background. It
fits the video by default (no zoom or crop); **Fill** crops the edges when you prefer
that. Its control bar fades after a few seconds; tap or move the mouse to show **Setup**,
**Grid/Single**, **Audio**, **Fill/Fit**, and **Stop**. The panel is responsive: two columns on wide
screens, full screen on phones, `Screen.safeArea` aware, with fixed action buttons and
large touch targets. Settings are remembered in `PlayerPrefs`; the relay token is not
saved. **Exit** stops the stream and quits after a second confirming press (in the Editor
it stops Play mode).

### 2. Walking sim demo scene

The project's `FXMWebStream` scene now starts a playable third-person character instead of
the rotating cube. Press **Play**, then press **Hide (F1)** or start streaming. The panel
hides automatically when streaming begins, and the Game view/preview shows what the
sender captures.

| Control | Desktop | Android |
| --- | --- | --- |
| Move | **WASD** or arrow keys | Left analogue stick |
| Look | Move the **mouse** | **Swipe** anywhere outside the stick |
| Setup | **F1** or **Setup** in the fullscreen bar | **Setup** in the fullscreen bar |

The Guy model plays **idle** when standing and **walk** while moving. The LowPolyCity
prefab is instantiated behind it and its buildings get mesh colliders, so the city is
walkable. Both assets are wired from `Assets/Prefabs`; replace them on the **Walking Sim**
object if you use different art.

#### Playing together

Every instance that starts a **Stream** joins the relay's shared gameplay room. The
default is `demo` on `ws://127.0.0.1:8787/fxm-relay`; set the same **Relay Url**,
**Game Room**, and **Token** in the **Walking Sim** component on every device. Remote
players appear as Guys with solid capsule colliders, so players cannot walk through each
other. An **idle** animation plays for a still remote player and **walk** while they move.

Video rooms and the gameplay room are independent. For many-to-one, give each sender its
own video room as before, but use the same **Game Room** on all of them; the viewers see
the players through the video grid while the players share one world. For many-to-many,
each streaming instance joins the same game room and sees the others. A device that is
only watching does not join the world because the player channel starts with **Stream**.

Run the relay before testing with more than one device:

```bash
cd Tools/RelayServer
npm start
```

On a phone, use the PC's Wi-Fi address (for example
`ws://192.168.2.246:8787/fxm-relay`), never `127.0.0.1`. The relay role `player` forwards
each player's 32-byte state packet to the other players and never echoes it back; video
senders and receivers keep their existing roles.

To add the same setup to another scene, use
**Tools > FXM WebStream > Replace Test Cube with Walking Sim**. The menu wires the city,
the Guy model, both clips, the camera, the relay room, and the touch controls.

### 3. Loopback (one scene, about one minute)

1. Open the project in Unity **6000.5.8f1** and wait for the import to finish.
2. Use **Tools > FXM WebStream > Add Loopback Test Setup**.
3. Press **Play**.

The camera renders into a RenderTexture (the Game view may look empty), the `RawImage`
shows a rotating cube, and the overlay reports FPS, JPEG size, and encode/decode time.
Use **Play / Pause / Stop / Restart** below the view. Pause freezes the last frame; Stop
clears the view; Play wakes the stream again.

### 4. Add a video

- **VideoClip asset** (recommended for Android/iOS): drop an mp4/webm into `Assets`,
  then assign it to **Clip** on `VideoPlayerStreamSource`.
- **Video file wizard**: **Tools > FXM WebStream > Add Video File Setup...** copies the
  file to `Assets/FXMWebStream/Media`, imports it as a VideoClip, and builds the scene.
- **Setup UI**: **Video clip** lists every clip in the project and adds **Choose local
  video...**, an in-panel folder browser for mp4/webm/mov/m4v/ogv files. **Video URL**
  accepts the same values as the `Url` field. Both are applied before the stream starts.
- **URL**: put an `https://` mp4 or HLS URL in **Url**. Desktop builds also accept an
  absolute local path such as `D:/media/clip.mp4`; it is converted to a `file://` URL.

Runtime API:

```csharp
source.SetClip(clip);
source.SetUrl("https://example.com/clip.mp4");
source.Reload();
```

See [Usage-Guide.md](Documentation/Usage-Guide.md#6-video-input) for details.

### 5. Stream over the internet

1. Start the relay on a public machine:

   ```bash
   cd Tools/RelayServer
   npm install
   FXM_RELAY_TOKEN=change-me npm start
   ```

2. Use **Tools > FXM WebStream > Add Relay Sender (Internet)** and
   **Add Relay Receiver (Internet)**.
3. On both components set the same **Relay Url**, **Room**, and **Token** (for example
   `wss://relay.example.com/fxm-relay`, `demo`, `change-me`). Use `wss://` in production.
   If the relay has no `FXM_RELAY_TOKEN`, leave **Token** empty on both sides.
4. Build or press Play. Both peers connect outbound; no port forwarding is required.
5. While both are running, check `http://127.0.0.1:8787/healthz`. It must report
   `"senders":1` and at least `"receivers":1`. `"tokenRequired":false` means the relay
   has no token, so leave **Token** empty on both sides. On a phone, never use
   `ws://127.0.0.1`; use the PC's Wi-Fi address, for example
   `ws://192.168.2.246:8787/fxm-relay`.

The relay is stateless: it forwards JPEG frames and audio packets from the sender to every
receiver in the room and forwards control messages back. It also has a `player` role for
symmetric gameplay state (used by the walking sim); player packets go to the other players
in the room and are kept separate from video traffic. It does not store media.

## Playback control

| Action | Receiver API | Effect on the sender |
| --- | --- | --- |
| Play | `receiver.Play()` | Resume capture; VideoPlayer.Play |
| Pause | `receiver.Pause()` | Pause capture; the last frame stays visible |
| Stop | `receiver.Stop()` | Stop capture; VideoPlayer.Stop; optionally clear the view |
| Restart | `receiver.Restart()` | VideoPlayer returns to 0 and plays |
| Seek | `receiver.Seek(12.5f)` | VideoPlayer.time = 12.5 seconds |
| Quality | `receiver.SetQuality(60)` | Changes sender `jpegQuality` |
| Target FPS | `receiver.SetTargetFps(24)` | Changes sender `targetFps` |

The sender owns the state and republishes it every second, so a receiver that connects
late or loses a packet stays in sync. The control channel has no authentication; enable
`allowRemoteControl` only on trusted networks or behind a relay token.

## Topologies

| Topology | How |
| --- | --- |
| One-to-many (UDP) | Fill `StreamingSender.udpReceivers` or call `AddUdpReceiver("host:port")` |
| One-to-many (WebSocket) | The sender runs a server; every client receives the broadcast |
| One-to-many (Relay) | The relay broadcasts the sender's frames to every receiver in the room |
| Many-to-one | **Many** in the setup panel, or `MultiStreamReceiver` with several `sources` |
| Many-to-many | Every node sends to its own room, then **Many** the other rooms |
| Shared world | Every streaming node joins the same **Game Room** on the walking sim |

In the setup panel choose **Many**, select Relay or WebSocket, and enter one room
(relay) or URL (WebSocket) per sender. Press **Start streaming** first if this node should
also send its own camera: the sender keeps running while you switch to **Many**. Watching
goes fullscreen automatically; click a cell or press 1-9 to choose the active source, or
use the control bar for **Grid/Single**, audio, and **Fill/Fit**. Audio follows the active
source.

For UDP, each source in `MultiStreamReceiver` needs its own local port because one socket
binds one port. WebSocket and relay sources do not have this limitation, so the panel
offers those two for multi-source watching.

## Platforms

| Platform | UDP | WebSocket | Relay | AsyncGPUReadback | Background encode |
| --- | --- | --- | --- | --- | --- |
| Windows / macOS (standalone) | Yes | Yes (client & server) | Yes | Yes | Yes |
| Android / iOS | Yes | Yes (client; server may work) | Yes | Yes | Yes |
| WebGL | No | Yes (client, via browser) | Yes | No | No (`EncodeToJPG` on the main thread) |

Cross-platform recipes:

- **exe to exe**: relay mode on both, same room. Works across NAT.
- **exe to Android**: desktop sender, Android receiver, relay mode.
- **Laptop to two phones**: one relay sender, the same receiver APK on both phones, same room/token.
- **Two cameras on one phone**: each sender uses its own room; the phone lists both rooms under **Many**.
- **Audio**: enable **Stream audio** on the sender and **Play audio** on the receiver. The setup
  panel offers Voice (16 kHz mono), Balanced (24 kHz mono), and Music (48 kHz stereo).
- **Android to exe**: `WebCamStreamSource` + sender on Android, relay receiver on desktop.
- **LAN only**: UDP or a WebSocket server on the desktop; Android connects to the LAN IP.

## Verification

- **EditMode**: covers JPEG round-trip, row orientation, odd dimensions, async encoder,
  UDP reassembly (loss/duplicate/random order/timeout), control protocol, relay URL
  building, local video path normalization, a live relay transport test, setup-panel
  validation, fullscreen grid layout, audio packet parsing, and many-source validation. The audio/multi changes
  compile in both the runtime and test assemblies; the audio protocol cases were also run
  in a standalone harness. Rerun the full Unity suite after the Editor reloads.
- **PlayMode**: 6/6 passed, including camera -> AsyncGPUReadback -> encoder -> UDP ->
  reassembly -> `LoadImage` -> `RawImage`, Pause/Play/Stop/Play control round-trips, two
  senders to one `MultiStreamReceiver`, and the demo scene building a playable Guy with
  idle/walk clips and a following camera. Gameplay tests drive a simulated W key and mouse
  through the Input System to prove movement, idle/walk blending, and mouse orbit, check
  that remote avatars block the local player, and verify the skybox clear mode plus the
  FXM Canvas being hidden in gameplay and restored in Watch mode.
- **Relay integration (Node)**: 8/8 passed covering fan-out, PCM16 audio forwarding,
  player-state fan-out and isolation, room isolation, token rejection, sender takeover
  after reconnect, and health reporting.
- **Relay harness (C#)**: `dotnet run` in `Tools/RelayHarness` verified frame/control
  routing and automatic reconnect through the real relay using the Unity transport source.
- **WebGL build**: a full IL2CPP WebGL player built with 0 errors (65 MB) from the demo
  scene. The panel hides UDP on WebGL, refuses `ws://` on HTTPS pages, and offers a
  **Server URL** so a browser sender can connect out to a desktop WebSocket server.
- **Device builds**: Android and desktop players were not rebuilt in this environment;
  the runtime compiles for the editor and WebGL branches, and the existing Android APK
  manifest already has `INTERNET` and `CAMERA`. Audio capture/playback and the
  multi-source panel have not been exercised on a device yet; test them with one sender
  and two viewers before distributing a build.

See [Setup-Guide.md](Documentation/Setup-Guide.md) for step-by-step scenes and
[Development-Guide.md](Documentation/Development-Guide.md) for internals.

## Documentation

| Document | Audience |
| --- | --- |
| [Setup-Guide.md](Documentation/Setup-Guide.md) | First scene, video input, relay, Windows/Android builds |
| [Mentor-Quickstart.md](Documentation/Mentor-Quickstart.md) | Run the PC sender and Android receiver at another home |
| [Usage-Guide.md](Documentation/Usage-Guide.md) | Field reference, topologies, tuning, troubleshooting |
| [Development-Guide.md](Documentation/Development-Guide.md) | Architecture, protocols, threading, extension, tests |
