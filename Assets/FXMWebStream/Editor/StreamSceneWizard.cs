using System.Collections.Generic;
using System.IO;
using FXM.WebStream;
using FXM.WebStream.Samples;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

namespace FXM.WebStream.EditorTools
{
    /// <summary>
    /// One-click scene setup menus:
    /// - Camera -> sender -> receiver loopback over UDP.
    /// - VideoPlayer -> sender -> receiver with playback controls.
    /// - Video file import with a VideoClip source.
    /// - Internet relay sender and receiver scenes.
    /// - One-to-many and many-to-one demos.
    /// </summary>
    public static class StreamSceneWizard
    {
        private const int Port = 47777;
        private const int ControlPort = 47800;

        [MenuItem("Tools/FXM WebStream/Add Loopback Test Setup")]
        public static void AddLoopbackSetup()
        {
            if (!Confirm("Add a loopback test setup (camera + sender + Canvas + receiver + playback controls)?"))
            {
                return;
            }

            int undoGroup = BeginUndo("FXM Loopback Setup");

            Camera camera = GetOrCreateCamera(new Vector3(0f, 2f, -8f), new Vector3(0f, 1f, 0f));
            CreateTestContent(new Vector3(0f, 1f, 0f));
            Canvas canvas = GetOrCreateCanvas();

            RawImage view = CreateView(canvas, "FXM Stream View",
                Vector2.zero, Vector2.one, new Vector2(20f, 76f), new Vector2(-20f, -20f));

            var sender = GetOrAdd<StreamingSender>(camera.gameObject);
            Undo.RecordObject(sender, "Configure Sender");
            sender.sourceCamera = camera;
            sender.sourceTexture = null;
            sender.videoSourceBehaviour = null;
            sender.resolutionScale = 0.6f;
            sender.captureMethod = CaptureMethod.AsyncGPUReadback;
            sender.flipVertically = true;
            sender.targetFps = 30;
            sender.jpegQuality = 65;
            sender.encoderThreads = 1;
            sender.transportKind = TransportKind.Udp;
            sender.remoteHost = "127.0.0.1";
            sender.remotePort = Port;
            sender.udpReceivers = new string[0];
            sender.allowRemoteControl = true;
            sender.localControlPort = ControlPort;
            sender.mtu = 1200;
            sender.autoStart = true;
            sender.autoPlay = true;
            sender.showStatistics = false;
            EditorUtility.SetDirty(sender);

            var receiverObject = new GameObject("FXM Stream Receiver");
            Undo.RegisterCreatedObjectUndo(receiverObject, "Create Receiver");
            var receiver = Undo.AddComponent<StreamingReceiver>(receiverObject);
            Undo.RecordObject(receiver, "Configure Receiver");
            receiver.transportKind = TransportKind.Udp;
            receiver.listenPort = Port;
            receiver.senderHost = "127.0.0.1";
            receiver.senderControlPort = ControlPort;
            receiver.autoStart = true;
            receiver.targetRawImage = view;
            receiver.preserveAspectRatio = true;
            receiver.maxDecodeFps = 30;
            receiver.allowRemoteCommands = true;
            receiver.showStatistics = true;
            EditorUtility.SetDirty(receiver);

            CreatePlaybackBar(canvas, receiver);
            FinishUndo(undoGroup, receiverObject);
            Selection.activeGameObject = receiverObject;

            Debug.Log("[FXM.WebStream] Loopback setup ready. Press Play: camera -> UDP -> RawImage; " +
                      "Play/Pause/Stop send commands to the sender.");
        }

        [MenuItem("Tools/FXM WebStream/Add Video Playback Setup")]
        public static void AddVideoPlaybackSetup()
        {
            if (!Confirm("Add a video setup (VideoPlayer + sender + receiver + playback controls)?"))
            {
                return;
            }

            CreateVideoSetup(null, null);
        }

