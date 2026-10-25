# FXM WebStream Development Guide

Internal architecture, protocols, threading, extension points, and the test workflow.

---

## 1. Design principles

1. **No native plugins.** JPEG is encoded in pure C# and decoded with Unity's
   `ImageConversion.LoadImage`, so the same code runs on desktop, mobile, and WebGL.
2. **Main thread stays free.** Capture uses `AsyncGPUReadback`; encoding runs on worker
   threads; sockets run on their own threads. Only decode and texture upload remain on the
   main thread because Unity requires it.
3. **Bounded queues.** Old frames are dropped rather than queued, keeping latency stable
   when the network or CPU falls behind.
4. **Pooled memory.** `FrameBufferPool` and reused textures prevent per-frame GC spikes.
5. **Transport-agnostic core.** `StreamingSender` and the receiver core depend on
   `INetworkTransport`, not on sockets.
6. **One sender per relay room.** Rooms isolate streams; multiple senders use multiple
   rooms.

---

## 2. Layout and assemblies

```
Runtime/            FXM.WebStream.asmdef (references UnityEngine.UI)
Editor/             Assembly-CSharp-Editor (no asmdef, editor only)
Tests/Editor/       FXM.WebStream.EditorTests.asmdef (Editor, NUnit)
Tests/PlayMode/     FXM.WebStream.PlayTests.asmdef (all platforms, NUnit)
Tools/RelayServer/  Node.js relay, independent of Unity
```

`Runtime` has no Editor references. `Editor/StreamSceneWizard.cs` uses `UnityEditor` and
must stay outside the runtime assembly.

---

## 3. Architecture

```
StreamingSender
 ├── source: Camera / RenderTexture / IStreamVideoSource
 ├── capture: AsyncGPUReadback | ReadPixels | WebGL EncodeToJPG
 ├── AsyncJpegEncoder (N workers)
 ├── INetworkTransport (UDP | WebSocket | relay)
 └── control: StreamControlProtocol + heartbeat

Receiver
 ├── INetworkTransport.Poll
 ├── StreamReceiverCore
 │    ├── LoadImage into one Texture2D
 │    └── control messages
 ├── RawImage + letterbox
 └── StreamPlaybackControls
```

| Class | Responsibility |
| --- | --- |
| `StreamingSender` | Capture, encode submission, send, state ownership, command handling. |
| `StreamingReceiver` | One transport + one core + one RawImage + UI commands. |
| `MultiStreamReceiver` | N transports/cores, grid or active source, commands to all. |
| `StreamReceiverCore` | Decode and texture reuse; no UI. |
| `VideoPlayerStreamSource` | VideoPlayer -> RenderTexture and playback API. |
| `WebCamStreamSource` | WebCamTexture -> fixed RenderTexture and playback API. |
| `StreamPlaybackControls` | Optional uGUI wiring through `IStreamPlaybackController`. |
| `StreamSetupPanel` | Dark-themed responsive IMGUI start screen plus the edge-to-edge fullscreen viewer (grid, fill/fit, auto-hiding controls, immersive Android). |
| `StreamSceneWizard` | Editor menus for loopback, video, webcam, relay, topologies. |

---

## 4. Frame lifecycle

1. `Update` checks `targetFps` and requests a capture.
2. `PrepareCapture` computes the capture size from the source, makes it even for 4:2:0,
   and creates/reuses the RenderTexture.
3. Capture:
   - camera: the camera renders directly into the small RenderTexture,
   - texture/video/webcam: `Graphics.Blit` into the capture texture,
   - WebGL: `ReadPixels` + `EncodeToJPG`.
4. Desktop/mobile: `AsyncGPUReadback` copies RGBA into a pooled buffer and submits it to
   the encoder. At most two readbacks are in flight.
5. A worker encodes JPEG and pushes an `EncodedFrame` into a bounded result queue.
6. `Update` dequeues results and calls `transport.Send`.
7. The receiver's `Poll` moves whole frames to the main thread, `LoadImage` decodes into a
   reused texture, and `RawImage` displays it.

WebGL has no worker threads or `AsyncGPUReadback`; its path is synchronous on the main
thread, so lower resolution/FPS.

---

## 5. Threading

| Thread | Work | Shared state |
| --- | --- | --- |
| Main | Capture requests, queue dequeue, transport `Poll`, decode/UI | Unity objects only on this thread |
| Encoder workers | JPEG encode | Pooled buffers, queues with locks |
| UDP send | Chunking and `SendTo` | Send queue, target list |
| UDP receive | `ReceiveFrom`, reassembly | Receive queue |
| WebSocket client | Connect, receive loop | `ClientWebSocket`, incoming queue |
| WebSocket server | Accept loop + per-peer receive threads | Peer list with locks |

Rules:

- Never touch Unity objects from a worker or socket thread.
- `FrameReceived` and `ControlReceived` fire only inside `Poll` on the main thread.
- `INetworkTransport.Send` copies the payload; the caller may reuse its buffer.
- `EncodedFrame.Buffer` returns to the pool via `ReleaseFrame` after sending.

---

## 6. JPEG encoder

`JpegEncoder` is a baseline sequential encoder:

