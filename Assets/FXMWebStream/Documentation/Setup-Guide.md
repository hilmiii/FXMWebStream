# FXM WebStream Setup Guide

This guide walks from an empty scene to a working stream, then to internet streaming and
Windows/Android builds. For every inspector field, see [Usage-Guide.md](Usage-Guide.md).
For internals, see [Development-Guide.md](Development-Guide.md).

---

## 0. Requirements

- Unity 6 (this project uses **6000.5.8f1** with URP).
- The package folder `Assets/FXMWebStream` inside the project.
- `com.unity.modules.imageconversion` enabled (Unity default) for `LoadImage`.
- Node.js 18+ only if you want to run the internet relay.
- Editor testing needs no build; Android testing needs the Android Build Support module.

> Important: `StreamingSender` assigns its RenderTexture to `Camera.targetTexture`. The
> streamed camera therefore no longer draws to the Game view; the image is visible in the
> receiver's `RawImage`. This is expected. Video and webcam sources do not use a camera.

---

## 0.5 Start screen (recommended)

The **Setup UI** is a start screen: nothing is streamed until the user configures the
connection and presses **Start streaming** or **Watch stream**.

1. Open the scene that has a sender and/or receiver.
2. Choose **Tools > FXM WebStream > Add Setup UI (Start / Watch)**.
3. Press **Play**.

The panel offers:

| Field | Meaning |
| --- | --- |
| Stream / Watch / Many | Send this scene, watch one sender, or watch several senders in a grid. |
| Relay / WebSocket / UDP | Internet relay, direct WebSocket, or LAN UDP (Many uses Relay or WebSocket). |
| Relay URL, Room, Token | Used when Relay is selected. Both sides must match. |
| Scene camera | Streams the scene camera, including the walking-sim character in the demo scene. |
| Video clip | Pick one of the clips assigned to the panel, or choose an mp4/webm/mov/m4v/ogv file from the built-in local-file browser. |
| Video URL | `https://` URL or a local path such as `D:/media/clip.mp4`. |
| Webcam | First available camera; Android asks for permission on start. |
| Resolution / FPS / JPEG | Sender quality settings. |
| Stream audio | Send the scene audio mix: off, Voice 16 kHz mono, Balanced 24 kHz mono, or Music 48 kHz stereo. |
| Play audio | Receiver switch (Watch and Many). |
| Sources | In Many: one relay room or WebSocket URL per sender. |
| Decode FPS | Receiver limit, useful on mobile. |
| Exit | Stops the stream and quits the app. Press twice to confirm; in the Editor it stops Play mode. |

The preview sits beside the settings on wide screens and above them on phones. It shows
the sender capture (including the walking-sim character) while streaming, or the decoded
receiver image while watching. Stop with **Stop and edit** to change the configuration.

The panel is responsive and uses a dark theme only: panel, fields, tabs, toggles, sliders,
and scrollbars stay dark on every platform instead of following the Editor or system skin.
When the panel is open it paints the whole screen black, so the scene camera and an empty
Canvas never show through behind the settings. It uses a two-column layout when there is
room, fills the screen on phones and small tablets, respects `Screen.safeArea` around
notches and gesture bars, and keeps the status line plus **Start/Watch** and **Exit** in a
fixed footer. With
`autoRotateOnMobile` enabled (default), the app follows the device orientation. Receiver
views use an `AspectRatioFitter` so a landscape video is letterboxed in portrait instead
of being stretched.

### Fullscreen viewer

Press **F1** (configurable) to hide the panel, or press **Hide**. In **Watch** and **Many**
the panel hides automatically when the stream starts (`fullscreenOnStart`). In **Stream**
mode the demo's `WalkingSimBootstrap` hides it automatically when streaming starts, so the
character is immediately visible; press **Setup** or **F1** to reopen it. The video then
fills the whole screen against a black background. **Fit** is the default: the video keeps
its aspect ratio and is never zoomed or cropped, adding black bars only when needed.
Press **Fill** to crop the edges instead (useful when the video aspect already matches the
screen).

