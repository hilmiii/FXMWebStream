# FXM WebStream Usage Guide

Reference for every component, field, topology, and tuning knob. For a first scene, start
with [Setup-Guide.md](Setup-Guide.md). For implementation details, see
[Development-Guide.md](Development-Guide.md).

---

## 1. How it works

```
Camera / VideoPlayer / Webcam
        │
        ▼
RenderTexture (downscaled)
        │  AsyncGPUReadback or ReadPixels
        ▼
RGBA byte[] (pooled)
        │  AsyncJpegEncoder worker thread
        ▼
JPEG byte[] (pooled)      AudioListener -> PCM16 packets
        │                          │
        └──── INetworkTransport.Send ────┐
        ├── UDPTransport (LAN, chunked, low latency)
        ├── WebSocketTransport (client/server, WebGL)
        └── Relay (both peers connect outbound to a public server)
        │
        ▼
Receiver Poll → audio magic? → StreamAudioPlayer
              → JPEG → LoadImage → reused Texture2D → RawImage
```

The control channel uses the same transport with 28-byte `FXC1` messages, so commands are
never stuck behind video frames. Audio packets use a 20-byte `FXA1` header and are
recognized before JPEG decoding; they share the frame queue but not the control queue.

---

## 2. Sender (`StreamingSender`)

Add it to a camera, or to an empty GameObject when using a texture/video/webcam source.

### 2.1 Image source

| Field | Meaning |
| --- | --- |
| `sourceCamera` | Camera to render. Empty falls back to `Camera.main`. |
| `sourceTexture` | Optional RenderTexture/Texture to capture instead of a camera. |
| `videoSourceBehaviour` | Optional `VideoPlayerStreamSource` or `WebCamStreamSource`. |
| `resolutionScale` | Output scale (0.5 = half width and height). |
| `captureMethod` | `AsyncGPUReadback` (default) or `ReadPixels`. |
| `flipVertically` | Flips row order for Unity textures. Leave on for camera capture. |

### 2.2 Encoding

| Field | Meaning |
| --- | --- |
| `targetFps` | Maximum frames sent per second (1-240). |
| `jpegQuality` | JPEG quality 1-100. 60-80 is a good streaming range. |
| `encoderThreads` | 1-4 worker threads. More threads help large resolutions. |

### 2.3 Playback

| Field | Meaning |
| --- | --- |
| `autoPlay` | Start in Playing state. When off, the stream starts Stopped. |
| `allowRemoteControl` | Accept commands from receivers. No authentication. |
| `localControlPort` | Local UDP port for commands (default 47800). |

### 2.4 Network

| Field | Meaning |
| --- | --- |
| `transportKind` | `Udp`, `WebSocket`, or `Relay`. |
| `remoteHost` / `remotePort` | UDP destination, or WebSocket server port. |
| `udpReceivers` | Extra `host:port` targets for UDP one-to-many. |
| `webSocketUrl` | WebSocket client URL, or host/port/path for server mode. |
| `webSocketServer` | Run a WebSocket server (sender side). Ignored for relay. |
| `relayUrl` | Public relay URL, for example `wss://relay.example.com/fxm-relay`. |
| `relayRoom` | Shared room name. One sender per room. |
| `relayToken` | Optional shared secret matching `FXM_RELAY_TOKEN`. |
| `mtu` | UDP datagram size including header. 1200 is internet-safe. |
| `autoStart` | Start automatically on `Start`. |

### 2.5 Audio

| Field | Meaning |
| --- | --- |
| `streamAudio` | Capture the scene mix from the AudioListener and send PCM16 packets. |
| `audioSampleRate` | 16000, 24000, or 48000 samples per second. |
| `audioStereo` | Send two channels instead of mono (double bandwidth). |
| `audioGain` | Capture gain (0-2). Values above 1 can clip. |
| `audioCapture` | Optional capture component; one is added to the AudioListener when empty. |

Audio uses the normal frame path, so it works on UDP, WebSocket, and relay without extra
ports. The sender captures the final mix, including VideoPlayer clips routed through an
AudioSource, webcam audio, and scene AudioSources. Audio capture is unavailable on WebGL.
Bandwidth is 256 kbps for 16 kHz mono, 384 kbps for 24 kHz mono, and 1536 kbps for
48 kHz stereo. Bandwidth is paid once by the sender, not once per viewer.