        [MenuItem("Tools/FXM WebStream/Add Video File Setup...")]
        public static void AddVideoFileSetup()
        {
            string selected = EditorUtility.OpenFilePanel(
                "Select a video file", string.Empty, "mp4,webm,mov,m4v,avi");

            if (string.IsNullOrEmpty(selected))
            {
                return;
            }

            VideoClip clip = ImportVideoClip(selected);
            if (clip == null)
            {
                EditorUtility.DisplayDialog("FXM WebStream",
                    "Unity could not import this file as a VideoClip. Check that the Video module is installed " +
                    "and that the codec is supported.", "OK");
                return;
            }

            CreateVideoSetup(clip, null);
            Debug.Log("[FXM.WebStream] Imported video clip: " + AssetDatabase.GetAssetPath(clip));
        }

        private static VideoClip ImportVideoClip(string sourcePath)
        {
            string assetsRoot = Path.GetFullPath(Application.dataPath);
            string fullPath = Path.GetFullPath(sourcePath);
            string destination = fullPath;

            if (!fullPath.StartsWith(assetsRoot, System.StringComparison.OrdinalIgnoreCase))
            {
                string mediaFolder = Path.Combine(assetsRoot, "FXMWebStream", "Media");
                Directory.CreateDirectory(mediaFolder);
                destination = Path.Combine(mediaFolder, Path.GetFileName(fullPath));

                int suffix = 1;
                while (File.Exists(destination))
                {
                    string name = Path.GetFileNameWithoutExtension(fullPath);
                    string extension = Path.GetExtension(fullPath);
                    destination = Path.Combine(mediaFolder, name + "_" + suffix + extension);
                    suffix++;
                }

                File.Copy(fullPath, destination);
            }

            AssetDatabase.Refresh();

            string relative = "Assets" + destination.Substring(assetsRoot.Length).Replace('\\', '/');
            AssetDatabase.ImportAsset(relative, ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<VideoClip>(relative);
        }

        private static void CreateVideoSetup(VideoClip clip, string url)
        {
            int undoGroup = BeginUndo("FXM Video Setup");

            Canvas canvas = GetOrCreateCanvas();
            RawImage view = CreateView(canvas, "FXM Video View",
                Vector2.zero, Vector2.one, new Vector2(20f, 76f), new Vector2(-20f, -20f));

            var videoObject = new GameObject("FXM Video Source", typeof(VideoPlayer));
            Undo.RegisterCreatedObjectUndo(videoObject, "Create Video Source");
            var videoPlayer = videoObject.GetComponent<VideoPlayer>();
            videoPlayer.playOnAwake = false;
            videoPlayer.renderMode = VideoRenderMode.RenderTexture;

            var videoSource = Undo.AddComponent<VideoPlayerStreamSource>(videoObject);
            videoSource.videoPlayer = videoPlayer;
            videoSource.clip = clip;
            videoSource.url = url;
            videoSource.width = 1280;
            videoSource.height = 720;
            videoSource.playOnStart = true;
            videoSource.loop = true;
            EditorUtility.SetDirty(videoSource);

            var senderObject = new GameObject("FXM Video Sender");
            Undo.RegisterCreatedObjectUndo(senderObject, "Create Video Sender");
            var sender = Undo.AddComponent<StreamingSender>(senderObject);
            Undo.RecordObject(sender, "Configure Video Sender");
            sender.sourceCamera = null;
            sender.sourceTexture = null;
            sender.videoSourceBehaviour = videoSource;
            sender.resolutionScale = 0.75f;
            sender.captureMethod = CaptureMethod.AsyncGPUReadback;
            sender.flipVertically = true;
            sender.targetFps = 30;
            sender.jpegQuality = 70;
            sender.transportKind = TransportKind.Udp;
            sender.remoteHost = "127.0.0.1";
            sender.remotePort = Port;
            sender.localControlPort = ControlPort;
            sender.allowRemoteControl = true;
            sender.autoStart = true;
            sender.autoPlay = true;
            EditorUtility.SetDirty(sender);

            var receiverObject = new GameObject("FXM Video Receiver");
            Undo.RegisterCreatedObjectUndo(receiverObject, "Create Video Receiver");
            var receiver = Undo.AddComponent<StreamingReceiver>(receiverObject);
            Undo.RecordObject(receiver, "Configure Video Receiver");
            receiver.listenPort = Port;
            receiver.senderHost = "127.0.0.1";
            receiver.senderControlPort = ControlPort;
            receiver.targetRawImage = view;
            receiver.autoStart = true;
            receiver.showStatistics = true;
            EditorUtility.SetDirty(receiver);

            CreatePlaybackBar(canvas, receiver);
            FinishUndo(undoGroup, videoObject);
            Selection.activeGameObject = receiverObject;

            Debug.Log("[FXM.WebStream] Video setup ready. Assign a Clip or URL on VideoPlayerStreamSource, " +
                      "then press Play. The seek slider activates once the duration is known.");
        }

        [MenuItem("Tools/FXM WebStream/Add Relay Sender (Internet)")]
        public static void AddRelaySenderSetup()
        {
            if (!Confirm("Add an internet relay sender? Both peers will connect as clients to a relay server."))
            {
                return;
            }

            int undoGroup = BeginUndo("FXM Relay Sender Setup");

            Camera camera = GetOrCreateCamera(new Vector3(0f, 2f, -8f), new Vector3(0f, 1f, 0f));
            CreateTestContent(new Vector3(0f, 1f, 0f));

            var sender = GetOrAdd<StreamingSender>(camera.gameObject);
            Undo.RecordObject(sender, "Configure Relay Sender");
            sender.sourceCamera = camera;
            sender.sourceTexture = null;
            sender.videoSourceBehaviour = null;
            sender.resolutionScale = 0.6f;
            sender.targetFps = 30;
            sender.jpegQuality = 65;
            sender.transportKind = TransportKind.Relay;
            sender.relayUrl = "ws://127.0.0.1:8787/fxm-relay";
            sender.relayRoom = "demo";
            sender.relayToken = "";
            sender.allowRemoteControl = true;
            sender.autoStart = true;
            sender.autoPlay = true;
            sender.showStatistics = true;
            EditorUtility.SetDirty(sender);

            FinishUndo(undoGroup, camera.gameObject);

            Debug.Log("[FXM.WebStream] Relay sender ready. Set Relay Url, Room, and Token, then build or press Play. " +
                      "Both sender and receiver must use the same room.");
        }

        [MenuItem("Tools/FXM WebStream/Add Webcam Sender (Relay)")]
        public static void AddWebcamSenderSetup()
        {
            if (!Confirm("Add a webcam relay sender? Useful for Android or desktop camera streaming."))
            {
                return;
            }

            int undoGroup = BeginUndo("FXM Webcam Sender Setup");

            var sourceObject = new GameObject("FXM Webcam Source");
            Undo.RegisterCreatedObjectUndo(sourceObject, "Create Webcam Source");
            var source = Undo.AddComponent<WebCamStreamSource>(sourceObject);
            source.requestedWidth = 1280;
            source.requestedHeight = 720;
            source.requestedFps = 30;
            EditorUtility.SetDirty(source);

            var senderObject = new GameObject("FXM Webcam Sender");
            Undo.RegisterCreatedObjectUndo(senderObject, "Create Webcam Sender");
            var sender = Undo.AddComponent<StreamingSender>(senderObject);
            sender.sourceCamera = null;
            sender.sourceTexture = null;
            sender.videoSourceBehaviour = source;
            sender.resolutionScale = 0.75f;
            sender.targetFps = 30;
            sender.jpegQuality = 65;
            sender.transportKind = TransportKind.Relay;
            sender.relayUrl = "ws://127.0.0.1:8787/fxm-relay";
            sender.relayRoom = "demo";
            sender.relayToken = "";
            sender.allowRemoteControl = true;
            sender.autoStart = true;
            sender.autoPlay = true;
            sender.showStatistics = true;
            EditorUtility.SetDirty(sender);

            FinishUndo(undoGroup, senderObject);
            Selection.activeGameObject = senderObject;

            Debug.Log("[FXM.WebStream] Webcam sender ready. On Android, allow camera permission on first launch. " +
                      "Set the same Relay Url, Room, and Token on the receiver.");
        }

        [MenuItem("Tools/FXM WebStream/Add Relay Receiver (Internet)")]
        public static void AddRelayReceiverSetup()
        {
            if (!Confirm("Add an internet relay receiver? Both peers will connect as clients to a relay server."))
            {
                return;
            }

            int undoGroup = BeginUndo("FXM Relay Receiver Setup");

            Canvas canvas = GetOrCreateCanvas();
            RawImage view = CreateView(canvas, "FXM Relay View",
                Vector2.zero, Vector2.one, new Vector2(20f, 76f), new Vector2(-20f, -20f));

            var receiverObject = new GameObject("FXM Relay Receiver");
            Undo.RegisterCreatedObjectUndo(receiverObject, "Create Relay Receiver");
            var receiver = Undo.AddComponent<StreamingReceiver>(receiverObject);
            Undo.RecordObject(receiver, "Configure Relay Receiver");
            receiver.transportKind = TransportKind.Relay;
            receiver.relayUrl = "ws://127.0.0.1:8787/fxm-relay";
            receiver.relayRoom = "demo";
            receiver.relayToken = "";
            receiver.targetRawImage = view;
            receiver.autoStart = true;
            receiver.allowRemoteCommands = true;
            receiver.showStatistics = true;
            EditorUtility.SetDirty(receiver);

            CreatePlaybackBar(canvas, receiver);
            FinishUndo(undoGroup, receiverObject);
            Selection.activeGameObject = receiverObject;

            Debug.Log("[FXM.WebStream] Relay receiver ready. Set the same Relay Url, Room, and Token as the sender, " +
                      "then build to any platform (Windows, macOS, Android, iOS, or WebGL).");
        }

        [MenuItem("Tools/FXM WebStream/Add One-to-Many Setup")]
        public static void AddOneToManySetup()
        {
            if (!Confirm("Add a one-to-many demo (1 UDP sender -> 3 receivers)?"))
            {
                return;
            }

            int undoGroup = BeginUndo("FXM One To Many Setup");

            Camera camera = GetOrCreateCamera(new Vector3(0f, 2f, -8f), new Vector3(0f, 1f, 0f));
            CreateTestContent(new Vector3(0f, 1f, 0f));
            Canvas canvas = GetOrCreateCanvas();

            const int receiverCount = 3;

            var sender = GetOrAdd<StreamingSender>(camera.gameObject);
            Undo.RecordObject(sender, "Configure Sender");
            sender.sourceCamera = camera;
            sender.sourceTexture = null;
            sender.videoSourceBehaviour = null;
            sender.resolutionScale = 0.5f;
            sender.transportKind = TransportKind.Udp;
            sender.remoteHost = "127.0.0.1";
            sender.remotePort = Port;
            sender.localControlPort = ControlPort;
            sender.allowRemoteControl = true;
            sender.autoStart = true;
            sender.autoPlay = true;

            var receivers = new string[receiverCount];
            for (int i = 0; i < receiverCount; i++)
            {
                receivers[i] = "127.0.0.1:" + (Port + i);
            }

            sender.udpReceivers = receivers;
            EditorUtility.SetDirty(sender);

            StreamingReceiver firstReceiver = null;

            for (int i = 0; i < receiverCount; i++)
            {
                float left = i / (float)receiverCount;
                float right = (i + 1) / (float)receiverCount;

                RawImage view = CreateView(canvas, "FXM View " + i,
                    new Vector2(left, 0f), new Vector2(right, 0.82f),
                    new Vector2(8f, 8f), new Vector2(-8f, -8f));

                var receiverObject = new GameObject("FXM Receiver " + i);
                Undo.RegisterCreatedObjectUndo(receiverObject, "Create Receiver " + i);
                var receiver = Undo.AddComponent<StreamingReceiver>(receiverObject);
                receiver.transportKind = TransportKind.Udp;
                receiver.listenPort = Port + i;
                receiver.senderHost = "127.0.0.1";
                receiver.senderControlPort = ControlPort;
                receiver.targetRawImage = view;
                receiver.autoStart = true;
                receiver.showStatistics = true;
                EditorUtility.SetDirty(receiver);

                if (firstReceiver == null)
                {
                    firstReceiver = receiver;
                }
            }

            CreatePlaybackBar(canvas, firstReceiver);
            FinishUndo(undoGroup, firstReceiver != null ? firstReceiver.gameObject : camera.gameObject);

            Debug.Log("[FXM.WebStream] One-to-many demo ready: the sender targets ports " +
                      Port + "-" + (Port + receiverCount - 1) + ".");
        }

        [MenuItem("Tools/FXM WebStream/Add Many-to-One Setup")]
        public static void AddManyToOneSetup()
        {
            if (!Confirm("Add a many-to-one demo (3 UDP senders -> 1 MultiStreamReceiver)?"))
            {
                return;
            }

            int undoGroup = BeginUndo("FXM Many To One Setup");

            Canvas canvas = GetOrCreateCanvas();

            const int senderCount = 3;
            Color[] colors =
            {
                new Color(0.9f, 0.3f, 0.3f),
                new Color(0.3f, 0.8f, 0.4f),
                new Color(0.3f, 0.5f, 0.95f)
            };

            for (int i = 0; i < senderCount; i++)
            {
                float x = (i - 1) * 4f;
                var cameraObject = new GameObject("FXM Source Camera " + i, typeof(Camera));
                Undo.RegisterCreatedObjectUndo(cameraObject, "Create Source Camera " + i);
                cameraObject.transform.position = new Vector3(x, 1.5f, -6f);
                cameraObject.tag = "MainCamera";

                var camera = cameraObject.GetComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.08f, 0.08f, 0.12f);
                camera.fieldOfView = 55f;

                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "FXM Source Cube " + i;
                cube.transform.position = new Vector3(x, 1f, 0f);
                cube.transform.localScale = Vector3.one * 1.6f;
                cube.GetComponent<Renderer>().material.color = colors[i % colors.Length];
                Undo.RegisterCreatedObjectUndo(cube, "Create Source Cube " + i);
                Undo.AddComponent<DemoSpinner>(cube);

                var sender = Undo.AddComponent<StreamingSender>(cameraObject);
                sender.sourceCamera = camera;
                sender.resolutionScale = 0.5f;
                sender.targetFps = 24;
                sender.jpegQuality = 60;
                sender.transportKind = TransportKind.Udp;
                sender.remoteHost = "127.0.0.1";
                sender.remotePort = Port + i;
                sender.localControlPort = ControlPort + i;
                sender.allowRemoteControl = true;
                sender.autoStart = true;
                sender.autoPlay = true;
                EditorUtility.SetDirty(sender);

                if (Object.FindAnyObjectByType<Light>() == null)
                {
                    var lightObject = new GameObject("Directional Light", typeof(Light));
                    Undo.RegisterCreatedObjectUndo(lightObject, "Create Light");
                    var light = lightObject.GetComponent<Light>();
                    light.type = LightType.Directional;
                    light.intensity = 1.1f;
                    lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                }
            }

            var views = new RawImage[senderCount];
            var sources = new StreamSourceConfig[senderCount];

            for (int i = 0; i < senderCount; i++)
            {
                float left = i / (float)senderCount;
                float right = (i + 1) / (float)senderCount;

                views[i] = CreateView(canvas, "FXM Multi View " + i,
                    new Vector2(left, 0f), new Vector2(right, 0.82f),
                    new Vector2(8f, 8f), new Vector2(-8f, -8f));

                sources[i] = new StreamSourceConfig
                {
                    name = "Source " + i,
                    transportKind = TransportKind.Udp,
                    host = "127.0.0.1",
                    localPort = Port + i,
                    senderControlPort = ControlPort + i,
                    webSocketUrl = "ws://127.0.0.1:47778/fxm-stream/",
                    mtu = 1200
                };
            }

            var receiverObject = new GameObject("FXM Multi Receiver");
            Undo.RegisterCreatedObjectUndo(receiverObject, "Create Multi Receiver");
            var multi = Undo.AddComponent<MultiStreamReceiver>(receiverObject);
            multi.sources = sources;
            multi.views = views;
            multi.showAllSources = true;
            multi.maxDecodeFps = 30;
            multi.autoStart = true;
            multi.showStatistics = true;
            EditorUtility.SetDirty(multi);

            CreatePlaybackBar(canvas, multi);
            FinishUndo(undoGroup, receiverObject);
            Selection.activeGameObject = receiverObject;

            Debug.Log("[FXM.WebStream] Many-to-one demo ready: 3 senders -> 3 ports -> 1 MultiStreamReceiver.");
        }