While **Stream** is idle, hiding the panel shows the live game view (with a small
**Setup** bar) instead of a black waiting screen. After streaming starts, the camera
renders into the sender texture, so the same area shows the sender preview.

The control bar fades after `fullscreenControlsTimeout` (3.5 s). Tap the screen or move
the mouse to bring it back. It offers **Setup**, **Grid/Single** (Many), **Audio/Muted**,
**Fill/Fit**, and **Stop**. In the Many grid, tap a cell or press 1-9 to select the active
source. The cursor hides while the controls are faded. On Android the status and
navigation bars are hidden while the viewer is active, and the bar respects the safe
area.

Settings are remembered between runs in `PlayerPrefs`; the relay token is never saved.
The **Exit** button asks for a second press, then stops the stream and closes the
application (Play mode is stopped in the Editor; WebGL asks you to close the tab instead).

> The Game view can look empty while a camera is being streamed because the camera renders
> into the sender's RenderTexture instead of the screen. Hide the panel to see the preview
> full screen, or look at the receiver. Stop the stream to restore the camera.

---

## 0.6 Walking sim (demo scene)

The `FXMWebStream` scene starts a third-person walking demo instead of the rotating cube.
The **Walking Sim** object builds the LowPolyCity prefab, a ground collider, the Guy
character, the third-person camera, the HUD, and the relay player room at runtime.

| Control | Desktop | Android |
| --- | --- | --- |
| Move | **WASD** / arrow keys | Left analogue stick |
| Look | Move the **mouse** | **Swipe** anywhere outside the stick |
| Setup | **F1** or **Setup** | **Setup** |

The Guy plays **idle** while standing and **walk** while moving. The city's buildings are
solid mesh colliders. Remote players are other Guy avatars with solid capsule colliders;
you cannot walk through them. The player channel starts with **Stream**, so a device that
only watches does not appear in the world. In **Stream** mode the bootstrap hides the
**FXM Canvas** playback overlay, so the 3D world and the scene skybox are unobstructed;
the canvas comes back in **Watch** and **Many** modes.

Set the same **Relay Url**, **Game Room** (default `demo`), and **Token** on every device.
Video rooms are separate: for many-to-one, keep one video room per sender but use the same
Game Room everywhere. Start the relay first:

```bash
cd Tools/RelayServer
npm start
```

On Android, replace `127.0.0.1` with the PC's Wi-Fi address. The relay accepts
`role=player` in addition to `sender` and `receiver`; player packets are forwarded to the
other players and never mixed with video. To add the setup to another scene, use
**Tools > FXM WebStream > Replace Test Cube with Walking Sim**.

---

## 1. Loopback in one minute

1. Open the project and wait for compilation.
2. Choose **Tools > FXM WebStream > Add Loopback Test Setup**.
3. Optionally add the start screen with **Tools > FXM WebStream > Add Setup UI (Start / Watch)**.
4. Press **Add**, then **Play**.

What appears:

- a camera rendering into a RenderTexture (the Game view may look empty),
- a `RawImage` showing a rotating cube,
- an overlay with FPS, JPEG size, and encode/decode time,
- **Play / Pause / Stop / Restart** buttons and a seek slider below the view.

Try the controls:

| Button | Result |
| --- | --- |
| Pause | The last frame remains visible. |
| Play | Streaming resumes. |
| Stop | The view clears; Play wakes the stream again. |
| Restart | Video sources return to the start; live sources simply resume. |

If the menu is missing, check the Console for compile errors. Unity only creates menus
after scripts compile.

---

## 2. Video input

There are three supported ways to feed a video into the stream.

### 2.1 VideoClip asset (recommended for Android/iOS)

1. Drag an **mp4**, **webm**, or **mov** file into `Assets` (for example
   `Assets/FXMWebStream/Media`).