Capture taps the final AudioListener mix. A node that both sends and plays received audio
will therefore capture the playback again (an echo loop over the relay). For many-to-many,
enable audio on one sender at a time, or disable **Play audio** on nodes that send audio.
For a same-process loopback test, leave audio off.

### 2.6 Diagnostics

`showStatistics` draws an overlay with resolution, capture/encode/send FPS, JPEG size,
encode time, dropped frames, and peer count.

---

## 3. Receiver (`StreamingReceiver`)

Add it to an empty GameObject with a `RawImage` on the same object, or assign one.

### 3.1 Network

| Field | Meaning |
| --- | --- |
| `transportKind` | `Udp`, `WebSocket`, or `Relay`. |
| `listenPort` | UDP port to bind. |
| `senderHost` | Sender address for UDP commands. |
| `senderControlPort` | Sender `localControlPort`. |
| `webSocketUrl` | WebSocket server URL, or relay URL in relay mode. |
| `webSocketServer` | Run the receiver as a WebSocket server (rare). |
| `relayUrl` / `relayRoom` / `relayToken` | Relay connection, same values as the sender. |
| `mtu` | Must be equal to or larger than the sender MTU. |
| `autoStart` | Start automatically. |
| `allowRemoteCommands` | Send play/pause/stop commands back to the sender. |
| `sourceIdFilter` | Accept state only from this sender id (0 = all). |

### 3.2 Rendering and performance

| Field | Meaning |
| --- | --- |
| `targetRawImage` | Destination image. Empty uses the RawImage on this object. |
| `preserveAspectRatio` | Letterbox instead of stretching. |
| `markTextureNonReadable` | Release the CPU copy after decode (saves memory). |
| `clearOnStop` | Clear the view when the sender reports Stopped. |
| `maxDecodeFps` | Decode rate limit; frames above it are skipped. |
| `playAudio` | Play the audio packets sent by the sender. |
| `audioVolume` | Playback volume for this receiver (0-1). |

### 3.3 Diagnostics

`showStatistics` shows source id, resolution, receive/decode FPS, decode time, frame size,
decoded/skipped/error counts, and peer count.

---

## 4. Multi-source receiver (`MultiStreamReceiver`)

Use one `StreamSourceConfig` per source.

| Config field | Meaning |
| --- | --- |
| `name` | Display name. |
| `transportKind` | `Udp`, `WebSocket`, or `Relay`. |
| `host` / `localPort` / `senderControlPort` | UDP addresses and ports. |
| `webSocketUrl` / `webSocketServer` | WebSocket mode. |
| `relayUrl` / `relayRoom` / `relayToken` | Relay mode for this source. |
| `mtu` | UDP datagram size. |

Component fields:

| Field | Meaning |
| --- | --- |
| `sources` | Source configurations. |
| `views` | One RawImage per source, in the same order. |
| `showAllSources` | True = grid (many-to-many); false = active source only. |
| `activeSourceIndex` | Visible source when `showAllSources` is false. |
| `maxDecodeFps` | Decode limit for visible sources. |
| `backgroundDecodeFps` | Lower limit for hidden sources. |
| `preserveAspectRatio` | Letterbox each view. |
| `clearOnStop` | Clear a view when its source stops. |
| `autoStart` | Start automatically. |
| `playAudio` | Create one audio player per source. |
| `audioFollowsActiveSource` | Only the active source is audible (otherwise all sources are mixed). |
| `audioVolume` | Volume applied to every source. |

UDP requires a unique local port per source. WebSocket and relay sources use one
connection each and do not need unique ports.

Runtime API:

```csharp
multi.SetActiveSource(1);
multi.Play();
multi.Pause();
multi.Stop();
multi.Restart();
multi.Seek(30f);
Texture2D frame = multi.GetSourceTexture(0);
```

---

## 5. Playback controls (`StreamPlaybackControls`)

Assign any `IStreamPlaybackController`: sender, receiver, or multi receiver. Buttons and
the slider are optional; only assigned fields are used.

| Field | Meaning |
| --- | --- |
| `controllerBehaviour` | Target component. |
| `playButton` / `pauseButton` / `stopButton` / `restartButton` | Optional UI buttons. |
| `seekSlider` | Optional slider normalized to 0..1. |
| `statusLabel` / `timeLabel` | Optional text labels. |
| `hideSeekWhenUnsupported` | Hide the slider when seeking is unavailable. |

Status text is `Streaming`, `Paused`, or `Stopped`. Time text is `mm:ss / mm:ss` for video
or `mm:ss / LIVE` for live sources.

---

## 6. Video input