- 8-bit YCbCr, 4:2:0 subsampling.
- Standard Annex K quantization and Huffman tables.
- Fast AAN float DCT, two passes.
- Quality 1..100 with libjpeg scaling.
- Edge replication for dimensions not divisible by 16.
- Optional vertical flip for Unity row order.
- Scratch buffers per instance; one instance per worker.

`AsyncJpegEncoder` owns the workers:

- Job queue bounded (default 2); the oldest job is dropped when full.
- Result queue bounded (default 4); the oldest result is dropped and its buffer returned.
- `Submit` takes ownership of the RGBA buffer.
- `TryDequeue` returns an `EncodedFrame`; `ReleaseFrame` returns its buffer.

The encoder output is verified against Unity's native `LoadImage` in EditMode tests,
including orientation and odd dimensions.

---

## 7. Transport contract

```csharp
public interface INetworkTransport : IDisposable
{
    bool IsRunning { get; }
    int PeerCount { get; }
    TransportStatistics Statistics { get; }

    event Action<ArraySegment<byte>> FrameReceived;
    event Action<ArraySegment<byte>> ControlReceived;
    event Action<string> Log;

    bool Start(TransportConfig config);
    void Send(ArraySegment<byte> frame);
    void SendControl(ArraySegment<byte> message);
    void Poll();
    void Stop();
}
```

`TransportConfig` includes destination/local ports, MTU, queues, WebSocket server/path,
query string, and `AutoReconnect`. `TransportFactory.Create` maps `Udp` to `UDPTransport`
and both `WebSocket` and `Relay` to `WebSocketTransport`.

---

## 8. UDP wire format

```
offset 0  : magic 'F','X','S','1'
offset 4  : version (1)
offset 5  : flags
offset 6  : headerSize (24)
offset 8  : frameId (uint)
offset 12 : totalLength (uint)
offset 16 : chunkIndex (ushort)
offset 18 : chunkCount (ushort)
offset 20 : payloadLength (ushort)
offset 22 : chunkPayloadSize (ushort)
offset 24 : payload
```

Reassembly handles loss (discard the frame), duplicates, random order, stale frames, and
timeouts. A sender can fan out to several targets by repeating each datagram. Control
messages use magic `FXC1` and bypass the frame queue.

UDP requires inbound reachability. For the internet, prefer the relay.

---

## 9. WebSocket transport

- **Client:** `ClientWebSocket`, optional reconnect with exponential backoff (500 ms to
  10 s), keepalive every 10 s. One binary message = one JPEG frame.
- **Server:** `TcpListener` + manual RFC 6455 handshake/framing. This avoids HttpListener
  urlacl/admin requirements on Windows.
- **WebGL:** `FXMWebSocket.jslib` wraps the browser WebSocket; C# polls messages.
- **Relay:** the same client transport, with the relay URL and a query string. It works on
  WebGL because the browser handles `wss://`.

The client logs host/port/path but never the token.

### Relay protocol

```
ws(s)://relay/fxm-relay?room=<room>&role=sender|receiver&token=<secret>
```

- Binary messages only.
- Sender -> every receiver in the room.
- Receiver -> sender in the room.
- Control messages (`FXC1`) use the same routing but bypass the relay backpressure drop.
- One sender per room; a new sender closes the old one with code 4001.
- Invalid token -> HTTP 401; invalid room/role -> 400; wrong path -> 404.
- `/healthz` returns room, sender, and receiver counts.

The Node relay (`Tools/RelayServer/server.js`) keeps no media, validates room names, uses
timing-safe token comparison, enforces a per-room receiver limit, and pings clients to
drop dead connections.

---

## 10. Control protocol and state

28-byte little-endian message:

```
0  magic 'F','X','C','1'
4  version (1)
5  kind (1 = command, 2 = state)
6  command (play/pause/stop/restart/seek/setQuality/setTargetFps)
7  state (stopped/playing/paused)
8  sourceId (uint)
12 sequence (uint)
16 value (int)
20 positionMs (int)
24 durationMs (int, 0 = live)
```

- The sender owns the state.
- State is broadcast on change and every second as a heartbeat.
- Receivers ignore stale sequence numbers and reset sequence tracking when the source id
  changes.
- Commands are idempotent and best-effort; the next heartbeat corrects the UI.
- `sourceIdFilter` lets a receiver ignore other senders' state.

State machine:

```
Stopped --Play--> Playing --Pause--> Paused --Play--> Playing
   ^                  |                  |
   +-------Stop-------+------------------+
```

`Restart` returns video sources to 0 and enters Playing; live sources resume.

---

## 11. Receiver core and multi-source

`StreamReceiverCore`:

- Creates a small `Texture2D`; `LoadImage` resizes it to the JPEG dimensions.
- Reuses that texture every frame.
- Optionally makes it non-readable to release the CPU copy.
- Throttles decode with `MaxDecodeFps`.
- Forwards control messages to its owner.

`MultiStreamReceiver` creates one transport + core per source and tracks per-source state,
source id, sequence, position, and duration. It applies `maxDecodeFps` to the active source
and `backgroundDecodeFps` to hidden ones when `showAllSources` is false.