2. Select **Tools > FXM WebStream > Add Video Playback Setup**.
3. Select `FXM Video Source` in the Hierarchy.
4. Assign the imported asset to **Clip** on `Video Player Stream Source`.
5. Press **Play**.

Unity imports the file as a `VideoClip` and includes it in the build, so this path works
on every platform, including Android and iOS.

To try a file without importing it, open the panel, choose **Clip**, then
**Choose local video...**. The in-panel browser lists folders and mp4/webm/mov/m4v/ogv
files with **Videos**, **Downloads**, and **App** shortcuts (desktop and Android; WebGL
has no local filesystem). The chosen path is remembered and applied through
`VideoPlayerStreamSource.SetUrl`, so the app reads the file directly from disk. On
Android, only files the app can read with normal storage permissions are listed; for a
file that must ship with the app, use a VideoClip asset instead.

### 2.2 Video file wizard

1. Choose **Tools > FXM WebStream > Add Video File Setup...**.
2. Pick a local video file.
3. The wizard copies it to `Assets/FXMWebStream/Media`, imports it as a `VideoClip`,
   creates the VideoPlayer source, sender, receiver, and playback bar, then assigns the
   clip automatically.
4. Press **Play**.

### 2.3 URL or local file path

Use the **Url** field when the file is served over HTTP or already exists on disk.

| Value | Platform | Notes |
| --- | --- | --- |
| `https://example.com/clip.mp4` | All | Recommended for mobile and WebGL. |
| `https://example.com/live.m3u8` | All | HLS support depends on the platform VideoPlayer backend. |
| `D:/media/clip.mp4` | Windows editor/build | Converted to `file:///D:/media/clip.mp4` automatically. |
| `/home/user/clip.mp4` | macOS/Linux | Converted to `file:///home/user/clip.mp4`. |
| `file:///D:/media/clip.mp4` | Windows | Used as-is. |

On Android, an absolute desktop path is not accessible. Use a VideoClip asset or an
HTTP(S) URL instead.

Runtime changes:

```csharp
using FXM.WebStream;
using UnityEngine;
using UnityEngine.Video;

public sealed class VideoLoader : MonoBehaviour
{
    public VideoPlayerStreamSource source;
    public VideoClip clip;

    public void LoadClip()
    {
        source.SetClip(clip);
        source.Play();
    }

    public void LoadUrl(string url)
    {
        source.SetUrl(url);
        source.Play();
    }
}
```

`SetClip`, `SetUrl`, and `Reload` reapply the source after `Awake`. Writing `clip` or
`url` directly after `Awake` does not reconfigure the VideoPlayer.

### 2.4 Video playback behavior

- `playOnStart` starts the clip when the scene starts.
- `loop` repeats the clip.
- `startTimeSeconds` seeks after preparation.
- The sender mirrors play/pause/stop/restart/seek commands to the VideoPlayer.
- `routeAudioToAudioSource` sends the clip's first audio track through an AudioSource so
  **Stream audio** can capture it. Disable it if you want Unity's direct audio output.

---

## 3. Webcam input (Android or desktop camera)

1. Choose **Tools > FXM WebStream > Add Webcam Sender (Relay)**.
2. Set **Relay Url**, **Room**, and **Token** to match the receiver.
3. Build to Android, or press Play on desktop.
4. Allow the camera permission prompt on first launch.

`WebCamStreamSource` copies `WebCamTexture` into a fixed-size RenderTexture, so the
sender can start before the camera opens. Change `requestedWidth`, `requestedHeight`, and
`requestedFps` to control capture. If the received image is upside down, enable
`flipVertically`; use `mirrorHorizontally` for a front-facing camera. The camera image is
live, so seeking is disabled.

For a custom Android manifest, include:

```xml
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.CAMERA" />
```

Unity's default **Internet Access** setting usually adds `INTERNET`. Camera permission is
requested at runtime by `Application.RequestUserAuthorization`.

---

## 4. Internet streaming with the relay

