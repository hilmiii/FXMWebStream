# Mentor Quickstart: Run FXM WebStream at Home

This guide is for someone who receives this project and wants to try it on their own
Windows PC and Android phone. Section A needs no access to your PC or network; section C
uses your PC as the sender and requires a public relay.

There are two different goals:

| Goal | Section |
| --- | --- |
| Run the sender and receiver entirely at the mentor's home | **A** |
| Test with an already-built `.exe` and `.apk` | **D** |
| Watch a stream coming from your PC in another house | **C** |

---

## A. Try everything at the mentor's home (recommended)

Both devices must be on the **same Wi-Fi**. The PC sends the video; the phone watches it.
The relay runs on the PC and only forwards bytes; it does not store video.

### A.0 What to prepare

- Windows 10/11 PC and an Android phone (Android 8.0 or newer).
- Unity Hub with **Unity 6000.5.8f1** and the **Android Build Support** module
  (Android SDK, NDK, and OpenJDK).
- **Node.js 18 or newer** on the PC.
- This project folder, including `Assets/FXMWebStream`, `Tools/RelayServer`,
  `ProjectSettings`, and `Packages`.
- Internet access on the PC for the first Unity package import and `npm install`.

When sending the project as a zip, exclude `Library/`, `Temp/`, `Logs/`, `obj/`, and
`Builds/`; Unity regenerates those folders. Exclude `Tools/RelayServer/node_modules/` too
and let the mentor run `npm install`.

If the mentor does not want to install Unity, build the APK first and send it together
with the `Tools/RelayServer` folder. The mentor still needs Node.js to run the relay.

### A.1 Install the tools

1. Install **Unity Hub**, then install **Unity 6000.5.8f1** with
   **Android Build Support** and its SDK/NDK/OpenJDK submodules.
2. Install **Node.js 18+** (the Windows installer includes `npm`).
3. Verify in PowerShell:

   ```powershell
   node --version
   npm --version
   ```

   Both commands must print a version. If `node` is not recognized, reopen PowerShell
   after installing Node.js.

### A.2 Open the project

1. In Unity Hub, choose **Add > Add project from disk** and select the project folder.
2. Open it with Unity **6000.5.8f1** and wait until package import and compilation finish.
3. Open `Assets/Scenes/FXMWebStream.unity`.
4. The scene already contains a sender, a receiver, and the **FXM Setup UI** panel.
   If the panel is missing, use
   **Tools > FXM WebStream > Add Setup UI (Start / Watch)** once, then save the scene.

### A.3 Start the relay on the PC

Open PowerShell in the project folder:

```powershell
cd Tools\RelayServer
npm install
$env:FXM_RELAY_TOKEN="demo-token"
npm start
```

Expected output includes:

```text
[relay] listening on ws://0.0.0.0:8787/fxm-relay
```

Keep this window open. Closing it stops the relay.

- Use the **same token** on the PC and phone.
- If you do not want a token, run `npm start` without setting `FXM_RELAY_TOKEN` and
  leave the **Token** field empty on both sides.
- The relay does not need to be installed as a service for a short demo.

Check it from the PC:

```powershell
curl http://127.0.0.1:8787/healthz
```

The response must contain `"status":"ok"`. `senders` and `receivers` are `0` until the
PC and phone connect.

### A.4 Find the PC's Wi-Fi address

```powershell
ipconfig
```

Under the active **Wireless LAN adapter Wi-Fi** entry, note the **IPv4 Address**, for
example `192.168.1.20`. Do not use `127.0.0.1` on the phone; on Android that address means
the phone itself.

Write the relay URL down:

```text
ws://192.168.1.20:8787/fxm-relay
```

Replace `192.168.1.20` with the address printed by `ipconfig`.

### A.5 Allow port 8787 through Windows Firewall

Open **Command Prompt as Administrator** on the PC and run:

```bat
netsh advfirewall firewall add rule name="FXM Relay 8787" dir=in action=allow protocol=TCP localport=8787 profile=private,public remoteip=LocalSubnet
```

This allows only devices on the same local network. If Windows shows a firewall prompt
when Node starts, choose **Allow access** for **Private networks** as well.

### A.6 Start the sender in Unity