### 6.1 Sources

| Source | Component | Seek | Notes |
| --- | --- | --- | --- |
| Video clip asset | `VideoPlayerStreamSource.clip` | Yes | Bundled in builds; best for mobile. |
| URL | `VideoPlayerStreamSource.url` | Usually | HTTP(S), HLS, or a local `file://` path. |
| Webcam | `WebCamStreamSource` | No | Android/iOS/desktop camera. |
| RenderTexture | `StreamingSender.sourceTexture` | Depends | Any custom render pipeline. |

### 6.2 VideoPlayer fields

| Field | Meaning |
| --- | --- |
| `clip` | Local VideoClip asset. Takes priority over `url`. |
| `url` | Video URL or local path. |
| `width` / `height` | Video render resolution before sender downscaling. |
| `targetTexture` | Optional custom RenderTexture. |
| `playOnStart` | Start playback on scene start. |
| `loop` | Repeat the clip. |
| `startTimeSeconds` | Start position after preparation. |

### 6.3 Runtime API

```csharp
source.SetClip(clip);
source.SetUrl("https://example.com/clip.mp4");
source.Play();
source.Pause();
source.Stop();
source.Restart();
source.Seek(12.5f);
source.Reload();
```

Local paths are normalized: `D:\media\clip.mp4` becomes `file:///D:/media/clip.mp4`.
Android cannot read a desktop absolute path; use a VideoClip or an HTTP(S) URL.

### 6.4 Webcam fields

| Field | Meaning |
| --- | --- |
| `deviceName` | Camera device name; empty uses the first device. |
| `requestedWidth` / `requestedHeight` | Capture size and source texture size. |
| `requestedFps` | Requested camera frame rate. |
| `mirrorHorizontally` | Mirror the image (front-facing camera). |
| `flipVertically` | Flip the image when the received camera view is upside down. |
| `playOnStart` | Open the camera on start. |
| `autoPlay` | Report Playing once the camera opens. |

The webcam image is copied into a fixed-size RenderTexture each frame, so the sender can
prepare before the camera is ready. The first use triggers the camera permission prompt.

---

## 7. Internet relay

### 7.1 Why a relay

On a LAN, UDP or a direct WebSocket server is fastest. On the internet:

- UDP requires a public IP and inbound port forwarding; mobile carriers usually block it.
- A WebSocket server works only if the sender is reachable inbound.
- A relay lets **both peers connect outbound** to one public endpoint. It works across
  NAT, carrier networks, and browsers.

### 7.2 Room model

- One room has one sender and up to `FXM_MAX_RECEIVERS` receivers.
- Frames go sender -> every receiver.
- Control messages go receiver -> sender (and sender state -> every receiver).
- A newer sender with the same room/token replaces the old one (reconnect takeover).
- Rooms are isolated; a receiver in another room receives nothing.
- A room can also hold `role=player` peers. Player packets are forwarded to the other
  players in that room and are never echoed back or sent to the video sender/receivers.
  This is the channel used by the walking sim's shared world.

### 7.3 URL format

The components build:

```
wss://relay.example.com/fxm-relay?room=demo&role=sender&token=change-me
```

`room` matches `[A-Za-z0-9_-]{1,64}`. `role` is `sender`, `receiver`, or `player`. `token`
is optional but strongly recommended in production. The token is not written to logs.

### 7.4 Deployment checklist

- TLS (`wss://`) for WebGL and mobile production.
- `FXM_RELAY_TOKEN` set to a long random value.
- `FXM_ALLOWED_ORIGINS` set when serving WebGL from known domains.
- Relay close to the sender for upstream latency; receivers add egress bandwidth.
- Health endpoint `/healthz` monitored.
- No sticky sessions required; the relay is stateless.

### 7.5 Bandwidth

The relay forwards one copy per receiver. Estimate:

```
bandwidth per receiver ≈ JPEG KB/frame × FPS × 8 / 1000 Mbps
```

At 720p, quality 65, 30 FPS, a frame is often 60-100 KB, so one receiver uses roughly
14-24 Mbps. Three receivers need roughly 42-72 Mbps of relay egress. Lower resolution,
quality, or FPS first.

---

## 8. Topologies and ports

### 8.1 One-to-many UDP

| Component | Field | Value |
| --- | --- | --- |
| Sender | `remoteHost` / `remotePort` | First receiver IP / 47777 |
| Sender | `udpReceivers` | `192.168.1.20:47777`, `192.168.1.21:47777` |
| Sender | `localControlPort` | 47800 |
| Receiver A | `listenPort` | 47777 |
| Receiver B | `listenPort` | 47778 |
| Receiver A/B | `senderHost` / `senderControlPort` | Sender IP / 47800 |