UDP and a direct WebSocket server work on a LAN. On the internet they need a public IP and
port forwarding, and mobile carriers usually block inbound connections. The relay avoids
that: **both peers connect outbound** to one public WebSocket endpoint.

### 4.1 Run the relay locally

```bash
cd Tools/RelayServer
npm install
FXM_RELAY_TOKEN=change-me npm start
```

On Windows PowerShell:

```powershell
cd Tools/RelayServer
npm install
$env:FXM_RELAY_TOKEN="change-me"
npm start
```

Health check: `http://127.0.0.1:8787/healthz`. It reports `rooms`, `senders`,
`receivers`, and `tokenRequired` (the token value is never exposed).

### 4.2 Deploy the relay

Any small Linux VPS or container host works. The relay only forwards bytes; it does not
transcode or store video.

```bash
docker build -t fxm-relay Tools/RelayServer
docker run -d --name fxm-relay \
  -e FXM_RELAY_TOKEN=change-me \
  -p 8787:8787 \
  fxm-relay
```

Put a TLS reverse proxy in front of it so browsers and mobile builds can use `wss://`.
Example Caddyfile:

```
relay.example.com {
    reverse_proxy 127.0.0.1:8787
}
```

The public URL is then `wss://relay.example.com/fxm-relay`.

Required server settings:

| Variable | Default | Purpose |
| --- | --- | --- |
| `PORT` | `8787` | HTTP/WebSocket port. |
| `HOST` | `0.0.0.0` | Bind address. |
| `FXM_RELAY_TOKEN` | empty | Shared secret. Set this in production. |
| `FXM_MAX_RECEIVERS` | `32` | Receiver limit per room. |
| `FXM_MAX_BUFFERED_BYTES` | `4194304` | Per-receiver backpressure limit. |
| `FXM_ALLOWED_ORIGINS` | empty | Comma-separated browser origins; empty allows all. |

### 4.3 Configure both scenes

**Sender** (**Tools > FXM WebStream > Add Relay Sender (Internet)**):

| Field | Value |
| --- | --- |
| Transport Kind | `Relay` |
| Relay Url | `wss://relay.example.com/fxm-relay` |
| Room | `demo` |
| Token | `change-me` |

**Receiver** (**Tools > FXM WebStream > Add Relay Receiver (Internet)**):

| Field | Value |
| --- | --- |
| Transport Kind | `Relay` |
| Relay Url | `wss://relay.example.com/fxm-relay` |
| Room | `demo` |
| Token | `change-me` |

Both use the same room. A room holds one sender and up to 32 receivers. If a sender
reconnects, the newest sender takes over the room and the old connection is closed with
code 4001. Desktop and mobile clients reconnect automatically with exponential backoff;
a WebGL page reconnects when it reloads.

### 4.4 Local relay test

Set the URL to `ws://127.0.0.1:8787/fxm-relay` on both scenes and run the relay locally.
This exercises the exact internet path without leaving the machine.

`Relay Url` accepts `ws://`, `wss://`, `http://`/`https://` (mapped to `ws`/`wss`), or a
bare `host:port`. If the path is omitted, `/fxm-relay` is used automatically. With the
default server settings the local URL is `ws://127.0.0.1:8787/fxm-relay`; there is no
relay bundled into a player, so the server must be running before Unity connects.

---

## 5. Windows to Windows (exe to exe)

1. Configure a relay sender and receiver with the same URL, room, and token.
2. **File > Build Profiles > Windows** and build two players, or build one scene with a
   runtime toggle that starts either component.
3. Run both. The sender publishes; the receiver shows the stream.

For a LAN-only build, use UDP (sender `remoteHost` = receiver LAN IP) or a WebSocket
server on the sender. Open the firewall ports, then connect by LAN IP.

---

## 6. Windows to Android (exe to Android)