        [MenuItem("Tools/FXM WebStream/Add Setup UI (Start / Watch)")]
        public static void AddSetupUi()
        {
            var sceneSender = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponentInParent<StreamingSender>()
                : null;
            if (sceneSender == null)
            {
                sceneSender = Object.FindAnyObjectByType<StreamingSender>(FindObjectsInactive.Include);
            }

            var sceneReceiver = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponentInParent<StreamingReceiver>()
                : null;
            if (sceneReceiver == null)
            {
                sceneReceiver = Object.FindAnyObjectByType<StreamingReceiver>(FindObjectsInactive.Include);
            }

            if (sceneSender == null && sceneReceiver == null)
            {
                EditorUtility.DisplayDialog("FXM WebStream",
                    "This scene has no StreamingSender or StreamingReceiver. Add one from the " +
                    "Tools > FXM WebStream menu first.", "OK");
                return;
            }

            var existing = Object.FindAnyObjectByType<StreamSetupPanel>(FindObjectsInactive.Include);
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                Debug.Log("[FXM.WebStream] This scene already has a setup UI.");
                return;
            }

            if (!Confirm("Add a start screen that configures the stream before Start/Watch?"))
            {
                return;
            }

            int undoGroup = BeginUndo("FXM Setup UI");

            var panelObject = new GameObject("FXM Setup UI");
            Undo.RegisterCreatedObjectUndo(panelObject, "Create Setup UI");
            var panel = Undo.AddComponent<StreamSetupPanel>(panelObject);