1. Press **Play** in Unity.
2. In the setup panel choose:
   - **Stream**
   - **Relay**
   - **Relay URL**: `ws://192.168.1.20:8787/fxm-relay`
   - **Room**: `demo`
   - **Token**: `demo-token` (or empty if the relay has no token)
   - **Video source**: **Scene camera** (shows the walking-sim character), a **Video clip**,
     or **Webcam**
3. Press **Start streaming**.
4. On the PC, check `http://127.0.0.1:8787/healthz`. It should now report
   `"senders":1`.

The Game view can look empty while the camera is being streamed; the image is in the
sender's preview and on the receiver. This is expected. Press **Hide** or **F1** to play
the walking sim full screen; WASD moves and moving the mouse looks around. For two players, set
the same **Game Room** on both devices so the Guys can see and collide with each other.

For a short demo, keeping Unity in Play mode is enough. For a standalone sender, use
**File > Build Profiles > Windows** and run the built `.exe` instead.

### A.7 Build the Android APK

1. In Unity choose **File > Build Profiles**.
2. Select **Android**, then **Switch Platform** and wait for the reimport.
3. Open **Edit > Project Settings > Player > Android** and confirm:
   - **Internet Access**: `Require`
   - **Scripting Backend**: `IL2CPP`
   - **Target Architectures**: `ARM64`
   - **Minimum API Level**: Android 8.0 (API 26) or newer
4. In **Build Profiles > Scene List**, confirm
   `Assets/Scenes/FXMWebStream.unity` is enabled.
5. Choose **Build**, create a folder such as `Builds\Android`, and save the APK as
   `FXMWebStream.apk`.
6. Install it on the phone by copying the APK and opening it, or with USB debugging:

   ```powershell
   adb install -r Builds\Android\FXMWebStream.apk
   ```

   Android may ask to allow installation from the file manager or browser; allow it for
   this one installation.

### A.8 Watch on the phone

1. Connect the phone to the **same Wi-Fi** as the PC.
2. Turn off **mobile data** and any **VPN** during the test.
3. Open the app and choose:
   - **Watch**
   - **Relay**
   - **Relay URL**: `ws://192.168.1.20:8787/fxm-relay`
   - **Room**: `demo`
   - **Token**: `demo-token` (must match the PC exactly)
4. Press **Watch stream**.
5. The app switches to the fullscreen viewer automatically. If you opened the panel again,
   press **F1** or **Hide**. Tap the screen or move the mouse to show the controls
   (**Setup**, **Fill/Fit**, **Audio**, **Stop**).

Fullscreen video starts within a few seconds.

### A.9 Verify the connection

On the PC:

```powershell
curl http://127.0.0.1:8787/healthz
```

Expected while both sides are running:

```json
{"status":"ok","rooms":1,"senders":1,"receivers":1}
```

From the phone browser, the same page should open at:

```text
http://192.168.1.20:8787/healthz
```

If the phone browser cannot open that page, the problem is the network or firewall, not
the Unity app. See the troubleshooting table below.

---

## B. No relay: direct WebSocket on the local network

This is the simplest option when both devices are in the same house and Node.js is not
available. The PC becomes the WebSocket server; the phone connects directly to it.

1. Allow the WebSocket port on the PC (Administrator Command Prompt):

   ```bat
   netsh advfirewall firewall add rule name="FXM WebSocket 47778" dir=in action=allow protocol=TCP localport=47778 profile=private,public remoteip=LocalSubnet
   ```

2. In Unity, press **Play**, choose **Stream > WebSocket**, set the listen port to
   `47778`, choose a video source, and press **Start streaming**.
3. On the phone choose **Watch > WebSocket** and set:

   ```text
   ws://192.168.1.20:47778/fxm-stream/
   ```

   Use the PC's actual Wi-Fi IPv4 address.
4. Press **Watch stream**.

There is no room or token in this mode. Do not use this over the internet; it has no
encryption and no authentication.

---

## C. Mentor watches your PC from a different house

Two different homes cannot use each other's `192.168.x.x` address. Both sides must connect
to a **public relay** instead.

1. Deploy `Tools/RelayServer` to a small Linux VPS or container host.
2. Set a strong token:

   ```bash
   docker build -t fxm-relay Tools/RelayServer
   docker run -d --name fxm-relay \
     -e FXM_RELAY_TOKEN=change-me \
     -p 127.0.0.1:8787:8787 \
     fxm-relay
   ```