1. Build the sender as a Windows player.
2. Switch the project to Android and use a relay receiver scene.
3. **Edit > Project Settings > Player > Android**:
   - **Internet Access**: `Require`
   - **Scripting Backend**: `IL2CPP`
   - **Target Architectures**: `ARM64` (add `ARMv7` only if needed)
   - **Allow downloads over HTTP**: `Always` only when using `http://` or `ws://`;
     prefer HTTPS/WSS.
4. Build and install the APK.
5. Start the Windows sender and the Android receiver with the same room and token.

The receiver needs no inbound ports. Only outbound TCP 443 (or 8787 for plain `ws://`) is
required.

On the same Wi-Fi you can skip the public host: run the relay on the PC, allow TCP 8787
through the firewall, and use `ws://<PC-LAN-IP>:8787/fxm-relay` on the Android device.
Never use `127.0.0.1` there; that address means the phone itself.

### 6.1 One laptop sender to two Android receivers

One room supports **one sender and up to 32 receivers** (default). All receivers get the
same frames in parallel; the sender encodes once.

```
Laptop (sender) --ws/wss--> Relay --ws/wss--> Phone A (receiver)
                                   \--------> Phone B (receiver)
```

1. **Start the relay on the laptop.**

   ```powershell
   cd Tools\RelayServer
   npm install
   $env:FXM_RELAY_TOKEN="demo-token"
   npm start
   ```

   Note the laptop LAN IP (`ipconfig`), for example `192.168.2.246`.

2. **Laptop sender**: add the setup UI, press Play, choose **Stream**, **Relay**, then
   `ws://192.168.2.246:8787/fxm-relay`, room `demo`, token `demo-token`. Choose the video
   source and press **Start streaming**.

3. **Android receiver scene**: use a scene with a `StreamingReceiver` and the setup UI
   (**Tools > FXM WebStream > Add Setup UI (Start / Watch)**). Set **Internet Access =
   Require**, **IL2CPP**, and **ARM64** in Player Settings, then build one APK.

4. **Install the same APK on both phones.** On each phone choose **Watch**, **Relay**,
   the same URL, room, and token, then press **Watch stream**. Press **F1** to hide the
   panel. Both phones should show the laptop stream.

5. **Confirm both joined** on the laptop: `http://127.0.0.1:8787/healthz` should report
   `"receivers": 2`, or the relay log shows two `receiver joined room demo` lines.

If a phone uses mobile data or another network, the laptop LAN IP is not reachable. Use a
public relay with `wss://relay.example.com/fxm-relay` and the same room/token instead.
Avoid guest Wi-Fi networks that block client-to-client traffic.

### 6.1.1 Many-to-one: several senders, one viewer

A relay room still has exactly one sender. To watch several senders, give each one its own
room and list all rooms on the viewer.

1. Sender A: **Stream > Relay**, room `cam-a`, token `demo-token`, **Start streaming**.
2. Sender B: **Stream > Relay**, same URL/token, room `cam-b`, **Start streaming**.
3. Viewer: open **Many**, choose **Relay**, enter the same URL and token, and put
   `cam-a, cam-b` under **Sources**. Press **Watch 2 sources**.
4. The grid fills the screen automatically. Click a cell or press 1-9 to select the source
   whose audio you hear; tap the screen for the controls.

With WebSocket, use one URL per sender instead of rooms. UDP is not offered in Many
because every source would need its own local port; use a `MultiStreamReceiver` component
for that case.

### 6.1.2 Many-to-many: every node sends and watches

1. On each node press **Start streaming** with a unique room (`alice`, `bob`, `carol`).
2. Without pressing Stop, switch to **Many** and list the other nodes' rooms.
3. Press **Watch N sources**. The sender keeps running, so the node now sends its own stream and
   shows every other node in a fullscreen grid.

Do not put two senders in the same room: the relay replaces the older sender (close code
`4001`). On Android the app can keep sending while the grid is visible, but the camera
stream continues to use CPU and battery.

### 6.1.3 Audio

Audio is off by default on a raw `StreamingSender`; the setup panel enables it.