            panel.sender = sceneSender;
            panel.receiver = sceneReceiver;
            panel.sceneCamera = Camera.main != null ? Camera.main : Object.FindAnyObjectByType<Camera>();
            panel.videoSource = Object.FindAnyObjectByType<VideoPlayerStreamSource>(FindObjectsInactive.Include);
            panel.webcamSource = Object.FindAnyObjectByType<WebCamStreamSource>(FindObjectsInactive.Include);
            panel.clips = LoadAllVideoClips();

            if (panel.sender != null)
            {
                Undo.RecordObject(panel.sender, "Disable sender auto start");
                panel.sender.autoStart = false;
                EditorUtility.SetDirty(panel.sender);
            }

            if (panel.receiver != null)
            {
                Undo.RecordObject(panel.receiver, "Disable receiver auto start");
                panel.receiver.autoStart = false;
                EditorUtility.SetDirty(panel.receiver);
            }

            EditorUtility.SetDirty(panel);
            FinishUndo(undoGroup, panelObject);

            Debug.Log("[FXM.WebStream] Setup UI added. Press Play, choose the connection and video source, " +
                      "then press Start streaming or Watch stream.");
        }

        private static VideoClip[] LoadAllVideoClips()
        {
            string[] guids = AssetDatabase.FindAssets("t:VideoClip");
            var result = new List<VideoClip>();

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var clip = AssetDatabase.LoadAssetAtPath<VideoClip>(path);
                if (clip != null)
                {
                    result.Add(clip);
                }
            }