---

## 12. Video and webcam sources

`VideoPlayerStreamSource`:

- Creates its RenderTexture in `Awake` so the sender can read it during `Start`.
- Applies clip/url through `ApplySource`, including at runtime via `SetClip`/`SetUrl`.
- Normalizes local paths to `file://` URLs.
- Forwards play/pause/stop/restart/seek to the VideoPlayer.
- Reports duration only after preparation.

`WebCamStreamSource`:

- Creates a fixed-size RenderTexture in `Awake`, so the sender can start before the camera.
- Requests camera authorization asynchronously.
- Copies `WebCamTexture` into the RenderTexture when a new frame arrives.
- Reports `CanSeek = false` and duration 0.
- Releases the camera and texture in `OnDestroy`.

---

## 13. Performance and memory

- Capture textures: one per sender, sized to the downscaled resolution.
- Readback: at most two in-flight requests.
- Encoder: input RGBA + output JPEG buffers per worker, pooled.
- Receiver: one Texture2D per source, resized by `LoadImage`.
- UDP: chunk buffers from a pool; reassembly buffer per in-flight frame.
- WebSocket: message buffers from a pool; server peers each have a send lock.
- Relay: forwards references to received messages; no decoding.

At 1080p, one RGBA frame is about 8 MB. Lower `resolutionScale` before adding threads.

---

## 14. Extending

### New transport

1. Implement `INetworkTransport`.
2. Add a `TransportKind` value and map it in `TransportFactory`.
3. Copy payloads in `Send` (the caller reuses buffers).
4. Raise `FrameReceived`/`ControlReceived` only from `Poll`.
5. Add a config helper and tests.

### New command

1. Add a `StreamCommandType`.
2. Handle it in `StreamingSender.HandleCommand`.
3. Add a sender/receiver wrapper method.
4. Extend the control tests.

### New video source

1. Implement `IStreamVideoSource`.
2. Return a stable `SourceTexture` with a fixed size before playback starts.
3. Report `CanSeek`, position, and duration correctly.
4. Raise `StateChanged` when playback ends.

### Relay changes

- Keep the query contract (`room`, `role`, `token`).
- Keep binary routing directional; never echo a sender's frame back to itself.
- Add integration tests in `Tools/RelayServer/test/relay.test.js`.

---

## 15. Testing

### Unity EditMode

```bash
Unity -batchmode -nographics -projectPath . \
  -runTests -testPlatform EditMode \
  -testResults Temp/editmode.xml -logFile Temp/editmode.log -quit
```

Covers JPEG round-trip/orientation/odd sizes, async encoder, UDP reassembly, control
protocol, relay URL building, and local path normalization.

### Unity PlayMode

```bash
Unity -batchmode -projectPath . \
  -runTests -testPlatform PlayMode \
  -testResults Temp/playmode.xml -logFile Temp/playmode.log -quit
```

Covers end-to-end camera streaming, playback control round-trips, and two senders into one
`MultiStreamReceiver`. PlayMode needs a graphics device, so do not use `-nographics`.

### Relay

```bash
cd Tools/RelayServer
npm install
npm test
```

Covers fan-out, room isolation, token rejection, sender takeover, and health reporting.

For the Unity transport path without opening the editor:

```bash
cd Tools/RelayHarness
dotnet run -c Release
```

This compiles the real transport sources, starts the Node relay, sends a frame and a
control message, restarts the relay, and verifies automatic reconnect.

### Current known gaps

- WebGL is verified by compilation, not by a browser run.
- VideoPlayer is verified by code/state logic, not by an automated clip playback test.
- `WebCamStreamSource` is compile-verified; no physical camera was opened in the test run.
- Windows/Android players are documented but no device build was produced in this run.
- Physical Android/iOS devices and macOS builds are not tested here.
- The relay is tested on loopback by Node and by the Unity transport test; a public
  deployment is environment-specific.

---

## 16. Conventions and roadmap

- C# identifiers, inspector text, logs, comments, and docs are English.
- Public API uses PascalCase; serialized fields use camelCase.
- Runtime code must not reference `UnityEditor`.
- Prefer pooling and reuse over per-frame allocation.
- Keep control messages fixed-size and allocation-free on the hot path.

Audio path:

- `StreamAudioCapture` (on the AudioListener) downmixes and resamples the game mix into
  PCM16 packets on the audio thread. `StreamingSender` prefixes each packet with the
  20-byte `FXA1` `StreamAudioProtocol` header and sends it through the normal frame path.
- `StreamReceiverCore` recognizes `FXA1` before JPEG decoding and raises
  `AudioReceived`; `StreamAudioPlayer` buffers the PCM and feeds a looping streaming
  `AudioClip`. `MultiStreamReceiver` creates one player per source and mutes all but the
  active source. There is no compressed codec and no A/V clock synchronization yet.

Roadmap ideas:

- Opus/AAC compression and A/V drift correction.
- Peer discovery / signaling.
- Relay authentication beyond a shared token (per-room keys, expiry).
- WebGL browser automation test.
- Optional H.264 path for platforms with hardware codecs.