1. Sender: open **Stream**, enable **Stream audio**, pick a quality, and start.
2. Receiver: open **Watch** or **Many**, enable **Play audio**, and start.
3. If a VideoPlayer clip is silent, keep `VideoPlayerStreamSource.routeAudioToAudioSource`
   enabled. It routes track 0 through an AudioSource so the AudioListener can capture it.

Audio adds about 256 kbps (16 kHz mono), 384 kbps (24 kHz mono), or 1536 kbps (48 kHz
stereo) on top of the video bitrate. The sender pays that upload once even with many
viewers. Audio capture is unavailable on WebGL, and there is no lip-sync correction after
a seek.

### 6.2 Phone says "Watching. Waiting for the sender."

This message means the receiver process is running, but it has not received a frame yet. On
the phone, the setup panel now shows a second line: **Relay: connected** or **Relay: not
connected**. Use that line first; it separates a network problem from a sender problem.

Run this on the laptop **while** the sender is streaming and the phone is watching:

```bash
curl http://127.0.0.1:8787/healthz
```

| Result | Meaning | Fix |
| --- | --- | --- |
| `"senders":0` | Unity is not connected to this relay | Sender must use **Relay**, the same room and token, and the same relay host/port. Check the Unity Console for `WebSocket client error` and `401`. |
| `"receivers":0` | The phone is not connected to this relay | On the phone, `127.0.0.1` means the phone itself. Use the laptop Wi-Fi address, e.g. `ws://192.168.2.246:8787/fxm-relay`. Check that both devices are on the same Wi-Fi and that Windows Firewall allows inbound TCP 8787. |
| `"senders":1,"receivers":1` | Both are connected, but no frame reached the phone | Press **Start streaming** in Unity, choose a valid video source, and confirm the sender is not paused. `frames` in the phone's status line should increase. |
| Both are `0` | Neither side connected | The relay is running, but both clients were rejected or cannot reach it. A token mismatch returns `401`: check `"tokenRequired"` in `/healthz`. If it is `false`, leave **Token** empty on both sides. |

If `"receivers":0` and the phone still cannot connect, check Windows Firewall first. A PC
connected to Wi-Fi often uses the **Public** profile, which blocks all inbound traffic by
default. Add an inbound rule for the relay port from an elevated Command Prompt:

```bat
netsh advfirewall firewall add rule name="FXM Relay 8787 (LAN)" dir=in action=allow protocol=TCP localport=8787 profile=private,public remoteip=LocalSubnet
```

This allows only devices on the same subnet. Then open, for example,
`http://192.168.2.246:8787/healthz` **in the phone's browser**. If that page does not load,
the problem is the network or the firewall, not the app. If it loads, check that the app uses
the **Relay** tab, the same room/token, and a `ws://` URL (not `127.0.0.1`).

Other checks:

- Use the **same URL, room, and token** on both sides. The token is case-sensitive.
- The relay must be running; opening `http://127.0.0.1:8787/healthz` on the laptop should
  return JSON, not a connection error.
- Android Player Settings: **Internet Access = Require**. For plain `ws://` also set
  **Allow downloads over HTTP = Always**; use `wss://` for anything outside a trusted LAN.
- Disable guest Wi-Fi or AP isolation, which can block phone-to-laptop traffic.
- If the phone uses mobile data, the laptop's `192.168.x.x` address is unreachable; deploy
  the relay publicly and use `wss://`.

### 6.3 Do the relay menu presets have to be added before building?

No. **Add Relay Sender (Internet)** and **Add Relay Receiver (Internet)** are presets that
create and configure the components; they are not build requirements. The setup UI can
switch an existing sender or receiver to Relay at runtime.

For a phone build, prefer a **receiver-only scene**: create it with **Add Relay Receiver
(Internet)**, add **Add Setup UI (Start / Watch)**, and build that scene. Run the sender on
the laptop from its own scene. Keeping the roles in separate scenes avoids an accidental
sender on the phone and keeps the APK smaller.

---

## 7. Android to Windows (Android to exe)