3. Put a TLS reverse proxy in front of it so the public URL uses `wss://`, for example:

   ```text
   relay.example.com {
       reverse_proxy 127.0.0.1:8787
   }
   ```

4. Use the same public URL, room, and token on both sides:

   ```text
   wss://relay.example.com/fxm-relay
   ```

5. Open TCP 443 on the server. Do not expose plain `ws://` to the internet.
6. Both the sender and the receiver connect **outbound**; no port forwarding is needed
   on either home router.

If a public server is not available, a tunnel such as Cloudflare Tunnel can expose the
local relay through HTTPS. The URL must still be `wss://`, and a token should be set.

---

## D. Test with the prebuilt .exe and .apk

Use this when the mentor already received the Windows build and the Android APK. Unity and
Node.js are not required for the direct WebSocket test. This assumes the `.exe` is the
sender and the `.apk` is the receiver, and the phone runs Android 8.0 or newer. Both
devices must be on the same Wi-Fi; no internet connection is needed.

### D.1 Copy the whole Windows build

A Unity Windows build is not only the `.exe`. Copy or extract the **entire build folder**,
including:

- `FXMWebStream.exe`
- `FXMWebStream_Data/`
- `UnityPlayer.dll`
- `GameAssembly.dll` (IL2CPP) or `MonoBleedingEdge/` (Mono)
- any other files next to the `.exe`

Run the `.exe` from a normal folder, not from inside the zip. If Windows SmartScreen
appears, choose **More info > Run anyway**.

### D.2 Prepare the network

1. Connect the PC and phone to the same non-guest Wi-Fi.
2. Turn off mobile data and VPN on the phone.
3. Allow the app through Windows Firewall when prompted (Private networks). If there is no
   prompt, run this in an Administrator Command Prompt:

   ```bat
   netsh advfirewall firewall add rule name="FXM WebSocket 47778" dir=in action=allow protocol=TCP localport=47778 profile=private,public remoteip=LocalSubnet
   ```

4. On the PC, run `ipconfig` and note the **IPv4 Address** of the active Wi-Fi adapter. Do
   not use `169.254.x.x`, VMware, or Bluetooth addresses.

### D.3 Start the sender (.exe)

1. Run the `.exe`.
2. In the setup panel choose:
   - **Stream**
   - **WebSocket**
   - **Listen port**: `47778`
   - a video source: **Scene camera**, **Video clip**, or **Webcam**
3. Press **Start streaming**.
4. If the build has no WebSocket option and only exposes Relay, skip to **D.5**.

### D.4 Start the receiver (.apk)

1. Install the APK. Allow installation from the file manager/browser if Android asks.
2. Open the app and choose:
   - **Watch**
   - **WebSocket**
   - URL: `ws://<PC-WIFI-IP>:47778/fxm-stream/`
3. Press **Watch stream**.
4. The preview should appear within a few seconds. The status should show **WebSocket**,
   **connected**, and a frame count that increases.

Test the controls: **Pause**, **Play**, **Stop**, **Restart**, and seek. The sender should
react.

### D.5 If the build uses Relay instead

If the `.exe` was built with a relay URL and has no WebSocket option, the mentor also needs
the `Tools/RelayServer` folder and Node.js 18+. If the mentor only received the `.exe` and
`.apk`, use the direct WebSocket mode in **D.3** instead, or send a build that exposes the
**WebSocket** transport.

1. Run the relay on the PC (Node.js 18+):

   ```powershell
   cd Tools\RelayServer
   npm install
   $env:FXM_RELAY_TOKEN="demo-token"
   npm start
   ```

2. Allow TCP 8787 in Windows Firewall:

   ```bat
   netsh advfirewall firewall add rule name="FXM Relay 8787" dir=in action=allow protocol=TCP localport=8787 profile=private,public remoteip=LocalSubnet
   ```

3. Use **Relay** on both sides with the same URL, room, and token:
   `ws://<PC-WIFI-IP>:8787/fxm-relay`, room `demo`, token `demo-token`.