### 8.2 One-to-many WebSocket

| Component | Field | Value |
| --- | --- | --- |
| Sender | `webSocketServer` | true |
| Sender | `webSocketUrl` | `ws://0.0.0.0:47778/fxm-stream/` |
| Receiver | `webSocketServer` | false |
| Receiver | `webSocketUrl` | `ws://SENDER_IP:47778/fxm-stream/` |

The server broadcasts every frame to all connected clients. WebGL receivers must use this
mode or the relay.

### 8.3 Many-to-one (setup panel)

1. Give each sender its own relay room (`cam-a`, `cam-b`, ...) or WebSocket URL.
2. On the receiver open **Many**, choose Relay (or WebSocket), and enter the rooms or
   URLs under **Sources**.
3. Press the **Watch N sources** button. The panel hides automatically and the grid fills
   the screen edge to edge. Tap or move the mouse for the control bar; click a cell or
   press 1-9 to switch the active source, and audio follows it. **Fit** (the default)
   letterboxes without zooming, **Fill** crops each cell, and **Setup** returns to this
   panel.

Behind the panel this creates a `MultiStreamReceiver` with one source per room/URL and
`showAllSources = true`. Turn the grid off to show only the active source.

For a scene without the panel, configure N `StreamSourceConfig` entries on a
`MultiStreamReceiver`, each pointing at a different UDP port or relay room, then call
`SetActiveSource(i)`.

### 8.4 Many-to-many (setup panel)

1. On every node press **Start streaming** with its own room (`alice`, `bob`, ...).
   The sender keeps running in the background.
2. Switch to **Many** without stopping the sender, and list the other nodes' rooms
   (or WebSocket URLs).
3. Press the **Watch N sources** button. Each node now sends one stream and shows every
   other node in a fullscreen grid. The control bar switches **Grid/Single**, mutes audio,
   toggles **Fill/Fit**, and stops the session.

One sender per room is a relay rule: two senders in the same room replace each other. For
UDP, each local source needs a distinct port. Audio follows the selected grid cell; set
`audioFollowsActiveSource = false` on `MultiStreamReceiver` to mix all sources instead.

### 8.5 Shared walking-sim world

The demo scene uses `WalkingSimBootstrap`, `WalkingSimPlayer`, `WalkingSimNetwork`, and
`GuyAnimator` from `Runtime/WalkingSim`. The bootstrap instantiates the city, ground, Guy,
and camera from `Assets/Prefabs`; nothing in that folder needs to be assigned by hand.

| Field | Default | Purpose |
| --- | --- | --- |
| `relayUrl` | `ws://127.0.0.1:8787/fxm-relay` | Relay used for the player room. Use the LAN/WAN address on phones. |
| `gameRoom` | `demo` | Shared world room. Set the same value on every player. |
| `relayToken` | empty | Optional relay token. |
| `spawnPoint` | `(-61.17, 16.9, -76.66)` | Start position in the city. |
| `groundHeight` | `16.7` | Ground collider height, just below the city roads. |
| `addCityColliders` | true | Adds mesh colliders to buildings and props (358 in this city). |
| `walkSpeed` | `3.2` | Metres per second. |
| `cameraDistance` | `4` | Third-person camera distance. |
| `lookSensitivity` | `0.11` | Mouse look speed. |
| `autoHideSetupPanel` | true | Hides the panel when streaming starts. |

In **Stream** mode the bootstrap also hides the **FXM Canvas** uGUI overlay (playback
controls and the empty relay view), so the world and skybox are visible; the canvas is
restored in **Watch** and **Many**. Input is accepted only while the setup panel is hidden,
so typing in the panel never moves
the character. Desktop uses **WASD**/arrows and free mouse look; Android uses a left
analogue stick and a look swipe anywhere outside the stick. The stick is IMGUI so it works on every input
backend and stays on top of the sender preview. To test it in the Editor, enable
`forceTouchControls` on the player; it always shows on phones.

The network starts only when the panel is in **Stream** mode and streaming is running. It
connects with `role=player`, sends a 32-byte state packet (`FXG1`) at 10 Hz, and creates a
Guy avatar for every other player. Remote avatars are interpolated at 14 Hz and use solid
`CapsuleCollider`s, so local `CharacterController` movement stops against them. A remote
player is removed after five seconds without a packet. The `WalkingSimHud` shows the room
name, relay state, and player count.