This is the webcam direction:

1. On a fresh scene choose **Tools > FXM WebStream > Add Webcam Sender (Relay)**.
2. Set the relay URL, room, and token.
3. Switch to Android, configure Internet Access and camera permission, then build.
4. On Windows, choose **Tools > FXM WebStream > Add Relay Receiver (Internet)** and use
   the same room and token.
5. Run the Android app first (allow camera access), then the Windows receiver.

The Android sender can also stream a VideoClip instead of the camera. Use
**Add Video File Setup...**, then change `Transport Kind` to `Relay` and fill the relay
fields.

---

## 8. WebGL

- WebGL has no UDP and no `AsyncGPUReadback`; it uses the browser WebSocket and
  `EncodeToJPG` on the main thread. The setup panel hides UDP on this platform.
- Use `wss://` when the page is served over HTTPS; the panel refuses plain `ws://` on an
  HTTPS page instead of failing silently.
- A WebGL peer is always a client: run the relay, or a WebSocket server on a desktop. In
  **Stream > WebSocket** the panel shows a **Server URL** field (not a listen port) and the
  browser sends out to that desktop server.
- Lower `resolutionScale`, `targetFps`, and `jpegQuality` to avoid main-thread hitches.

---

## 9. Quick troubleshooting

| Symptom | Fix |
| --- | --- |
| Menu missing | Wait for compilation; check Console errors. |
| Black view | Assign a Clip or URL; check VideoPlayer errors; allow camera permission. |
| Webcam upside down | Enable `flipVertically` on `WebCamStreamSource`. |
| Receiver stays black on relay | Same URL, room, and token; check `/healthz`; use `wss://` if the page is HTTPS. See section 6.2 for the health-value table. |
| Phone shows "Watching. Waiting for the sender." | Check the panel's **Relay: connected** line and `/healthz`. `senders:0` = Unity sender not on this relay; `receivers:0` = phone not connected (usually `127.0.0.1`, firewall, or different Wi-Fi). |
| Game view is empty while streaming | Expected for camera senders: the camera renders into the sender texture. Hide the panel (the preview fills the screen) or use the receiver view; stop the stream to see the scene again. |
| No analogue stick on a phone or tablet browser | Hide the setup panel first; the stick only draws over the game view. It appears for Android/iOS and whenever the Input System reports a touchscreen. |
| Remote players never appear | Both devices must be in the same **Game Room** on the same relay, and both must have pressed **Start streaming**. Check the relay `/healthz` output for `players`. |
| Video looks stretched on a phone | Keep `preserveAspectRatio` on the receiver (the setup panel forces it on). The view letterboxes landscape video in portrait instead of stretching it. |
| Start/Stop/Exit buttons are off-screen | Update to the current setup panel; it uses a fixed footer and DPI scaling. |
| Start button is disabled | The reason is shown next to it: missing URL, invalid room, no clip, or no sender/receiver. |
| `401` on relay | Token mismatch. If the relay was started without `FXM_RELAY_TOKEN`, clear the token field on both sides. |
| `400` on relay | Room must match `[A-Za-z0-9_-]{1,64}` and role must be sender, receiver, or player. |
| `Unable to connect to the remote server` | Nothing is listening. Start the relay with `npm start`, open `http://127.0.0.1:8787/healthz`, then confirm the scheme, host, port, and `/fxm-relay` path. |
| Sender cannot connect on Android | Internet Access = Require; use `wss://` or allow HTTP downloads. `127.0.0.1` points at the phone; use the PC's LAN IP or a public relay. |
| Buttons do nothing | `allowRemoteControl` and `allowRemoteCommands` on; same control port for UDP. |
| Second source in Multi is empty | Give each UDP source a different local port. |
| Game view is empty | Expected for camera senders; look at the receiver RawImage. |
| High latency | Lower resolution/FPS/quality; do not queue stale frames; check relay region. |

Full field reference and tuning: [Usage-Guide.md](Usage-Guide.md).