            return result.ToArray();
        }

        // ------------------------------------------------------------------
        // Helper scene
        // ------------------------------------------------------------------

        private static bool Confirm(string message)
        {
            return EditorUtility.DisplayDialog("FXM WebStream", message, "Add", "Cancel");
        }

        private static int BeginUndo(string name)
        {
            Undo.SetCurrentGroupName(name);
            return Undo.GetCurrentGroup();
        }

        private static void FinishUndo(int undoGroup, GameObject selected)
        {
            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            if (selected != null)
            {
                Selection.activeGameObject = selected;
            }
        }

        private static Camera GetOrCreateCamera(Vector3 position, Vector3 lookAt)
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                camera = Object.FindAnyObjectByType<Camera>();
            }

            if (camera != null)
            {
                return camera;
            }

            var cameraObject = new GameObject("Main Camera", typeof(Camera));
            Undo.RegisterCreatedObjectUndo(cameraObject, "Create Main Camera");
            cameraObject.tag = "MainCamera";
            camera = cameraObject.GetComponent<Camera>();
            camera.transform.position = position;
            camera.transform.LookAt(lookAt);

            if (Object.FindAnyObjectByType<AudioListener>() == null)
            {
                Undo.AddComponent<AudioListener>(cameraObject);
            }

            return camera;
        }

        private static void CreateTestContent(Vector3 position)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "FXM Test Cube";
            cube.transform.position = position;
            cube.transform.localScale = new Vector3(1.5f, 1.5f, 1.5f);
            Undo.RegisterCreatedObjectUndo(cube, "Create Test Cube");
            Undo.AddComponent<DemoSpinner>(cube);

            if (Object.FindAnyObjectByType<Light>() == null)
            {
                var lightObject = new GameObject("Directional Light", typeof(Light));
                Undo.RegisterCreatedObjectUndo(lightObject, "Create Light");
                var light = lightObject.GetComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                light.shadows = LightShadows.Soft;
                lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }
        }

        private static Canvas GetOrCreateCanvas()
        {
            Canvas canvas = Object.FindAnyObjectByType<Canvas>();
            if (canvas != null)
            {
                return canvas;
            }

            var canvasObject = new GameObject("FXM Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Undo.RegisterCreatedObjectUndo(canvasObject, "Create Canvas");
            canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        private static RawImage CreateView(Canvas canvas, string name,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var viewObject = new GameObject(name, typeof(RectTransform), typeof(RawImage));
            Undo.RegisterCreatedObjectUndo(viewObject, "Create " + name);
            viewObject.transform.SetParent(canvas.transform, false);

            var image = viewObject.GetComponent<RawImage>();
            image.color = Color.white;
            image.raycastTarget = false;

            var rect = image.rectTransform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            return image;
        }

        private static StreamPlaybackControls CreatePlaybackBar(Canvas canvas, IStreamPlaybackController controller)
        {
            var barObject = new GameObject("FXM Playback Controls", typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(barObject, "Create Playback Controls");
            barObject.transform.SetParent(canvas.transform, false);

            var rect = barObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0f, 0f);
            rect.anchoredPosition = new Vector2(24f, 18f);
            rect.sizeDelta = new Vector2(760f, 44f);

            var controls = Undo.AddComponent<StreamPlaybackControls>(barObject);
            controls.controllerBehaviour = controller as MonoBehaviour;

            float x = 0f;
            controls.playButton = CreateButton(barObject.transform, "Play", new Vector2(x, 0f));
            x += 96f;
            controls.pauseButton = CreateButton(barObject.transform, "Pause", new Vector2(x, 0f));
            x += 96f;
            controls.stopButton = CreateButton(barObject.transform, "Stop", new Vector2(x, 0f));
            x += 96f;
            controls.restartButton = CreateButton(barObject.transform, "Restart", new Vector2(x, 0f));
            x += 108f;

            controls.seekSlider = CreateSlider(barObject.transform, new Vector2(x, 0f), new Vector2(220f, 26f));
            x += 232f;

            controls.statusLabel = CreateText(barObject.transform, "Status", "Stopped",
                new Vector2(x, 2f), new Vector2(120f, 20f), 16, TextAnchor.MiddleLeft);
            controls.timeLabel = CreateText(barObject.transform, "Time", "00:00 / LIVE",
                new Vector2(x, 20f), new Vector2(180f, 20f), 14, TextAnchor.MiddleLeft);

            return controls;
        }

        private static Button CreateButton(Transform parent, string label, Vector2 position)
        {
            var buttonObject = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button));
            Undo.RegisterCreatedObjectUndo(buttonObject, "Create " + label + " Button");
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0f, 0f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(88f, 40f);

            var image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.13f, 0.14f, 0.18f, 0.92f);

            var button = buttonObject.GetComponent<Button>();
            button.targetGraphic = image;

            CreateText(buttonObject.transform, "Label", label, new Vector2(0f, 0f),
                new Vector2(88f, 40f), 16, TextAnchor.MiddleCenter).color = Color.white;

            return button;
        }

        private static Slider CreateSlider(Transform parent, Vector2 position, Vector2 size)
        {
            var sliderObject = new GameObject("Seek Slider", typeof(RectTransform), typeof(Slider));
            Undo.RegisterCreatedObjectUndo(sliderObject, "Create Seek Slider");
            sliderObject.transform.SetParent(parent, false);

            var rect = sliderObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0f, 0f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            var slider = sliderObject.GetComponent<Slider>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;

            var background = CreateUiImage(sliderObject.transform, "Background",
                new Vector2(0f, 0.3f), new Vector2(1f, 0.7f), Vector2.zero, Vector2.zero,
                new Color(0.2f, 0.2f, 0.24f, 1f));

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(sliderObject.transform, false);
            var fillAreaRect = fillArea.GetComponent<RectTransform>();
            fillAreaRect.anchorMin = new Vector2(0f, 0.3f);
            fillAreaRect.anchorMax = new Vector2(1f, 0.7f);
            fillAreaRect.offsetMin = new Vector2(4f, 0f);
            fillAreaRect.offsetMax = new Vector2(-4f, 0f);

            var fill = CreateUiImage(fillArea.transform, "Fill",
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
                new Color(0.25f, 0.7f, 1f, 1f));

            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(sliderObject.transform, false);
            var handleAreaRect = handleArea.GetComponent<RectTransform>();
            handleAreaRect.anchorMin = Vector2.zero;
            handleAreaRect.anchorMax = Vector2.one;
            handleAreaRect.offsetMin = new Vector2(8f, 0f);
            handleAreaRect.offsetMax = new Vector2(-8f, 0f);

            var handle = CreateUiImage(handleArea.transform, "Handle",
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, Vector2.zero,
                Color.white);
            var handleRect = handle.rectTransform;
            handleRect.sizeDelta = new Vector2(12f, 24f);

            slider.fillRect = fill.rectTransform;
            slider.handleRect = handleRect;
            slider.targetGraphic = handle;
            slider.value = 0f;

            // The background must receive raycasts so clicking the track moves the value.
            background.raycastTarget = true;
            return slider;
        }

        private static Image CreateUiImage(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
        {
            var imageObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            imageObject.transform.SetParent(parent, false);
            var image = imageObject.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;

            var rect = image.rectTransform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            return image;
        }

        private static Text CreateText(Transform parent, string name, string value,
            Vector2 position, Vector2 size, int fontSize, TextAnchor alignment)
        {
            var textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);

            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0f, 0f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            var text = textObject.GetComponent<Text>();
            text.text = value;
            text.font = GetBuiltinFont();
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = new Color(0.9f, 0.92f, 0.96f, 1f);
            text.raycastTarget = false;
            return text;
        }

        private static Font GetBuiltinFont()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
            {
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }

            return font;
        }

        private static T GetOrAdd<T>(GameObject target) where T : Component
        {
            T component = target.GetComponent<T>();
            return component != null ? component : Undo.AddComponent<T>(target);
        }
    }
}