To share a world in many-to-one mode, give each sender its own video room but set the same
`gameRoom` on all of them. The receiver can keep watching a grid of video rooms; the
players still collide in the shared world. To disambiguate a room from the video room,
use a value such as `world` for `gameRoom`.

---

## 9. Performance and tuning

### 9.1 Presets

| Use case | Resolution scale | FPS | Quality | Notes |
| --- | --- | --- | --- | --- |
| LAN preview | 1.0 | 30 | 75 | Best quality; high bandwidth. |
| Internet 720p | 0.6-0.75 | 24-30 | 60-70 | Good balance. |
| Mobile / weak CPU | 0.5 | 20-24 | 55-65 | Lower heat and bandwidth. |
| WebGL | 0.4-0.5 | 15-20 | 50-60 | Main-thread encode; keep it light. |
| Remote desktop | 1.0 | 30 | 80 | Text stays readable; bandwidth is high. |

### 9.2 Encoder cost

Approximate single-thread results on a desktop CPU:

| Input | Quality | Encode time | Throughput |
| --- | --- | --- | --- |
| 1280x720 | 70 | ~65 ms | ~15 fps |
| 1280x720 | 90 | ~61 ms | ~16 fps |
| 1920x1080 | 70 | ~134 ms | ~7.5 fps |

Use more `encoderThreads` for 1080p, or lower the resolution. The encoder never blocks the
main thread on desktop/mobile.

### 9.3 Latency

The pipeline holds at most a few frames: two in-flight readbacks, two encode jobs, and a
small send queue. If the network slows down, old frames are dropped instead of queued, so
latency stays bounded at the cost of frame rate. Relay round-trip time is added on top.

### 9.4 Memory

Reused textures and pooled buffers avoid per-frame GC. A 1080p RGBA frame is 8 MB, so
avoid `resolutionScale = 1` on low-memory mobile devices.

---

## 10. Troubleshooting

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| Black receiver | No frames or decode failure | Check sender FPS and receiver decoded count. |
| Black video | Clip/URL not assigned | Assign a source; check VideoPlayer errors. |
| Webcam black | Permission denied or device busy | Allow camera permission; close other apps. |
| Relay `401` | Token mismatch | Use the same token on both peers and the server. |
| Relay `400` | Invalid room/role | Room `[A-Za-z0-9_-]{1,64}`, role sender/receiver. |
| Relay `404` | Wrong path | Use `/fxm-relay` or the configured `FXM_RELAY_PATH`. |
| Receiver connects but no image | Different room, or no sender | Compare room names; check `/healthz`. |
| Controls do nothing | Remote control disabled or wrong control port | Enable both toggles; match UDP control ports. |
| Works on LAN, not internet | UDP inbound blocked | Use relay or port forwarding. |
| WebGL cannot connect | `ws://` on an HTTPS page | Use `wss://`. |
| Android cannot connect | Internet Access not required | Set Internet Access = Require; use WSS. |
| Stutter every second | GC or relay backpressure | Lower quality/FPS; check relay CPU/bandwidth. |
| Colors shifted | Row order or color space | Toggle `flipVertically`; keep sRGB textures. |
| Image stretched | Aspect ratio | Enable `preserveAspectRatio` and use a full-screen RawImage. |

---

## 11. Limits and FAQ

**Is audio streamed?** Yes, as optional PCM16 on the same transport. Enable `streamAudio`
on the sender and `playAudio` on the receiver. Game audio goes through the AudioListener
mix; VideoPlayer clips are routed to an AudioSource automatically. Audio is not available
on WebGL and is not compressed, so use 16-24 kHz mono on slow links. Because capture uses
the final mix, avoid playing received audio on a node that also sends audio (feedback).

**Can a browser send?** Yes, with the relay or a WebSocket server, but WebGL encoding is
slower and has no UDP.

**Can I discover peers automatically?** No. Fill addresses/rooms manually.

**Is the relay secure?** It supports a shared token and TLS via a reverse proxy. It does
not encrypt media itself; use `wss://` for that.

**Can one room have multiple senders?** No. One sender per room; use multiple rooms and a
`MultiStreamReceiver` for many-to-many.

**Does the relay store video?** No. It forwards binary messages and keeps no media.

**Can I use a custom transport?** Yes. Implement `INetworkTransport` and add it to
`TransportFactory`.