4. Check `http://127.0.0.1:8787/healthz`; it must show `"senders":1` and
   `"receivers":1`.

### D.6 Pass/fail checklist

| Check | Pass |
| --- | --- |
| `.exe` starts without missing-DLL errors | The whole build folder was copied |
| PC status shows streaming and frames sent | Sender is running |
| Phone preview appears and frames increase | Receiver connected and decoding |
| Pause/Play/Stop/seek work | Control channel works |
| Phone can open the LAN URL used by the app | Network/firewall is correct |

If the phone cannot connect, verify the PC IP, use the same Wi-Fi, turn off mobile data/VPN,
and allow the port. Never use `127.0.0.1` on the phone.

---

## E. Audio and multiple senders

With the current build:

1. Sender: in the setup panel choose **Stream**, pick a **Stream audio** quality, then
   press **Start streaming**.
2. Receiver: choose **Watch**, enable **Play audio**, then press **Watch stream**.
3. For several senders, give each one its own relay room. On the viewer choose
   **Many**, enter the rooms, and press **Watch N sources**. The grid fills the screen;
   press 1-9 or tap a cell to switch the active source (audio follows it).
4. For many-to-many, every node starts its own stream first, then switches to **Many**
   and lists the other nodes' rooms.

Older `.exe`/`.apk` builds without these options still work one-to-one; rebuild from the
project to get audio and multi-source watching.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| Phone browser cannot open `http://PC-IP:8787/healthz` | Firewall, different Wi-Fi, mobile data, VPN, or router isolation | Allow TCP 8787, use the same non-guest Wi-Fi, turn off mobile data/VPN, disable AP/client isolation. |
| `ERR_CONNECTION_REFUSED` on the phone | Wrong IP/port, relay not running, or a proxy | Confirm `ipconfig`, confirm `npm start` is still running, set the Wi-Fi proxy to **None**. |
| `/healthz` shows `"senders":0` | Unity is not streaming or is on another relay | Press **Start streaming**, choose **Relay**, and match the URL/room/token. |
| `/healthz` shows `"receivers":0` | Phone is not connected | On the phone use the PC's LAN IP, the **Relay** tab, and the same room/token. |
| Both are `1` but the preview is white | No frame is being produced | Choose **Scene camera**, a valid **Video clip**, or **Webcam**; press **Play** if paused. |
| Phone cannot install the APK | Unknown-source installation is blocked | Allow installs from the file manager/browser, or use `adb install -r`. |
| Works on PC, fails on phone | Phone is using mobile data or a different network | Turn off mobile data, use the same Wi-Fi, and disable VPN/proxy. |
| No audio | **Stream audio** or **Play audio** is off, or the scene has no AudioListener | Enable both switches; keep an AudioListener; for a video clip keep `routeAudioToAudioSource` on. |
| Second sender kicks the first | Both senders use the same relay room | Give each sender its own room; list all rooms under **Many**. |
| One grid cell stays empty | That room has no sender | Start the sender for that room, or remove it from **Sources**. |

Remember:

- On the phone, `127.0.0.1` is the phone itself. Always use the PC's Wi-Fi IPv4 address.
- The relay token is not saved by the app; enter it again after restarting the app.
- Audio is optional PCM16: enable **Stream audio** on the sender and **Play audio** on the
  receiver. Use 16-24 kHz mono on slow links. Audio capture is unavailable on WebGL.
- The relay is stateless: stopping it disconnects both sides, but no media is stored.

---

## Short checklist to send with the project

```text
1. Install Unity 6000.5.8f1 + Android Build Support, and Node.js 18+.
2. Open Assets/Scenes/FXMWebStream.unity.
3. cd Tools\RelayServer && npm install
4. $env:FXM_RELAY_TOKEN="demo-token"; npm start
5. ipconfig -> note the Wi-Fi IPv4 address.
6. Allow TCP 8787 in Windows Firewall (admin).
7. Unity Play -> Stream -> Relay -> ws://PC-IP:8787/fxm-relay -> demo/demo-token.
8. Build Android APK (IL2CPP, ARM64, Internet Access = Require).
9. Phone: same Wi-Fi, mobile data off -> Watch -> Relay -> same URL/room/token.
10. Check http://PC-IP:8787/healthz: senders 1, receivers 1.
```
