using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Video;

namespace FXM.WebStream
{
    /// <summary>
    /// Start screen for a stream. The panel configures the transport, relay room/token,
    /// video input, and quality before anything is sent or received.
    ///
    /// Add it with <b>Tools &gt; FXM WebStream &gt; Add Setup UI (Start / Watch)</b>.
    /// It disables auto-start on the sender and receiver, then starts them only when the
    /// user presses Start or Watch.
    ///
    /// The panel uses IMGUI so it works without a Canvas, EventSystem, or input module,
    /// including on WebGL and Android.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    [AddComponentMenu("FXM WebStream/Stream Setup Panel")]
    public sealed class StreamSetupPanel : MonoBehaviour
    {
        public enum SetupMode
        {
            Stream,
            Watch,
            Multi
        }

        public enum VideoInputMode
        {
            Camera,
            Clip,
            Url,
            Webcam
        }

        [Header("Scene References (auto-detected when empty)")]
        [Tooltip("Sender used in Stream mode. Found automatically in the scene when empty.")]
        public StreamingSender sender;

        [Tooltip("Receiver used in Watch mode. Found automatically in the scene when empty.")]
        public StreamingReceiver receiver;

        [Tooltip("Multi-source receiver used in Watch many mode. Added at runtime when empty.")]
        public MultiStreamReceiver multiReceiver;

        [Tooltip("Optional VideoPlayer source used for Clip and URL input.")]
        public VideoPlayerStreamSource videoSource;

        [Tooltip("Optional webcam source used for Webcam input.")]
        public WebCamStreamSource webcamSource;

        [Tooltip("Camera used for Scene camera input. Falls back to Camera.main.")]
        public Camera sceneCamera;

        [Tooltip("Clips offered in the Video clip list. Assign the videos you want in the Inspector.")]
        public VideoClip[] clips = new VideoClip[0];

        [Tooltip("Optional local video file for Clip mode. The panel's file browser fills this in.")]
        public string localClipPath = string.Empty;

        [Header("Many-source watch")]
        [Tooltip("Relay rooms shown in Watch many mode. Each sender needs its own room.")]
        public string multiRooms = "demo2, demo3";

        [Tooltip("WebSocket URLs shown in Watch many mode, one per sender.")]
        public string multiUrls = string.Empty;

        [Tooltip("Show every source in a grid. Disable to show only the selected source.")]
        public bool multiShowGrid = true;

        [Tooltip("Play audio from the selected source only.")]
        public bool multiPlayAudio = true;

        [Header("Fullscreen viewer")]
        [Tooltip("Hide the panel and fill the screen as soon as watching starts.")]
        public bool fullscreenOnStart = true;

        [Tooltip("Fill the screen by cropping the edges instead of adding black bars. Off by default so the video is never zoomed.")]
        public bool fullscreenFill = false;

        [Tooltip("Seconds the fullscreen controls stay visible after the last tap or mouse move.")]
        [Range(1f, 12f)]
        public float fullscreenControlsTimeout = 3.5f;

        [Header("Audio")]
        [Tooltip("Capture the scene audio mix while streaming.")]
        public bool streamAudio = true;

        [Range(StreamAudioProtocol.MinSampleRate, StreamAudioProtocol.MaxSampleRate)]
        public int audioSampleRate = 24000;

        [Tooltip("Send stereo instead of mono. Doubles audio bandwidth.")]
        public bool audioStereo;

        [Tooltip("Play audio in Watch mode.")]
        public bool playAudio = true;

        [Header("Panel")]
        [Tooltip("Remember the last URL, room, and quality settings between runs. The token is never saved.")]
        public bool rememberSettings = true;

        [Tooltip("Show the live preview next to the settings.")]
        public bool showPreview = true;

        [Tooltip("Show the panel when the scene starts. Turn it off to open it with the toggle key.")]
        public bool visibleOnStart = true;

        [Tooltip("Key that shows or hides the panel.")]
        public KeyCode toggleKey = KeyCode.F1;

        [Tooltip("Show an Exit button that stops the stream and quits the application.")]
        public bool allowExit = true;

        [Tooltip("On phones, follow the device orientation automatically (portrait or landscape).")]
        public bool autoRotateOnMobile = true;

        private const string PrefsPrefix = "FXM.Setup.";
        private const string DefaultRelayUrl = "ws://127.0.0.1:8787/fxm-relay";
        private const string DefaultWebSocketUrl = "ws://127.0.0.1:47778/fxm-stream/";

        private static readonly string[] ModeLabels = { "Stream", "Watch", "Many" };
        private static readonly string[] TransportLabels = { "Relay", "WebSocket", "UDP" };
        private static readonly string[] InputLabels = { "Camera", "Clip", "URL", "Webcam" };
        private static readonly string[] AudioRateLabels = { "Voice 16k", "Balanced 24k", "Music 48k" };
        private static readonly Color PanelBackdrop = new Color(0.071f, 0.082f, 0.102f, 1f);
        private static readonly Color PanelSurface = new Color(0.094f, 0.110f, 0.137f, 1f);
        private static readonly Color FieldBackground = new Color(0.047f, 0.055f, 0.071f, 1f);
        private static readonly Color FieldFocused = new Color(0.063f, 0.075f, 0.094f, 1f);
        private static readonly Color ControlBackground = new Color(0.129f, 0.153f, 0.192f, 1f);
        private static readonly Color ControlHover = new Color(0.161f, 0.192f, 0.239f, 1f);
        private static readonly Color ControlActive = new Color(0.196f, 0.235f, 0.290f, 1f);
        private static readonly Color AccentColor = new Color(0.153f, 0.514f, 0.612f, 1f);
        private static readonly Color AccentHover = new Color(0.196f, 0.627f, 0.729f, 1f);
        private static readonly Color AccentActive = new Color(0.118f, 0.412f, 0.502f, 1f);
        private static readonly Color AccentText = new Color(0.541f, 0.804f, 0.867f, 1f);
        private static readonly Color TextPrimary = new Color(0.918f, 0.937f, 0.961f, 1f);
        private static readonly Color TextSecondary = new Color(0.667f, 0.708f, 0.761f, 1f);
        private static readonly Color TextMuted = new Color(0.478f, 0.529f, 0.596f, 1f);
        private static readonly Color SuccessColor = new Color(0.416f, 0.855f, 0.596f, 1f);
        private static readonly Color WarningColor = new Color(0.949f, 0.729f, 0.353f, 1f);
        private static readonly Color DangerColor = new Color(0.949f, 0.435f, 0.435f, 1f);
        private static readonly Color DividerColor = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color BorderColor = new Color(1f, 1f, 1f, 0.09f);

        private SetupMode _mode = SetupMode.Stream;
        private VideoInputMode _videoInput = VideoInputMode.Camera;
        private int _transportIndex;
        private int _clipIndex;
        private string _relayUrl = DefaultRelayUrl;
        private string _relayRoom = "demo";
        private string _relayToken = string.Empty;
        private string _webSocketUrl = DefaultWebSocketUrl;
        private string _udpHost = "127.0.0.1";
        private string _videoUrl = string.Empty;
        private int _udpPort = 47777;
        private int _controlPort = 47800;
        private int _webSocketPort = 47778;
        private float _resolutionScale = 0.6f;
        private int _targetFps = 30;
        private int _jpegQuality = 70;
        private int _decodeFps = 30;

        private bool _hidden;
        private bool _exitArmed;
        private float _exitArmedUntil;
        private float _startedAt;
        private string _status = string.Empty;
        private bool _statusIsError;
        private Vector2 _contentScroll;

        private bool _fileBrowserOpen;
        private string _browsePath = string.Empty;
        private string _browseStatus = string.Empty;
        private Vector2 _browseScroll;
        private readonly List<string> _browseFolders = new List<string>();
        private readonly List<string> _browseFiles = new List<string>();

        private GUIStyle _titleStyle;
        private GUIStyle _sectionStyle;
        private GUIStyle _smallStyle;
        private GUIStyle _hintStyle;
        private GUIStyle _fieldLabelStyle;
        private GUIStyle _valueStyle;
        private GUIStyle _centerStyle;
        private GUIStyle _footerStatusStyle;
        private GUIStyle _primaryButtonStyle;
        private GUIStyle _secondaryButtonStyle;
        private GUIStyle _dangerButtonStyle;
        private GUIStyle _fieldStyle;
        private GUIStyle _textAreaStyle;
        private GUIStyle _tabStyle;
        private GUIStyle _selectionGridStyle;
        private GUIStyle _overlayLabelStyle;
        private GUIStyle _overlaySmallStyle;
        private GUIStyle _overlayButtonStyle;
        private GUIStyle _fullscreenMessageStyle;
        private GUISkin _darkSkin;
        private List<Texture2D> _themeTextures;
        private bool _stylesReady;

        private float _contentWidth = 620f;
        private float _uiScale = 1f;
        private float _scaleDpi = -1f;
        private int _scaleScreenWidth;
        private int _scaleScreenHeight;
        private Rect _safeAreaGui;
        private Rect _safePixels;
        private bool _hasSafePixels;
        private float _safeAreaScale = -1f;
        private float _controlsUntil;
        private bool _immersiveApplied;

        private GameObject _ownedVideoObject;
        private GameObject _ownedWebcamObject;

        /// <summary>Show or hide the setup panel from gameplay code.</summary>
        public void SetPanelHidden(bool hidden)
        {
            _hidden = hidden;
            _fileBrowserOpen = false;
            _controlsUntil = Time.unscaledTime + fullscreenControlsTimeout;
        }

        /// <summary>Current role shown by the panel.</summary>
        public SetupMode Mode
        {
            get { return _mode; }
            set { _mode = value; }
        }

        /// <summary>0 = Relay, 1 = WebSocket, 2 = UDP.</summary>
        public int TransportOption
        {
            get { return _transportIndex; }
            set
            {
                int max = Application.platform == RuntimePlatform.WebGLPlayer ? 1 : 2;
                _transportIndex = Mathf.Clamp(value, 0, max);
            }
        }

        public VideoInputMode VideoInput
        {
            get { return _videoInput; }
            set { _videoInput = value; }
        }

        public string RelayUrl
        {
            get { return _relayUrl; }
            set { _relayUrl = value; }
        }

        /// <summary>Direct WebSocket URL used when <see cref="TransportOption"/> is 1.</summary>
        public string WebSocketUrl
        {
            get { return _webSocketUrl; }
            set { _webSocketUrl = value; }
        }

        public string RelayRoom
        {
            get { return _relayRoom; }
            set { _relayRoom = value; }
        }

        public string VideoUrl
        {
            get { return _videoUrl; }
            set { _videoUrl = value; }
        }

        public bool IsPanelHidden { get { return _hidden; } }

        /// <summary>True while the selected role is actually running.</summary>
        public bool IsStarted
        {
            get
            {
                if (_mode == SetupMode.Stream)
                {
                    return sender != null && sender.IsStreaming;
                }

                if (_mode == SetupMode.Multi)
                {
                    return multiReceiver != null && multiReceiver.IsReceiving;
                }

                return receiver != null && receiver.IsReceiving;
            }
        }

        /// <summary>Rooms or URLs currently used by Watch many, for tests and tooling.</summary>
        public string[] MultiSourceList
        {
            get { return ParseMultiSources(); }
        }

        private void Awake()
        {
            ResolveReferences();
            LoadSettings();
            DisableAutoStart();
            ApplyMobileOrientation();
            _hidden = !visibleOnStart;
        }

        private void ApplyMobileOrientation()
        {
            if (!autoRotateOnMobile || !Application.isMobilePlatform)
            {
                return;
            }

            Screen.orientation = ScreenOrientation.AutoRotation;
            Screen.autorotateToPortrait = true;
            Screen.autorotateToPortraitUpsideDown = true;
            Screen.autorotateToLandscapeLeft = true;
            Screen.autorotateToLandscapeRight = true;
        }

        private void Update()
        {
            if (_exitArmed && Time.unscaledTime > _exitArmedUntil)
            {
                _exitArmed = false;
            }
        }

        private void OnDisable()
        {
            Cursor.visible = true;
            SetAndroidImmersive(false);
        }

        private void OnDestroy()
        {
            Cursor.visible = true;
            SetAndroidImmersive(false);

            if (sender != null && sender.IsStreaming)
            {
                sender.StopStreaming();
            }

            if (receiver != null && receiver.IsReceiving)
            {
                receiver.StopReceiving();
            }

            if (multiReceiver != null && multiReceiver.IsReceiving)
            {
                multiReceiver.StopReceiving();
            }

            if (_ownedVideoObject != null)
            {
                Destroy(_ownedVideoObject);
            }

            if (_ownedWebcamObject != null)
            {
                Destroy(_ownedWebcamObject);
            }

            if (_darkSkin != null)
            {
                Destroy(_darkSkin);
                _darkSkin = null;
            }

            if (_themeTextures != null)
            {
                for (int i = 0; i < _themeTextures.Count; i++)
                {
                    if (_themeTextures[i] != null)
                    {
                        Destroy(_themeTextures[i]);
                    }
                }

                _themeTextures = null;
            }

            _stylesReady = false;
        }

        // ------------------------------------------------------------------
        // Setup
        // ------------------------------------------------------------------

        private void ResolveReferences()
        {
            if (sender == null)
            {
                sender = FindAnyObjectByType<StreamingSender>(FindObjectsInactive.Include);
            }

            if (receiver == null)
            {
                var receivers = FindObjectsByType<StreamingReceiver>(FindObjectsInactive.Include);
                for (int i = 0; i < receivers.Length; i++)
                {
                    if (receivers[i].targetRawImage != null)
                    {
                        receiver = receivers[i];
                        break;
                    }
                }

                if (receiver == null && receivers.Length > 0)
                {
                    receiver = receivers[0];
                }
            }

            if (sceneCamera == null)
            {
                sceneCamera = Camera.main;
            }

            if (videoSource == null && sender != null)
            {
                videoSource = sender.videoSourceBehaviour as VideoPlayerStreamSource;
            }

            if (videoSource == null)
            {
                videoSource = FindAnyObjectByType<VideoPlayerStreamSource>(FindObjectsInactive.Include);
            }

            if (webcamSource == null)
            {
                webcamSource = FindAnyObjectByType<WebCamStreamSource>(FindObjectsInactive.Include);
            }

            if (multiReceiver == null)
            {
                multiReceiver = FindAnyObjectByType<MultiStreamReceiver>(FindObjectsInactive.Include);
            }
        }

        private void DisableAutoStart()
        {
            var senders = FindObjectsByType<StreamingSender>(FindObjectsInactive.Include);
            for (int i = 0; i < senders.Length; i++)
            {
                senders[i].autoStart = false;
                if (senders[i].IsStreaming)
                {
                    senders[i].StopStreaming();
                }
            }

            var receivers = FindObjectsByType<StreamingReceiver>(FindObjectsInactive.Include);
            for (int i = 0; i < receivers.Length; i++)
            {
                receivers[i].autoStart = false;
                if (receivers[i].IsReceiving)
                {
                    receivers[i].StopReceiving();
                }
            }

            var multiReceivers = FindObjectsByType<MultiStreamReceiver>(FindObjectsInactive.Include);
            for (int i = 0; i < multiReceivers.Length; i++)
            {
                multiReceivers[i].autoStart = false;
                if (multiReceivers[i].IsReceiving)
                {
                    multiReceivers[i].StopReceiving();
                }
            }
        }

        private void LoadSettings()
        {
            if (sender != null)
            {
                _relayUrl = string.IsNullOrEmpty(sender.relayUrl) ? DefaultRelayUrl : sender.relayUrl;
                _relayRoom = sender.relayRoom;
                _relayToken = sender.relayToken;
                _udpHost = sender.remoteHost;
                _udpPort = sender.remotePort;
                _controlPort = sender.localControlPort;
                _resolutionScale = sender.resolutionScale;
                _targetFps = sender.targetFps;
                _jpegQuality = sender.jpegQuality;

                var config = TransportConfig.ParseWebSocketUrl(sender.webSocketUrl);
                if (config.RemotePort > 0)
                {
                    _webSocketPort = config.RemotePort;
                }

                _transportIndex = ToTransportOption(sender.transportKind);
            }
            else if (receiver != null)
            {
                _relayUrl = string.IsNullOrEmpty(receiver.relayUrl) ? DefaultRelayUrl : receiver.relayUrl;
                _relayRoom = receiver.relayRoom;
                _relayToken = receiver.relayToken;
                _udpHost = receiver.senderHost;
                _udpPort = receiver.listenPort;
                _controlPort = receiver.senderControlPort;
                _webSocketUrl = receiver.webSocketUrl;
                _decodeFps = receiver.maxDecodeFps;
                _transportIndex = ToTransportOption(receiver.transportKind);
            }

            if (receiver != null)
            {
                _webSocketUrl = receiver.webSocketUrl;
                _decodeFps = receiver.maxDecodeFps;
            }

            if (videoSource != null && !string.IsNullOrEmpty(videoSource.url))
            {
                _videoInput = VideoInputMode.Url;
                _videoUrl = videoSource.url;
            }

            if (!rememberSettings)
            {
                return;
            }

            _mode = (SetupMode)PlayerPrefs.GetInt(PrefsPrefix + "Mode", (int)_mode);
            _transportIndex = PlayerPrefs.GetInt(PrefsPrefix + "Transport", _transportIndex);
            _relayUrl = PlayerPrefs.GetString(PrefsPrefix + "RelayUrl", _relayUrl);
            _relayRoom = PlayerPrefs.GetString(PrefsPrefix + "Room", _relayRoom);
            _webSocketUrl = PlayerPrefs.GetString(PrefsPrefix + "WebSocketUrl", _webSocketUrl);
            _udpHost = PlayerPrefs.GetString(PrefsPrefix + "UdpHost", _udpHost);
            _udpPort = PlayerPrefs.GetInt(PrefsPrefix + "UdpPort", _udpPort);
            _controlPort = PlayerPrefs.GetInt(PrefsPrefix + "ControlPort", _controlPort);
            _webSocketPort = PlayerPrefs.GetInt(PrefsPrefix + "WebSocketPort", _webSocketPort);
            _videoInput = (VideoInputMode)PlayerPrefs.GetInt(PrefsPrefix + "VideoInput", (int)_videoInput);
            _videoUrl = PlayerPrefs.GetString(PrefsPrefix + "VideoUrl", _videoUrl);
            localClipPath = PlayerPrefs.GetString(PrefsPrefix + "LocalClip", localClipPath);
            _clipIndex = PlayerPrefs.GetInt(PrefsPrefix + "ClipIndex", _clipIndex);
            _resolutionScale = PlayerPrefs.GetFloat(PrefsPrefix + "ResolutionScale", _resolutionScale);
            _targetFps = PlayerPrefs.GetInt(PrefsPrefix + "TargetFps", _targetFps);
            _jpegQuality = PlayerPrefs.GetInt(PrefsPrefix + "JpegQuality", _jpegQuality);
            _decodeFps = PlayerPrefs.GetInt(PrefsPrefix + "DecodeFps", _decodeFps);
            streamAudio = PlayerPrefs.GetInt(PrefsPrefix + "StreamAudio", streamAudio ? 1 : 0) != 0;
            audioSampleRate = PlayerPrefs.GetInt(PrefsPrefix + "AudioRate", audioSampleRate);
            audioStereo = PlayerPrefs.GetInt(PrefsPrefix + "AudioStereo", audioStereo ? 1 : 0) != 0;
            playAudio = PlayerPrefs.GetInt(PrefsPrefix + "PlayAudio", playAudio ? 1 : 0) != 0;
            multiRooms = PlayerPrefs.GetString(PrefsPrefix + "MultiRooms", multiRooms);
            multiUrls = PlayerPrefs.GetString(PrefsPrefix + "MultiUrls", multiUrls);
            multiShowGrid = PlayerPrefs.GetInt(PrefsPrefix + "MultiGrid", multiShowGrid ? 1 : 0) != 0;
            multiPlayAudio = PlayerPrefs.GetInt(PrefsPrefix + "MultiAudio", multiPlayAudio ? 1 : 0) != 0;
            // New key: older builds saved FullscreenFill=true, which cropped (zoomed) the video.
            fullscreenFill = PlayerPrefs.GetInt(PrefsPrefix + "FullscreenFit", fullscreenFill ? 1 : 0) != 0;
        }

        private void SaveSettings()
        {
            if (!rememberSettings)
            {
                return;
            }

            PlayerPrefs.SetInt(PrefsPrefix + "Mode", (int)_mode);
            PlayerPrefs.SetInt(PrefsPrefix + "Transport", _transportIndex);
            PlayerPrefs.SetString(PrefsPrefix + "RelayUrl", _relayUrl);
            PlayerPrefs.SetString(PrefsPrefix + "Room", _relayRoom);
            PlayerPrefs.SetString(PrefsPrefix + "WebSocketUrl", _webSocketUrl);
            PlayerPrefs.SetString(PrefsPrefix + "UdpHost", _udpHost);
            PlayerPrefs.SetInt(PrefsPrefix + "UdpPort", _udpPort);
            PlayerPrefs.SetInt(PrefsPrefix + "ControlPort", _controlPort);
            PlayerPrefs.SetInt(PrefsPrefix + "WebSocketPort", _webSocketPort);
            PlayerPrefs.SetInt(PrefsPrefix + "VideoInput", (int)_videoInput);
            PlayerPrefs.SetString(PrefsPrefix + "VideoUrl", _videoUrl);
            PlayerPrefs.SetString(PrefsPrefix + "LocalClip", localClipPath ?? string.Empty);
            PlayerPrefs.SetInt(PrefsPrefix + "ClipIndex", _clipIndex);
            PlayerPrefs.SetFloat(PrefsPrefix + "ResolutionScale", _resolutionScale);
            PlayerPrefs.SetInt(PrefsPrefix + "TargetFps", _targetFps);
            PlayerPrefs.SetInt(PrefsPrefix + "JpegQuality", _jpegQuality);
            PlayerPrefs.SetInt(PrefsPrefix + "DecodeFps", _decodeFps);
            PlayerPrefs.SetInt(PrefsPrefix + "StreamAudio", streamAudio ? 1 : 0);
            PlayerPrefs.SetInt(PrefsPrefix + "AudioRate", audioSampleRate);
            PlayerPrefs.SetInt(PrefsPrefix + "AudioStereo", audioStereo ? 1 : 0);
            PlayerPrefs.SetInt(PrefsPrefix + "PlayAudio", playAudio ? 1 : 0);
            PlayerPrefs.SetString(PrefsPrefix + "MultiRooms", multiRooms ?? string.Empty);
            PlayerPrefs.SetString(PrefsPrefix + "MultiUrls", multiUrls ?? string.Empty);
            PlayerPrefs.SetInt(PrefsPrefix + "MultiGrid", multiShowGrid ? 1 : 0);
            PlayerPrefs.SetInt(PrefsPrefix + "MultiAudio", multiPlayAudio ? 1 : 0);
            PlayerPrefs.SetInt(PrefsPrefix + "FullscreenFit", fullscreenFill ? 1 : 0);
            PlayerPrefs.Save();
        }

        private static int ToTransportOption(TransportKind kind)
        {
            if (kind == TransportKind.Relay)
            {
                return 0;
            }

            return kind == TransportKind.WebSocket ? 1 : 2;
        }

        // ------------------------------------------------------------------
        // Start / stop
        // ------------------------------------------------------------------

        /// <summary>
        /// Check the current settings. Returns false and a user-facing reason when the
        /// panel cannot start yet.
        /// </summary>
        public bool CanStart(out string reason)
        {
            reason = string.Empty;

            if (_mode == SetupMode.Multi)
            {
                return CanStartMulti(out reason);
            }

            if (_mode == SetupMode.Stream)
            {
                if (sender == null)
                {
                    reason = "No StreamingSender in this scene. Add one from Tools > FXM WebStream.";
                    return false;
                }
            }
            else if (receiver == null)
            {
                reason = "No StreamingReceiver in this scene. Add one from Tools > FXM WebStream.";
                return false;
            }

            if (_transportIndex == 0)
            {
                if (!IsValidWebSocketUrl(_relayUrl))
                {
                    reason = "Relay URL is not valid. Example: wss://relay.example.com/fxm-relay";
                    return false;
                }

                if (!IsValidRoom(_relayRoom))
                {
                    reason = "Room must be 1-64 characters: letters, digits, _ or -.";
                    return false;
                }

                if (NeedsSecureWebSocket(_relayUrl))
                {
                    reason = "This page is served over HTTPS, so the browser needs wss:// for the relay. " +
                             "Use wss://relay.example.com/fxm-relay.";
                    return false;
                }

                if (Application.isMobilePlatform && IsLoopbackHost(_relayUrl))
                {
                    reason = "On a phone, 127.0.0.1 is the phone itself. Enter the PC's Wi-Fi address, " +
                             "for example ws://192.168.2.246:8787/fxm-relay.";
                    return false;
                }
            }            else if (_transportIndex == 1)
            {
                bool browserSender = Application.platform == RuntimePlatform.WebGLPlayer &&
                                     _mode == SetupMode.Stream;

                if (_mode == SetupMode.Stream && !browserSender)
                {
                    if (_webSocketPort < 1 || _webSocketPort > 65535)
                    {
                        reason = "WebSocket listen port must be between 1 and 65535.";
                        return false;
                    }
                }
                else if (!IsValidWebSocketUrl(_webSocketUrl))
                {
                    reason = browserSender
                        ? "WebSocket server URL is not valid. Example: ws://192.168.1.10:47778/fxm-stream/"
                        : "WebSocket URL is not valid. Example: ws://192.168.1.10:47778/fxm-stream/";
                    return false;
                }

                if ((browserSender || _mode == SetupMode.Watch) && IsRelayUrl(_webSocketUrl))
                {
                    reason = "This is a relay URL. Select Relay and use the same room and token.";
                    return false;
                }

                if (NeedsSecureWebSocket(_webSocketUrl))
                {
                    reason = "This page is served over HTTPS, so the browser needs wss:// for the WebSocket server.";
                    return false;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_udpHost))
                {
                    reason = _mode == SetupMode.Stream
                        ? "Receiver host is required."
                        : "Sender host is required.";
                    return false;
                }

                if (_udpPort < 1 || _udpPort > 65535)
                {
                    reason = "UDP port must be between 1 and 65535.";
                    return false;
                }

                if (_mode == SetupMode.Watch && (_controlPort < 1 || _controlPort > 65535))
                {
                    reason = "Control port must be between 1 and 65535.";
                    return false;
                }
            }

            if (_mode == SetupMode.Stream)
            {
                if (_videoInput == VideoInputMode.Clip)
                {
                    bool hasLocal = HasUsableLocalClip();
                    bool hasClip = clips != null && _clipIndex >= 0 && _clipIndex < clips.Length &&
                                   clips[_clipIndex] != null;

                    if (!hasLocal && !hasClip)
                    {
                        reason = string.IsNullOrWhiteSpace(localClipPath)
                            ? "Choose a local video file or a clip from the list."
                            : "The chosen local video no longer exists. Pick another file.";
                        return false;
                    }
                }
                else if (_videoInput == VideoInputMode.Url && string.IsNullOrWhiteSpace(_videoUrl))
                {
                    reason = "Enter a video URL or a local file path.";
                    return false;
                }
                else if (_videoInput == VideoInputMode.Camera &&
                         sceneCamera == null && Camera.main == null &&
                         (sender == null || sender.sourceCamera == null))
                {
                    reason = "No camera found. Add a Camera or choose a video source.";
                    return false;
                }
            }

            return true;
        }

        private static bool NeedsSecureWebSocket(string url)
        {
            return Application.platform == RuntimePlatform.WebGLPlayer &&
                   Application.absoluteURL.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                   url != null && url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase);
        }

        private bool CanStartMulti(out string reason)
        {
            reason = string.Empty;

            if (_transportIndex == 2)
            {
                reason = "Watch many supports Relay and WebSocket. Use Relay with one room per sender, " +
                         "or a WebSocket URL per sender.";
                return false;
            }

            string[] sources = ParseMultiSources();
            if (sources.Length == 0)
            {
                reason = _transportIndex == 0
                    ? "Add at least one relay room, for example: demo2, demo3"
                    : "Add at least one WebSocket URL, for example: ws://192.168.1.20:47778/fxm-stream/";
                return false;
            }

            if (_transportIndex == 0)
            {
                if (!IsValidWebSocketUrl(_relayUrl))
                {
                    reason = "Relay URL is not valid. Example: wss://relay.example.com/fxm-relay";
                    return false;
                }

                if (NeedsSecureWebSocket(_relayUrl))
                {
                    reason = "This page is served over HTTPS, so the browser needs wss:// for the relay.";
                    return false;
                }

                if (Application.isMobilePlatform && IsLoopbackHost(_relayUrl))
                {
                    reason = "On a phone, 127.0.0.1 is the phone itself. Enter the PC's Wi-Fi address, " +
                             "for example ws://192.168.2.246:8787/fxm-relay.";
                    return false;
                }

                for (int i = 0; i < sources.Length; i++)
                {
                    if (!IsValidRoom(sources[i]))
                    {
                        reason = "Room '" + sources[i] + "' is invalid. Use 1-64 letters, digits, _ or -.";
                        return false;
                    }
                }
            }
            else
            {
                for (int i = 0; i < sources.Length; i++)
                {
                    if (!IsValidWebSocketUrl(sources[i]))
                    {
                        reason = "WebSocket URL '" + sources[i] + "' is invalid.";
                        return false;
                    }

                    if (IsRelayUrl(sources[i]))
                    {
                        reason = "This is a relay URL. Select Relay and use rooms instead.";
                        return false;
                    }

                    if (NeedsSecureWebSocket(sources[i]))
                    {
                        reason = "This page is served over HTTPS, so the browser needs wss:// for '" +
                                 sources[i] + "'.";
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>Start the selected role with the current settings.</summary>
        public void StartSelected()
        {
            string reason;
            if (!CanStart(out reason))
            {
                SetStatus(reason, true);
                return;
            }

            SaveSettings();
            _startedAt = Time.unscaledTime;
            _contentScroll = Vector2.zero;
            _fileBrowserOpen = false;

            if (_mode == SetupMode.Stream)
            {
                StartSender();
            }
            else if (_mode == SetupMode.Watch)
            {
                StartReceiver();
            }
            else
            {
                StartMulti();
            }

            if (fullscreenOnStart && IsStarted &&
                (_mode == SetupMode.Watch || _mode == SetupMode.Multi))
            {
                _hidden = true;
                _controlsUntil = Time.unscaledTime + fullscreenControlsTimeout;
            }

            if (IsStarted)
            {
                string transportName = TransportLabels[_transportIndex];
                if (_transportIndex == 0)
                {
                    SetStatus(_mode == SetupMode.Stream
                        ? "Streaming | Relay | connecting..."
                        : "Watching | Relay | connecting...", false);
                }
                else
                {
                    SetStatus(_mode == SetupMode.Stream
                        ? "Streaming | " + transportName + ". Keep this window open or hide it."
                        : (_mode == SetupMode.Watch
                            ? "Watching | " + transportName + " | waiting for the sender."
                            : "Watching | " + transportName + " | waiting for the senders."), false);
                }
            }
            else
            {
                SetStatus("Could not start. Check the Console for transport errors and the server URL.", true);
            }
        }

        /// <summary>Stop the active role and return to the settings screen.</summary>
        public void StopSelected()
        {
            if (sender != null && sender.IsStreaming)
            {
                sender.StopStreaming();
            }

            if (receiver != null && receiver.IsReceiving)
            {
                receiver.StopReceiving();
            }

            if (multiReceiver != null && multiReceiver.IsReceiving)
            {
                multiReceiver.StopReceiving();
            }

            if (webcamSource != null)
            {
                webcamSource.Stop();
            }

            if (videoSource != null)
            {
                videoSource.Stop();
            }

            SetStatus("Stopped. Change the settings, then start again.", false);
        }

        private void StartSender()
        {
            sender.StopStreaming();
            sender.autoStart = false;
            sender.autoPlay = true;
            sender.showStatistics = false;
            sender.resolutionScale = _resolutionScale;
            sender.targetFps = _targetFps;
            sender.jpegQuality = _jpegQuality;
            ApplyTransportToSender();
            ApplyAudioToSender();
            ApplyVideoInput();

            sender.StartStreaming();
        }

        private void StartReceiver()
        {
            receiver.StopReceiving();
            if (multiReceiver != null && multiReceiver.IsReceiving)
            {
                multiReceiver.StopReceiving();
            }

            receiver.autoStart = false;
            receiver.maxDecodeFps = _decodeFps;
            receiver.preserveAspectRatio = true;
            receiver.showStatistics = false;
            receiver.playAudio = playAudio;
            ApplyTransportToReceiver();
            receiver.StartReceiving();
        }

        private void ApplyAudioToSender()
        {
            sender.streamAudio = streamAudio;
            sender.audioSampleRate = audioSampleRate;
            sender.audioStereo = audioStereo;
            sender.audioGain = 1f;
        }

        private void StartMulti()
        {
            MultiStreamReceiver multi = EnsureMultiReceiver();
            if (multi == null)
            {
                SetStatus("Could not create the multi-source receiver.", true);
                return;
            }

            if (multi.IsReceiving)
            {
                multi.StopReceiving();
            }

            if (receiver != null && receiver.IsReceiving)
            {
                receiver.StopReceiving();
            }

            string[] sources = ParseMultiSources();
            var configs = new StreamSourceConfig[sources.Length];

            for (int i = 0; i < sources.Length; i++)
            {
                string source = sources[i];

                if (_transportIndex == 0)
                {
                    configs[i] = new StreamSourceConfig
                    {
                        name = source,
                        transportKind = TransportKind.Relay,
                        relayUrl = _relayUrl.Trim(),
                        relayRoom = source,
                        relayToken = (_relayToken ?? string.Empty).Trim()
                    };
                }
                else
                {
                    configs[i] = new StreamSourceConfig
                    {
                        name = ShortSourceName(source),
                        transportKind = TransportKind.WebSocket,
                        webSocketUrl = source,
                        webSocketServer = false
                    };
                }
            }

            multi.autoStart = false;
            multi.showStatistics = false;
            multi.maxDecodeFps = _decodeFps;
            multi.showAllSources = multiShowGrid;
            multi.playAudio = multiPlayAudio;
            multi.audioFollowsActiveSource = true;
            multi.sources = configs;
            multi.StartReceiving();
        }

        private MultiStreamReceiver EnsureMultiReceiver()
        {
            if (multiReceiver == null)
            {
                multiReceiver = GetComponent<MultiStreamReceiver>();
                if (multiReceiver == null)
                {
                    multiReceiver = gameObject.AddComponent<MultiStreamReceiver>();
                }

                multiReceiver.autoStart = false;
            }

            return multiReceiver;
        }

        private string[] ParseMultiSources()
        {
            string raw = _transportIndex == 0 ? multiRooms : multiUrls;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new string[0];
            }

            string[] parts = raw.Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new System.Collections.Generic.List<string>(parts.Length);

            for (int i = 0; i < parts.Length; i++)
            {
                string value = parts[i].Trim();
                if (value.Length == 0 || result.Contains(value))
                {
                    continue;
                }

                result.Add(value);
            }

            return result.ToArray();
        }

        private static string ShortSourceName(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return "Source";
            }

            var config = TransportConfig.ParseWebSocketUrl(url);
            if (string.IsNullOrEmpty(config.RemoteHost))
            {
                return url;
            }

            return config.RemoteHost + ":" + config.RemotePort;
        }

        private void ApplyTransportToSender()
        {
            if (_transportIndex == 0)
            {
                sender.transportKind = TransportKind.Relay;
                sender.relayUrl = _relayUrl.Trim();
                sender.relayRoom = _relayRoom.Trim();
                sender.relayToken = (_relayToken ?? string.Empty).Trim();
            }
            else if (_transportIndex == 1)
            {
                sender.transportKind = TransportKind.WebSocket;

                if (Application.platform == RuntimePlatform.WebGLPlayer)
                {
                    // The browser cannot listen; connect out to a desktop WebSocket server.
                    sender.webSocketServer = false;
                    sender.webSocketUrl = _webSocketUrl.Trim();
                }
                else
                {
                    sender.webSocketServer = true;
                    sender.webSocketUrl = "ws://0.0.0.0:" + _webSocketPort + "/fxm-stream/";
                    sender.remotePort = _webSocketPort;
                }
            }
            else
            {
                sender.transportKind = TransportKind.Udp;
                sender.remoteHost = _udpHost.Trim();
                sender.remotePort = _udpPort;
                sender.localControlPort = _controlPort;
                sender.allowRemoteControl = true;
            }
        }

        private void ApplyTransportToReceiver()
        {
            if (_transportIndex == 0)
            {
                receiver.transportKind = TransportKind.Relay;
                receiver.relayUrl = _relayUrl.Trim();
                receiver.relayRoom = _relayRoom.Trim();
                receiver.relayToken = (_relayToken ?? string.Empty).Trim();
            }
            else if (_transportIndex == 1)
            {
                receiver.transportKind = TransportKind.WebSocket;
                receiver.webSocketServer = false;
                receiver.webSocketUrl = _webSocketUrl.Trim();
            }
            else
            {
                receiver.transportKind = TransportKind.Udp;
                receiver.listenPort = _udpPort;
                receiver.senderHost = _udpHost.Trim();
                receiver.senderControlPort = _controlPort;
            }
        }

        private void ApplyVideoInput()
        {
            if (_videoInput == VideoInputMode.Camera)
            {
                if (webcamSource != null)
                {
                    webcamSource.Stop();
                }

                if (videoSource != null)
                {
                    videoSource.Stop();
                }

                sender.AssignVideoSource(null);
                sender.sourceTexture = null;
                sender.sourceCamera = sceneCamera != null ? sceneCamera : Camera.main;
                return;
            }

            if (_videoInput == VideoInputMode.Webcam)
            {
                WebCamStreamSource webcam = EnsureWebcamSource();
                if (videoSource != null)
                {
                    videoSource.Stop();
                }

                webcam.playOnStart = false;
                sender.AssignVideoSource(webcam);
                sender.sourceTexture = null;
                return;
            }

            VideoPlayerStreamSource source = EnsureVideoSource();
            source.playOnStart = false;
            source.loop = true;
            sender.AssignVideoSource(source);
            sender.sourceTexture = null;

            if (_videoInput == VideoInputMode.Clip)
            {
                if (HasUsableLocalClip())
                {
                    source.SetUrl(localClipPath);
                }
                else if (clips != null && _clipIndex >= 0 && _clipIndex < clips.Length)
                {
                    source.SetClip(clips[_clipIndex]);
                }
            }
            else
            {
                source.SetUrl(_videoUrl);
            }
        }

        private VideoPlayerStreamSource EnsureVideoSource()
        {
            if (videoSource != null)
            {
                return videoSource;
            }

            _ownedVideoObject = new GameObject("FXM Video Source (runtime)");
            var player = _ownedVideoObject.AddComponent<VideoPlayer>();
            player.playOnAwake = false;
            player.renderMode = VideoRenderMode.RenderTexture;

            videoSource = _ownedVideoObject.AddComponent<VideoPlayerStreamSource>();
            videoSource.videoPlayer = player;
            videoSource.playOnStart = false;
            videoSource.loop = true;
            return videoSource;
        }

        private WebCamStreamSource EnsureWebcamSource()
        {
            if (webcamSource != null)
            {
                return webcamSource;
            }

            _ownedWebcamObject = new GameObject("FXM Webcam Source (runtime)");
            webcamSource = _ownedWebcamObject.AddComponent<WebCamStreamSource>();
            webcamSource.playOnStart = false;
            webcamSource.autoPlay = true;
            return webcamSource;
        }

        private void SetStatus(string message, bool isError)
        {
            _status = message;
            _statusIsError = isError;
        }

        // ------------------------------------------------------------------
        // IMGUI
        // ------------------------------------------------------------------

        private void OnGUI()
        {
            EnsureStyles();

            GUISkin previousSkin = GUI.skin;
            GUI.skin = _darkSkin;
            Matrix4x4 previousMatrix = GUI.matrix;

            try
            {
                DrawGui();
            }
            finally
            {
                GUI.skin = previousSkin;
                GUI.matrix = previousMatrix;
            }
        }

        private void DrawGui()
        {
            float scale = GetUiScale();
            if (scale > 1.001f)
            {
                GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            }

            float screenWidth = Screen.width / scale;
            float screenHeight = Screen.height / scale;
            Rect safe = GetSafeAreaGui(scale);

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == toggleKey)
            {
                _hidden = !_hidden;
                _controlsUntil = Time.unscaledTime + fullscreenControlsTimeout;
                Event.current.Use();
            }

            if (_hidden)
            {
                _fileBrowserOpen = false;
                DrawFullscreenViewer(screenWidth, screenHeight, safe);
                return;
            }

            RestorePanelChrome();

            bool compact = safe.width < 660f;
            float margin = compact ? 0f : 16f;
            float width = compact ? safe.width : Mathf.Min(920f, safe.width - margin * 2f);
            float height = compact ? safe.height : Mathf.Min(720f, safe.height - margin * 2f);
            Rect window = new Rect(
                safe.x + (safe.width - width) * 0.5f,
                safe.y + (safe.height - height) * 0.5f,
                width,
                height);

            // Opaque black behind the panel: the scene, cameras, and the empty Canvas must
            // not show through while the setup UI is open.
            Color previousBackdrop = GUI.color;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0f, 0f, screenWidth, screenHeight), Texture2D.whiteTexture);
            GUI.color = previousBackdrop;
            GUI.color = PanelBackdrop;
            GUI.DrawTexture(window, Texture2D.whiteTexture);
            GUI.color = previousBackdrop;

            float padding = compact ? 12f : 18f;
            const float headerHeight = 58f;
            const float footerHeight = 80f;

            Rect header = new Rect(window.x + padding, window.y + padding,
                window.width - padding * 2f, headerHeight);
            Rect footer = new Rect(window.x + padding,
                window.y + window.height - padding - footerHeight,
                window.width - padding * 2f, footerHeight);
            float contentTop = header.yMax + 8f;
            Rect content = new Rect(header.x, contentTop, header.width,
                Mathf.Max(40f, footer.y - 8f - contentTop));

            Color previousSurface = GUI.color;
            GUI.color = PanelSurface;
            GUI.DrawTexture(new Rect(window.x, window.y, window.width,
                header.yMax + 10f - window.y), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(window.x, footer.y - 9f, window.width,
                window.yMax - (footer.y - 9f)), Texture2D.whiteTexture);
            GUI.color = previousSurface;

            if (!compact)
            {
                DrawOutline(window, BorderColor, 1f);
            }

            Color previousLine = GUI.color;
            GUI.color = DividerColor;
            GUI.DrawTexture(new Rect(window.x, header.yMax + 9f, window.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(window.x, footer.y - 8f, window.width, 1f), Texture2D.whiteTexture);
            GUI.color = previousLine;

            DrawPanelHeader(header);
            DrawPanelContent(content);
            DrawPanelFooter(footer);
        }

        private float GetUiScale()
        {
            if (Screen.width != _scaleScreenWidth || Screen.height != _scaleScreenHeight ||
                !Mathf.Approximately(Screen.dpi, _scaleDpi))
            {
                _scaleScreenWidth = Screen.width;
                _scaleScreenHeight = Screen.height;
                _scaleDpi = Screen.dpi;

                float dpi = Screen.dpi;
                float scale = dpi > 1f ? dpi / 160f : Screen.height / 800f;
                _uiScale = Mathf.Clamp(scale, 1f, 2.2f);
            }

            return _uiScale;
        }

        private Rect GetSafeAreaGui(float scale)
        {
            Rect safe = Screen.safeArea;
            if (safe.width <= 0f || safe.height <= 0f)
            {
                safe = new Rect(0f, 0f, Screen.width, Screen.height);
            }

            if (!_hasSafePixels || safe != _safePixels || !Mathf.Approximately(scale, _safeAreaScale))
            {
                _hasSafePixels = true;
                _safePixels = safe;
                _safeAreaScale = scale;

                _safeAreaGui = new Rect(
                    safe.x / scale,
                    (Screen.height - safe.yMax) / scale,
                    safe.width / scale,
                    safe.height / scale);
            }

            return _safeAreaGui;
        }

        // ------------------------------------------------------------------
        // Fullscreen viewer
        // ------------------------------------------------------------------

        private bool ControlsVisible
        {
            get { return !IsStarted || Time.unscaledTime < _controlsUntil; }
        }

        private void DrawFullscreenViewer(float screenWidth, float screenHeight, Rect safe)
        {
            // While sending, the camera may not be attached to a capture texture yet.
            // In that case the game is the best thing to show instead of a black screen
            // and the "waiting for the first frame" message. Keep the compact bar so the
            // Setup button remains reachable on touch devices.
            if (_mode == SetupMode.Stream && !IsStarted)
            {
                TickFullscreenControls();
                bool idleControls = ControlsVisible;
                UpdateFullscreenChrome();

                if (idleControls)
                {
                    DrawFullscreenControls(screenWidth, safe);
                }

                UpdateCursor(idleControls);
                return;
            }

            Color previous = GUI.color;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0f, 0f, screenWidth, screenHeight), Texture2D.whiteTexture);
            GUI.color = previous;

            TickFullscreenControls();
            bool showControls = ControlsVisible;

            if (_mode == SetupMode.Multi && multiReceiver != null && multiReceiver.SourceCount > 0)
            {
                if (multiReceiver.showAllSources && multiReceiver.SourceCount > 1)
                {
                    DrawFullscreenGrid(screenWidth, screenHeight, showControls);
                }
                else
                {
                    DrawFullscreenTexture(
                        new Rect(0f, 0f, screenWidth, screenHeight),
                        multiReceiver.GetSourceTexture(multiReceiver.ActiveSourceIndex));
                }
            }
            else
            {
                DrawFullscreenTexture(
                    new Rect(0f, 0f, screenWidth, screenHeight),
                    GetPreviewTexture());
            }

            HandleFullscreenKeys();
            UpdateFullscreenChrome();

            if (showControls)
            {
                DrawFullscreenControls(screenWidth, safe);
            }

            UpdateCursor(showControls);
        }

        private void DrawFullscreenTexture(Rect rect, Texture texture)
        {
            if (texture == null)
            {
                GUI.Label(rect, GetFullscreenWaitingMessage(), _fullscreenMessageStyle);
                return;
            }

            GUI.DrawTexture(rect, texture,
                fullscreenFill ? ScaleMode.ScaleAndCrop : ScaleMode.ScaleToFit, false);
        }

        private void DrawFullscreenGrid(float screenWidth, float screenHeight, bool showControls)
        {
            int count = multiReceiver.SourceCount;
            float screenAspect = screenHeight > 0f ? screenWidth / screenHeight : 1f;
            Texture activeTexture = multiReceiver.GetSourceTexture(multiReceiver.ActiveSourceIndex);
            float sourceAspect = activeTexture != null && activeTexture.height > 0
                ? activeTexture.width / (float)activeTexture.height
                : 16f / 9f;

            int columns = GetGridColumnCount(count, screenAspect, sourceAspect);
            int rows = Mathf.CeilToInt(count / (float)columns);
            float cellWidth = screenWidth / columns;
            float cellHeight = screenHeight / rows;
            ScaleMode mode = fullscreenFill ? ScaleMode.ScaleAndCrop : ScaleMode.ScaleToFit;
            int active = multiReceiver.ActiveSourceIndex;

            for (int row = 0; row < rows; row++)
            {
                int itemsInRow = Mathf.Min(columns, count - row * columns);
                float offset = (screenWidth - itemsInRow * cellWidth) * 0.5f;

                for (int column = 0; column < itemsInRow; column++)
                {
                    int index = row * columns + column;
                    Rect cell = new Rect(offset + column * cellWidth, row * cellHeight,
                        cellWidth, cellHeight);

                    Texture texture = multiReceiver.GetSourceTexture(index);
                    if (texture != null)
                    {
                        GUI.DrawTexture(cell, texture, mode, false);
                    }

                    if (showControls)
                    {
                        DrawSourceChrome(cell, index, index == active);

                        if (GUI.Button(cell, GUIContent.none, GUIStyle.none))
                        {
                            multiReceiver.SetActiveSource(index);
                        }
                    }
                }
            }
        }

        private void DrawSourceChrome(Rect cell, int index, bool active)
        {
            Color previous = GUI.color;

            if (active)
            {
                GUI.color = new Color(0.35f, 0.9f, 0.65f, 0.95f);
                GUI.DrawTexture(new Rect(cell.x, cell.y, cell.width, 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cell.x, cell.yMax - 2f, cell.width, 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cell.x, cell.y, 2f, cell.height), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cell.xMax - 2f, cell.y, 2f, cell.height), Texture2D.whiteTexture);
            }

            float labelWidth = Mathf.Max(1f, Mathf.Min(cell.width - 12f, 240f));
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(cell.x + 6f, cell.yMax - 28f, labelWidth, 22f), Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUI.Label(new Rect(cell.x + 12f, cell.yMax - 27f, Mathf.Max(1f, cell.width - 24f), 20f),
                multiReceiver.GetSourceName(index) + "  " + multiReceiver.GetSourceState(index),
                _overlaySmallStyle);

            GUI.color = previous;
        }

        private void DrawFullscreenControls(float screenWidth, Rect safe)
        {
            const float barHeight = 46f;

            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.68f);
            GUI.DrawTexture(new Rect(0f, safe.y, screenWidth, barHeight), Texture2D.whiteTexture);
            GUI.color = previous;

            GUILayout.BeginArea(new Rect(safe.x + 8f, safe.y + 8f, safe.width - 16f, barHeight - 14f));
            GUILayout.BeginHorizontal();

            if (safe.width >= 620f)
            {
                GUILayout.Label(GetViewerTitle(), _overlayLabelStyle, GUILayout.ExpandWidth(true));
            }
            else
            {
                GUILayout.FlexibleSpace();
            }

            float buttonWidth = safe.width >= 560f ? 80f : (safe.width >= 420f ? 66f : 54f);

            if (OverlayButton("Setup", buttonWidth))
            {
                _hidden = false;
                _controlsUntil = 0f;
            }

            if (IsStarted)
            {
                if (_mode == SetupMode.Multi && multiReceiver != null && multiReceiver.SourceCount > 1 &&
                    OverlayButton(multiReceiver.showAllSources ? "Single" : "Grid", buttonWidth))
                {
                    multiReceiver.SetShowAllSources(!multiReceiver.showAllSources);
                }

                if (_mode != SetupMode.Stream && IsViewerAudioAvailable() &&
                    OverlayButton(IsViewerAudioOn() ? "Audio" : "Muted", buttonWidth))
                {
                    ToggleViewerAudio();
                }

                if (_mode != SetupMode.Stream &&
                    OverlayButton(fullscreenFill ? "Fit" : "Fill", buttonWidth))
                {
                    fullscreenFill = !fullscreenFill;
                    SaveSettings();
                }

                if (OverlayButton("Stop", buttonWidth))
                {
                    StopSelected();
                    _hidden = false;
                }
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private bool OverlayButton(string label, float width)
        {
            return GUILayout.Button(label, _overlayButtonStyle,
                GUILayout.Width(width), GUILayout.Height(28f));
        }

        private string GetViewerTitle()
        {
            string transport = _transportIndex >= 0 && _transportIndex < TransportLabels.Length
                ? TransportLabels[_transportIndex]
                : string.Empty;

            if (!IsStarted)
            {
                return "Idle | " + transport;
            }

            if (_mode == SetupMode.Stream)
            {
                return "Sending | " + transport;
            }

            if (_mode == SetupMode.Watch)
            {
                int peers = receiver != null && receiver.Transport != null ? receiver.Transport.PeerCount : 0;
                return "Watching | " + transport + (peers > 0 ? " | connected" : " | waiting");
            }

            int total = multiReceiver != null ? multiReceiver.SourceCount : 0;
            int connected = multiReceiver != null ? multiReceiver.ConnectedSourceCount : 0;
            return "Watching " + total + " source(s) | " + connected + " connected";
        }

        private bool IsViewerAudioAvailable()
        {
            if (_mode == SetupMode.Multi)
            {
                return multiReceiver != null && multiReceiver.AudioAvailable;
            }

            return receiver != null && receiver.AudioAvailable;
        }

        private bool IsViewerAudioOn()
        {
            if (_mode == SetupMode.Multi)
            {
                return multiReceiver != null && multiReceiver.playAudio;
            }

            return receiver != null && receiver.playAudio && !receiver.AudioMuted;
        }

        private void ToggleViewerAudio()
        {
            if (_mode == SetupMode.Multi)
            {
                if (multiReceiver != null)
                {
                    multiReceiver.playAudio = !multiReceiver.playAudio;
                }

                return;
            }

            if (receiver != null)
            {
                receiver.SetAudioMuted(!receiver.AudioMuted);
            }
        }

        private string GetFullscreenWaitingMessage()
        {
            if (_mode == SetupMode.Multi)
            {
                return multiReceiver != null && multiReceiver.SourceCount > 0
                    ? "Waiting for the senders..."
                    : "Nothing to watch yet.\nTap the screen, then Setup.";
            }

            if (!IsStarted)
            {
                return "Nothing is playing yet.\nTap the screen, then Setup.";
            }

            return _mode == SetupMode.Stream
                ? "Waiting for the first frame..."
                : "Waiting for the sender...";
        }

        private void TickFullscreenControls()
        {
            Event current = Event.current;
            if (current == null)
            {
                return;
            }

            if (current.type == EventType.MouseUp ||
                current.type == EventType.MouseDrag ||
                current.type == EventType.ScrollWheel)
            {
                // Reveal on release so the tap that opens the bar cannot also press a
                // button that appears under the finger.
                _controlsUntil = Time.unscaledTime + fullscreenControlsTimeout;
            }
            else if (current.type == EventType.MouseMove && current.delta.sqrMagnitude > 0.01f)
            {
                _controlsUntil = Time.unscaledTime + fullscreenControlsTimeout;
            }
        }

        private void HandleFullscreenKeys()
        {
            if (Event.current == null || Event.current.type != EventType.KeyDown ||
                _mode != SetupMode.Multi || multiReceiver == null || !multiReceiver.IsReceiving)
            {
                return;
            }

            int index = -1;
            if (Event.current.keyCode >= KeyCode.Alpha1 && Event.current.keyCode <= KeyCode.Alpha9)
            {
                index = Event.current.keyCode - KeyCode.Alpha1;
            }
            else if (Event.current.keyCode >= KeyCode.Keypad1 && Event.current.keyCode <= KeyCode.Keypad9)
            {
                index = Event.current.keyCode - KeyCode.Keypad1;
            }

            if (index >= 0 && index < multiReceiver.SourceCount)
            {
                multiReceiver.SetActiveSource(index);
                Event.current.Use();
            }
        }

        private void UpdateCursor(bool controlsVisible)
        {
            bool hide = _hidden && IsStarted && !controlsVisible;
            if (Cursor.visible == hide)
            {
                Cursor.visible = !hide;
            }
        }

        private void UpdateFullscreenChrome()
        {
            bool immersive = _hidden && IsStarted && Application.isMobilePlatform;
            if (immersive != _immersiveApplied)
            {
                SetAndroidImmersive(immersive);
            }
        }

        private void RestorePanelChrome()
        {
            if (_immersiveApplied)
            {
                SetAndroidImmersive(false);
            }

            if (!Cursor.visible)
            {
                Cursor.visible = true;
            }
        }

        private void SetAndroidImmersive(bool enabled)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var window = activity.Call<AndroidJavaObject>("getWindow"))
                using (var decor = window.Call<AndroidJavaObject>("getDecorView"))
                {
                    // HIDE_NAVIGATION | FULLSCREEN | LAYOUT_STABLE | LAYOUT_HIDE_NAVIGATION |
                    // LAYOUT_FULLSCREEN | IMMERSIVE_STICKY
                    decor.Call("setSystemUiVisibility", enabled ? 5894 : 0);
                }

                _immersiveApplied = enabled;
            }
            catch (System.Exception)
            {
                _immersiveApplied = false;
            }
#else
            _immersiveApplied = enabled;
#endif
        }

        /// <summary>
        /// Number of columns used by the fullscreen many-source grid. Chooses the layout
        /// whose cells come closest to the source aspect without leaving many empty cells.
        /// </summary>
        public static int GetGridColumnCount(int sourceCount, float screenAspect, float sourceAspect)
        {
            if (sourceCount <= 1)
            {
                return 1;
            }

            if (screenAspect <= 0f)
            {
                screenAspect = 16f / 9f;
            }

            if (sourceAspect <= 0f)
            {
                sourceAspect = 16f / 9f;
            }

            int best = 1;
            float bestScore = float.MaxValue;

            for (int columns = 1; columns <= sourceCount; columns++)
            {
                int rows = Mathf.CeilToInt(sourceCount / (float)columns);
                float cellAspect = screenAspect * rows / columns;
                float score = Mathf.Abs(Mathf.Log(cellAspect / sourceAspect));
                score += 0.08f * (columns * rows - sourceCount);

                bool better = score < bestScore - 0.002f;
                bool tieOnLandscape = Mathf.Abs(score - bestScore) <= 0.002f &&
                                      screenAspect > 1f && columns > best;

                if (better || tieOnLandscape)
                {
                    bestScore = score;
                    best = columns;
                }
            }

            return best;
        }

        // ------------------------------------------------------------------
        // Panel: header, content, footer
        // ------------------------------------------------------------------

        private void DrawPanelHeader(Rect rect)
        {
            GUILayout.BeginArea(rect);

            GUILayout.BeginHorizontal();
            GUILayout.Label("FXM WebStream", _titleStyle, GUILayout.Height(26f));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Hide (" + toggleKey + ")", _secondaryButtonStyle,
                    GUILayout.Width(96f), GUILayout.Height(26f)))
            {
                _hidden = true;
                _controlsUntil = Time.unscaledTime + fullscreenControlsTimeout;
            }

            GUILayout.EndHorizontal();

            GUILayout.Space(2f);

            int mode = GUILayout.Toolbar((int)_mode, ModeLabels, _tabStyle,
                GUILayout.Width(Mathf.Min(400f, rect.width)), GUILayout.Height(28f));
            if (mode != (int)_mode)
            {
                _mode = (SetupMode)mode;
                _contentScroll = Vector2.zero;
                _fileBrowserOpen = false;
            }

            GUILayout.EndArea();
        }

        private void DrawPanelContent(Rect rect)
        {
            if (_fileBrowserOpen)
            {
                DrawFileBrowser(rect);
                return;
            }

            const float gap = 16f;

            if (rect.width >= 600f)
            {
                float previewWidth = Mathf.Clamp(rect.width * 0.38f, 280f, 400f);
                Rect settings = new Rect(rect.x, rect.y, rect.width - previewWidth - gap, rect.height);
                Rect preview = new Rect(settings.xMax + gap, rect.y, previewWidth, rect.height);

                DrawSettingsScroll(settings);
                DrawPreviewPanel(preview);
            }
            else
            {
                float previewHeight = Mathf.Clamp(rect.height * 0.30f, 112f, 200f);
                Rect preview = new Rect(rect.x, rect.y, rect.width, previewHeight);
                Rect settings = new Rect(rect.x, preview.yMax + 8f, rect.width,
                    Mathf.Max(40f, rect.height - previewHeight - 8f));

                DrawPreviewPanel(preview);
                DrawSettingsScroll(settings);
            }
        }

        private void DrawSettingsScroll(Rect rect)
        {
            GUILayout.BeginArea(rect);
            _contentWidth = Mathf.Max(220f, rect.width - 18f);
            _contentScroll = GUILayout.BeginScrollView(_contentScroll, false, true);

            if (IsStarted && _transportIndex == 0)
            {
                DrawRelayConnectionStatus();
                GUILayout.Space(4f);
            }

            bool wasEnabled = GUI.enabled;
            GUI.enabled = !IsStarted;
            DrawSettings();
            GUI.enabled = wasEnabled;

            GUILayout.Space(6f);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawPreviewPanel(Rect rect)
        {
            GUILayout.BeginArea(rect);
            GUILayout.Label("Preview", _sectionStyle);

            float available = Mathf.Max(60f, rect.height - 22f);
            float previewWidth = Mathf.Min(rect.width, available * 16f / 9f);
            float aspectHeight = Mathf.Min(available, previewWidth * 9f / 16f);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            Rect preview = GUILayoutUtility.GetRect(previewWidth, aspectHeight);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            Color previous = GUI.color;
            GUI.color = FieldBackground;
            GUI.DrawTexture(preview, Texture2D.whiteTexture);
            GUI.color = previous;

            Texture texture = GetPreviewTexture();
            if (texture != null)
            {
                GUI.DrawTexture(preview, texture, ScaleMode.ScaleToFit, false);
            }
            else
            {
                GUI.Label(preview, GetPreviewPlaceholder(), _centerStyle);
            }

            DrawOutline(preview, BorderColor, 1f);

            if (rect.height >= 220f)
            {
                GUILayout.Label(GetLiveSummary(), _hintStyle);
            }

            GUILayout.EndArea();
        }

        private string GetPreviewPlaceholder()
        {
            if (_mode == SetupMode.Stream)
            {
                return IsStarted
                    ? "Waiting for the first frame..."
                    : "The preview appears after you start.";
            }

            if (_mode == SetupMode.Watch)
            {
                return IsStarted
                    ? "Waiting for the sender..."
                    : "The video appears after you press Watch.";
            }

            return IsStarted
                ? "Waiting for the senders..."
                : "Start Many to see the sources.";
        }

        private void DrawSettings()
        {
            DrawConnectionSettings();

            if (_mode == SetupMode.Stream)
            {
                DrawVideoSettings();
                DrawQualitySettings();
            }
            else if (_mode == SetupMode.Watch)
            {
                DrawWatchSettings();
            }
            else
            {
                DrawMultiSettings();
            }
        }

        private void DrawConnectionSettings()
        {
            bool multi = _mode == SetupMode.Multi;
            bool webGL = Application.platform == RuntimePlatform.WebGLPlayer;
            string[] labels;
            if (webGL || multi)
            {
                // Browsers have no UDP sockets. A WebGL sender still supports WebSocket,
                // but only as an outbound client to a server running on a desktop.
                labels = new[] { TransportLabels[0], TransportLabels[1] };
            }
            else
            {
                labels = TransportLabels;
            }

            _transportIndex = Mathf.Clamp(_transportIndex, 0, labels.Length - 1);
            SectionRow("Connection", labels, ref _transportIndex);

            if (_transportIndex == 0)
            {
                _relayUrl = InlineField("Relay URL", _relayUrl);
                if (!multi)
                {
                    _relayRoom = InlineField("Room", _relayRoom, 64);
                }

                _relayToken = InlinePasswordField("Token", _relayToken, 64);

                if (Application.isMobilePlatform && IsLoopbackHost(_relayUrl))
                {
                    Hint("On this phone, use the PC's Wi-Fi address, not 127.0.0.1.");
                }
            }
            else if (_transportIndex == 1)
            {
                if (_mode == SetupMode.Stream)
                {
                    if (Application.platform == RuntimePlatform.WebGLPlayer)
                    {
                        _webSocketUrl = InlineField("Server URL", _webSocketUrl);
                        Hint("Run the WebSocket server on a desktop; the browser connects out as a client.");
                    }
                    else
                    {
                        _webSocketPort = InlineIntField("Listen port", _webSocketPort);
                        Hint("Receivers connect to ws://<this-pc-ip>:" + _webSocketPort + "/fxm-stream/");
                    }
                }
                else if (_mode == SetupMode.Watch)
                {
                    _webSocketUrl = InlineField("WebSocket URL", _webSocketUrl);
                }
                else
                {
                    Hint("Enter one WebSocket URL per sender under Sources.");
                }
            }
            else
            {
                _udpHost = InlineField(_mode == SetupMode.Stream ? "Receiver IP" : "Sender IP", _udpHost);
                _udpPort = InlineIntField(_mode == SetupMode.Stream ? "Receiver port" : "Listen port", _udpPort);
                if (_mode == SetupMode.Watch)
                {
                    _controlPort = InlineIntField("Control port", _controlPort);
                }
            }
        }

        private void DrawVideoSettings()
        {
            int input = (int)_videoInput;
            SectionRow("Source", InputLabels, ref input);
            _videoInput = (VideoInputMode)input;

            if (_videoInput == VideoInputMode.Camera)
            {
                Hint("Streams the scene camera, including the rotating cube in the demo scene.");
            }
            else if (_videoInput == VideoInputMode.Clip)
            {
                bool hasClipList = clips != null && clips.Length > 0;
                bool hasLocal = !string.IsNullOrWhiteSpace(localClipPath);

                if (hasLocal)
                {
                    GUILayout.Label("File: " + GetDisplayFileName(localClipPath), _smallStyle);
                    if (!HasUsableLocalClip())
                    {
                        Color previousWarning = GUI.color;
                        GUI.color = WarningColor;
                        GUILayout.Label("This file is no longer available. Choose another video.", _hintStyle);
                        GUI.color = previousWarning;
                    }
                }

                if (hasClipList)
                {
                    var names = new string[clips.Length];
                    for (int i = 0; i < clips.Length; i++)
                    {
                        names[i] = clips[i] != null ? clips[i].name : "(missing)";
                    }

                    _clipIndex = Mathf.Clamp(_clipIndex, 0, clips.Length - 1);
                    int selectedClip = GUILayout.SelectionGrid(_clipIndex, names,
                        names.Length > 4 ? 2 : 1, _selectionGridStyle,
                        GUILayout.Height(26f * Mathf.Ceil(names.Length / (float)(names.Length > 4 ? 2 : 1))));

                    if (selectedClip != _clipIndex)
                    {
                        _clipIndex = selectedClip;
                        localClipPath = string.Empty;
                        SaveSettings();
                    }
                }
                else if (!hasLocal)
                {
                    Hint("No clips assigned. Choose a local video file below, or add clips in the Inspector.");
                }

                if (hasLocal && hasClipList)
                {
                    Hint("The local file is used until you pick a clip above.");
                    if (GUILayout.Button("Use the selected Inspector clip", _secondaryButtonStyle,
                            GUILayout.Height(26f)))
                    {
                        localClipPath = string.Empty;
                        SaveSettings();
                    }
                }

                if (Application.platform != RuntimePlatform.WebGLPlayer &&
                    GUILayout.Button(hasLocal ? "Change local video..." : "Choose local video...",
                        _secondaryButtonStyle, GUILayout.Height(30f)))
                {
                    OpenFileBrowser();
                }
                else if (Application.platform == RuntimePlatform.WebGLPlayer && hasLocal)
                {
                    Hint("Local files are available on desktop and Android only; WebGL needs a URL.");
                }
            }
            else if (_videoInput == VideoInputMode.Url)
            {
                _videoUrl = InlineField("Video URL", _videoUrl);
                Hint("https://example.com/clip.mp4, D:/media/clip.mp4, or /home/user/clip.mp4");
            }
            else
            {
                Hint("Uses the first available webcam. Android asks for camera permission on start.");
            }
        }

        private static readonly string[] VideoFileExtensions = { ".mp4", ".webm", ".mov", ".m4v", ".ogv" };

        /// <summary>True when the path ends with a video container the VideoPlayer can open.</summary>
        public static bool IsSupportedVideoFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string extension;
            try
            {
                extension = Path.GetExtension(path);
            }
            catch (Exception)
            {
                return false;
            }

            if (string.IsNullOrEmpty(extension))
            {
                return false;
            }

            for (int i = 0; i < VideoFileExtensions.Length; i++)
            {
                if (string.Equals(extension, VideoFileExtensions[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Folder the local-file browser opens when no previous folder is known.</summary>
        public static string GetDefaultBrowseDirectory()
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                return string.Empty;
            }

            try
            {
                string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                if (!string.IsNullOrEmpty(videos) && Directory.Exists(videos))
                {
                    return videos;
                }

                string home = GetHomeDirectory();
                if (!string.IsNullOrEmpty(home))
                {
                    return home;
                }
            }
            catch (Exception)
            {
                // Fall back to the app data folder below.
            }

            return Application.persistentDataPath ?? string.Empty;
        }

        private static string GetHomeDirectory()
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
                {
                    return home;
                }
            }
            catch (Exception)
            {
                // Ignore and try the Android shared storage root.
            }

            if (Directory.Exists("/storage/emulated/0"))
            {
                return "/storage/emulated/0";
            }

            return Application.persistentDataPath ?? string.Empty;
        }

        private static string GetVideosDirectory()
        {
            try
            {
                string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                if (!string.IsNullOrEmpty(videos) && Directory.Exists(videos))
                {
                    return videos;
                }
            }
            catch (Exception)
            {
                // Try the usual Android and desktop locations below.
            }

            if (Directory.Exists("/storage/emulated/0/Movies"))
            {
                return "/storage/emulated/0/Movies";
            }

            string movies = Path.Combine(GetHomeDirectory(), "Movies");
            return Directory.Exists(movies) ? movies : GetDefaultBrowseDirectory();
        }

        private static string GetDownloadsDirectory()
        {
            string downloads = Path.Combine(GetHomeDirectory(), "Downloads");
            return Directory.Exists(downloads) ? downloads : GetHomeDirectory();
        }

        private bool HasUsableLocalClip()
        {
            if (string.IsNullOrWhiteSpace(localClipPath))
            {
                return false;
            }

            try
            {
                return File.Exists(localClipPath);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string GetDisplayFileName(string path)
        {
            try
            {
                string name = Path.GetFileName(path);
                return string.IsNullOrEmpty(name) ? path : name;
            }
            catch (Exception)
            {
                return path;
            }
        }

        private void OpenFileBrowser()
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                return;
            }

            _fileBrowserOpen = true;
            _browseStatus = string.Empty;
            _browseScroll = Vector2.zero;

            if (!string.IsNullOrWhiteSpace(localClipPath))
            {
                try
                {
                    string folder = Path.GetDirectoryName(localClipPath);
                    if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    {
                        _browsePath = folder;
                    }
                }
                catch (Exception)
                {
                    // Keep the previous path.
                }
            }

            if (string.IsNullOrEmpty(_browsePath))
            {
                _browsePath = GetDefaultBrowseDirectory();
            }

            BrowseFolder(_browsePath);
        }

        private void NavigateUp()
        {
            try
            {
                DirectoryInfo parent = Directory.GetParent(_browsePath);
                if (parent != null)
                {
                    BrowseFolder(parent.FullName);
                }
            }
            catch (Exception)
            {
                _browseStatus = "This folder has no parent.";
            }
        }

        private void BrowseFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                _browseStatus = "Enter a folder path.";
                return;
            }

            try
            {
                string full = Path.GetFullPath(path.Trim());
                if (!Directory.Exists(full))
                {
                    _browsePath = path.Trim();
                    ClearBrowseLists();
                    _browseStatus = "Folder not found.";
                    return;
                }

                _browsePath = full;
                _browseScroll = Vector2.zero;
                ReloadFileBrowser();
            }
            catch (Exception exception)
            {
                ClearBrowseLists();
                _browseStatus = "That path is not valid (" + exception.GetType().Name + ").";
            }
        }

        private void ClearBrowseLists()
        {
            _browseFolders.Clear();
            _browseFiles.Clear();
        }

        private void ReloadFileBrowser()
        {
            ClearBrowseLists();
            _browseStatus = string.Empty;

            if (string.IsNullOrEmpty(_browsePath) || !Directory.Exists(_browsePath))
            {
                _browseStatus = "Folder not found.";
                return;
            }

            bool truncated = false;
            try
            {
                string[] folders = Directory.GetDirectories(_browsePath);
                Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < folders.Length; i++)
                {
                    string name = Path.GetFileName(folders[i]);
                    if (string.IsNullOrEmpty(name) || name[0] == '.' || name[0] == '$')
                    {
                        continue;
                    }

                    if (_browseFolders.Count >= 300)
                    {
                        truncated = true;
                        break;
                    }

                    _browseFolders.Add(folders[i]);
                }

                string[] files = Directory.GetFiles(_browsePath);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Length; i++)
                {
                    if (!IsSupportedVideoFile(files[i]))
                    {
                        continue;
                    }

                    string name = Path.GetFileName(files[i]);
                    if (string.IsNullOrEmpty(name) || name[0] == '.')
                    {
                        continue;
                    }

                    if (_browseFiles.Count >= 300)
                    {
                        truncated = true;
                        break;
                    }

                    _browseFiles.Add(files[i]);
                }

                if (truncated)
                {
                    _browseStatus = "Showing the first 300 items.";
                }
            }
            catch (Exception exception)
            {
                ClearBrowseLists();
                _browseStatus = "Cannot read this folder (" + exception.GetType().Name + ").";
            }
        }

        private void SelectLocalClip(string path)
        {
            localClipPath = path;
            _videoInput = VideoInputMode.Clip;
            _fileBrowserOpen = false;
            SaveSettings();
            SetStatus("Local video: " + GetDisplayFileName(path), false);
        }

        private void DrawFileBrowser(Rect rect)
        {
            GUILayout.BeginArea(rect);
            _contentWidth = Mathf.Max(220f, rect.width - 18f);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Local video", _sectionStyle, GUILayout.Height(28f));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Back", _secondaryButtonStyle, GUILayout.Width(72f), GUILayout.Height(28f)))
            {
                _fileBrowserOpen = false;
            }
            GUILayout.EndHorizontal();
            DrawDivider();

            float quickWidth = Mathf.Clamp((rect.width - 12f) / 4f, 52f, 92f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Up", _secondaryButtonStyle, GUILayout.Width(quickWidth), GUILayout.Height(28f)))
            {
                NavigateUp();
            }

            if (GUILayout.Button("Videos", _secondaryButtonStyle, GUILayout.Width(quickWidth), GUILayout.Height(28f)))
            {
                BrowseFolder(GetVideosDirectory());
            }

            if (GUILayout.Button("Downloads", _secondaryButtonStyle, GUILayout.Width(quickWidth), GUILayout.Height(28f)))
            {
                BrowseFolder(GetDownloadsDirectory());
            }

            if (GUILayout.Button("App", _secondaryButtonStyle, GUILayout.Width(quickWidth), GUILayout.Height(28f)))
            {
                BrowseFolder(Application.persistentDataPath);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Folder", _fieldLabelStyle,
                GUILayout.Width(FieldLabelWidth()), GUILayout.Height(28f));
            _browsePath = GUILayout.TextField(_browsePath ?? string.Empty, 512,
                _fieldStyle, GUILayout.Height(28f));
            if (GUILayout.Button("Go", _secondaryButtonStyle, GUILayout.Width(44f), GUILayout.Height(28f)))
            {
                BrowseFolder(_browsePath);
            }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_browseStatus))
            {
                Color previous = GUI.color;
                GUI.color = _browseStatus.StartsWith("No ", StringComparison.Ordinal)
                    ? TextMuted
                    : WarningColor;
                GUILayout.Label(_browseStatus, _hintStyle);
                GUI.color = previous;
            }

            string openFolder = null;
            string openFile = null;

            _browseScroll = GUILayout.BeginScrollView(_browseScroll, false, true,
                GUILayout.ExpandHeight(true));

            if (_browseFolders.Count == 0 && _browseFiles.Count == 0)
            {
                GUILayout.Label("Open another folder or use Up.", _hintStyle);
            }

            for (int i = 0; i < _browseFolders.Count; i++)
            {
                string folder = _browseFolders[i];
                if (GUILayout.Button(GetDisplayFileName(folder) + "/", _secondaryButtonStyle,
                        GUILayout.Height(28f)))
                {
                    openFolder = folder;
                    break;
                }
            }

            if (string.IsNullOrEmpty(openFolder))
            {
                for (int i = 0; i < _browseFiles.Count; i++)
                {
                    string file = _browseFiles[i];
                    bool selected = string.Equals(file, localClipPath, StringComparison.OrdinalIgnoreCase);
                    string label = GetDisplayFileName(file) + (selected ? "  (selected)" : string.Empty);

                    if (GUILayout.Button(label, selected ? _primaryButtonStyle : _secondaryButtonStyle,
                            GUILayout.Height(28f)))
                    {
                        openFile = file;
                        break;
                    }
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();

            if (!string.IsNullOrEmpty(openFile))
            {
                SelectLocalClip(openFile);
            }
            else if (!string.IsNullOrEmpty(openFolder))
            {
                BrowseFolder(openFolder);
            }
        }

        private void DrawQualitySettings()
        {
            Section("Quality");
            _resolutionScale = PercentSlider("Resolution", _resolutionScale, 0.1f, 1f);
            _targetFps = Mathf.RoundToInt(Slider("Frame rate", _targetFps, 5f, 60f, "0"));
            _jpegQuality = Mathf.RoundToInt(Slider("JPEG quality", _jpegQuality, 20f, 95f, "0"));

            DrawAudioSettings();
        }

        private void DrawAudioSettings()
        {
            Section("Audio");
            streamAudio = Toggle("Stream audio", streamAudio);

            if (!streamAudio)
            {
                return;
            }

            int rateOption = audioSampleRate >= 48000 ? 2 : (audioSampleRate <= 16000 ? 0 : 1);
            rateOption = GUILayout.Toolbar(rateOption, AudioRateLabels, _tabStyle, GUILayout.Height(26f));

            audioSampleRate = rateOption == 0 ? 16000 : (rateOption == 2 ? 48000 : 24000);
            audioStereo = rateOption == 2;

            int kbps = StreamAudioProtocol.BytesPerSecond(audioSampleRate, audioStereo ? 2 : 1) / 125;
            Hint("About " + kbps + " kbps per viewer. Voice and balanced are mono; music is stereo.");
        }

        private void DrawWatchSettings()
        {
            Section("Playback");
            playAudio = Toggle("Play audio", playAudio);
            _decodeFps = Mathf.RoundToInt(Slider("Decode FPS", _decodeFps, 5f, 60f, "0"));
            Hint("Lower the decode rate on phones to save battery.");
        }

        private void DrawMultiSettings()
        {
            Section("Sources");
            Hint(_transportIndex == 0
                ? "One relay room per sender (comma or new line):"
                : "One WebSocket URL per sender (comma or new line):");

            if (_transportIndex == 0)
            {
                multiRooms = GUILayout.TextArea(multiRooms ?? string.Empty, _textAreaStyle,
                    GUILayout.Height(64f), GUILayout.ExpandWidth(true));
            }
            else
            {
                multiUrls = GUILayout.TextArea(multiUrls ?? string.Empty, _textAreaStyle,
                    GUILayout.Height(64f), GUILayout.ExpandWidth(true));
            }

            multiShowGrid = Toggle("Show all sources in a grid", multiShowGrid);
            multiPlayAudio = Toggle("Play audio from the selected source", multiPlayAudio);
            _decodeFps = Mathf.RoundToInt(Slider("Decode FPS", _decodeFps, 5f, 60f, "0"));

            if (multiShowGrid)
            {
                Hint(ParseMultiSources().Length + " source(s). " + toggleKey +
                     " hides the panel; 1-9 selects a source.");
            }
            else
            {
                Hint(ParseMultiSources().Length + " source(s). Only the selected source is shown.");
            }
        }

        private void DrawPanelFooter(Rect rect)
        {
            string reason = string.Empty;
            bool canStart = true;
            if (!IsStarted)
            {
                canStart = CanStart(out reason);
            }

            bool isError = (!IsStarted && !canStart) || _statusIsError;
            string message = !IsStarted && !canStart && !string.IsNullOrEmpty(reason)
                ? reason
                : _status;

            Color previous = GUI.color;
            GUI.color = isError ? DangerColor : SuccessColor;

            GUI.BeginGroup(new Rect(rect.x, rect.y, rect.width, 30f));
            GUI.Label(new Rect(0f, 0f, rect.width, 30f), message ?? string.Empty, _footerStatusStyle);
            GUI.EndGroup();
            GUI.color = previous;

            GUILayout.BeginArea(new Rect(rect.x, rect.y + 32f, rect.width, rect.height - 32f));
            GUILayout.BeginHorizontal();

            bool wasEnabled = GUI.enabled;

            if (!IsStarted)
            {
                GUI.enabled = canStart;
                string label = _mode == SetupMode.Stream
                    ? "Start streaming"
                    : (_mode == SetupMode.Watch
                        ? "Watch stream"
                        : "Watch " + ParseMultiSources().Length + " sources");

                if (GUILayout.Button(label, _primaryButtonStyle,
                        GUILayout.Height(40f), GUILayout.ExpandWidth(true)))
                {
                    StartSelected();
                }

                GUI.enabled = wasEnabled;
            }
            else if (GUILayout.Button("Stop and edit", _primaryButtonStyle,
                         GUILayout.Height(40f), GUILayout.ExpandWidth(true)))
            {
                StopSelected();
            }

            if (allowExit && GUILayout.Button(_exitArmed ? "Confirm exit" : "Exit",
                    _exitArmed ? _dangerButtonStyle : _secondaryButtonStyle,
                    GUILayout.Width(rect.width < 520f ? 76f : 96f), GUILayout.Height(40f)))
            {
                OnExitPressed();
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // ------------------------------------------------------------------
        // Shared widgets
        // ------------------------------------------------------------------

        private void Section(string title)
        {
            GUILayout.Space(12f);
            GUILayout.Label(title, _sectionStyle);
            DrawDivider();
            GUILayout.Space(2f);
        }

        private void SectionRow(string title, string[] labels, ref int index)
        {
            GUILayout.Space(12f);
            GUILayout.BeginHorizontal();
            GUILayout.Label(title, _sectionStyle, GUILayout.Width(84f), GUILayout.Height(26f));
            GUILayout.FlexibleSpace();
            index = GUILayout.Toolbar(index, labels, _tabStyle,
                GUILayout.Width(ToolbarWidth()), GUILayout.Height(26f));
            GUILayout.EndHorizontal();
            DrawDivider();
            GUILayout.Space(2f);
        }

        private void DrawDivider()
        {
            Rect line = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
            Color previous = GUI.color;
            GUI.color = DividerColor;
            GUI.DrawTexture(line, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private static void DrawOutline(Rect rect, Color color, float thickness)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private float ToolbarWidth()
        {
            return Mathf.Clamp(_contentWidth - 98f, 150f, 240f);
        }

        private float FieldLabelWidth()
        {
            return Mathf.Clamp(_contentWidth * 0.30f, 78f, 132f);
        }

        private string InlineField(string label, string value, int maxLength = 128)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _fieldLabelStyle,
                GUILayout.Width(FieldLabelWidth()), GUILayout.Height(26f));
            string result = GUILayout.TextField(value ?? string.Empty, maxLength,
                _fieldStyle, GUILayout.Height(26f));
            GUILayout.EndHorizontal();
            return result;
        }

        private string InlinePasswordField(string label, string value, int maxLength)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _fieldLabelStyle,
                GUILayout.Width(FieldLabelWidth()), GUILayout.Height(26f));
            string result = GUILayout.PasswordField(value ?? string.Empty, '*', maxLength,
                _fieldStyle, GUILayout.Height(26f));
            GUILayout.EndHorizontal();
            return result;
        }

        private int InlineIntField(string label, int value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _fieldLabelStyle,
                GUILayout.Width(FieldLabelWidth()), GUILayout.Height(26f));
            string text = GUILayout.TextField(value.ToString(), 5, _fieldStyle, GUILayout.Height(26f));
            GUILayout.EndHorizontal();
            return ParsePort(text, value);
        }

        private void Hint(string text)
        {
            GUILayout.Label(text, _hintStyle);
        }

        private bool Toggle(string label, bool value)
        {
            bool enabled = GUI.enabled;
            Rect row = GUILayoutUtility.GetRect(1f, 26f, GUILayout.ExpandWidth(true));
            Event current = Event.current;
            bool hovered = enabled && row.Contains(current.mousePosition);

            if (hovered)
            {
                Color previousHover = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.04f);
                GUI.DrawTexture(row, Texture2D.whiteTexture);
                GUI.color = previousHover;
            }

            Rect box = new Rect(row.x, row.y + (row.height - 16f) * 0.5f, 16f, 16f);
            Color previous = GUI.color;
            GUI.color = value ? AccentColor : new Color(1f, 1f, 1f, 0.18f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = FieldBackground;
            GUI.DrawTexture(new Rect(box.x + 2f, box.y + 2f, box.width - 4f, box.height - 4f),
                Texture2D.whiteTexture);

            if (value)
            {
                GUI.color = enabled ? AccentText : TextMuted;
                GUI.DrawTexture(new Rect(box.x + 5f, box.y + 5f, box.width - 10f, box.height - 10f),
                    Texture2D.whiteTexture);
            }

            GUI.color = enabled ? previous : new Color(1f, 1f, 1f, 0.5f);
            GUI.Label(new Rect(row.x + 26f, row.y, Mathf.Max(1f, row.width - 26f), row.height),
                label, _fieldLabelStyle);
            GUI.color = previous;

            if (enabled && current.type == EventType.MouseDown && current.button == 0 &&
                row.Contains(current.mousePosition))
            {
                value = !value;
                current.Use();
            }

            return value;
        }

        private float Slider(string label, float value, float min, float max, string format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _fieldLabelStyle,
                GUILayout.Width(FieldLabelWidth()), GUILayout.Height(26f));
            Rect row = GUILayoutUtility.GetRect(1f, 26f, GUILayout.ExpandWidth(true));
            float result = DrawSlider(row, value, min, max);
            GUILayout.Label(value.ToString(format), _valueStyle,
                GUILayout.Width(44f), GUILayout.Height(26f));
            GUILayout.EndHorizontal();
            return result;
        }

        private static float DrawSlider(Rect rect, float value, float min, float max)
        {
            const float trackHeight = 4f;
            const float thumbSize = 12f;

            bool enabled = GUI.enabled;
            float range = Mathf.Max(0.0001f, max - min);
            float t = Mathf.Clamp01((value - min) / range);
            Rect track = new Rect(rect.x + thumbSize * 0.5f,
                rect.y + (rect.height - trackHeight) * 0.5f,
                Mathf.Max(1f, rect.width - thumbSize), trackHeight);

            Color previous = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.10f);
            GUI.DrawTexture(track, Texture2D.whiteTexture);

            GUI.color = enabled ? AccentColor : TextMuted;
            GUI.DrawTexture(new Rect(track.x, track.y, track.width * t, track.height),
                Texture2D.whiteTexture);

            GUI.color = enabled ? new Color(0.82f, 0.92f, 0.97f, 1f) : TextMuted;
            GUI.DrawTexture(new Rect(track.x + track.width * t - thumbSize * 0.5f,
                rect.y + (rect.height - thumbSize) * 0.5f, thumbSize, thumbSize), Texture2D.whiteTexture);
            GUI.color = previous;

            if (!enabled)
            {
                return value;
            }

            int id = GUIUtility.GetControlID(FocusType.Passive);
            Event current = Event.current;
            switch (current.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (current.button == 0 && rect.Contains(current.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        value = SliderValueAt(current.mousePosition.x, track, min, range);
                        current.Use();
                    }

                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        value = SliderValueAt(current.mousePosition.x, track, min, range);
                        current.Use();
                    }

                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        current.Use();
                    }

                    break;
            }

            return value;
        }

        private static float SliderValueAt(float x, Rect track, float min, float range)
        {
            float t = Mathf.Clamp01((x - track.x) / Mathf.Max(1f, track.width));
            return min + range * t;
        }

        private float PercentSlider(string label, float value, float min, float max)
        {
            return Slider(label, value * 100f, min * 100f, max * 100f, "0") / 100f;
        }

        private void DrawRelayConnectionStatus()
        {
            if (_mode == SetupMode.Multi)
            {
                int total = multiReceiver != null ? multiReceiver.SourceCount : 0;
                int connectedSources = multiReceiver != null ? multiReceiver.ConnectedSourceCount : 0;
                Color previousMultiColor = GUI.color;
                GUI.color = connectedSources > 0 ? SuccessColor : WarningColor;

                GUILayout.Label(connectedSources > 0
                    ? "Relay: connected to " + connectedSources + " of " + total + " sources."
                    : "Relay: waiting for senders in the listed rooms.", _smallStyle);

                GUI.color = previousMultiColor;
                return;
            }

            INetworkTransport transport = _mode == SetupMode.Stream
                ? (sender != null ? sender.Transport : null)
                : (receiver != null ? receiver.Transport : null);

            int peers = transport != null ? transport.PeerCount : 0;
            bool connected = peers > 0;
            bool connecting = !connected && Time.unscaledTime - _startedAt < 2f;

            string message;
            if (transport == null)
            {
                message = "Relay: starting...";
            }
            else if (connected)
            {
                if (_mode == SetupMode.Stream)
                {
                    long sent = transport.Statistics.FramesSent;
                    message = sent > 0
                        ? "Relay: connected. Frames sent: " + sent + "."
                        : "Relay: connected, but no frame has been captured yet. Check the video source.";
                }
                else if (receiver != null && receiver.DecodedFrames > 0)
                {
                    message = "Relay: receiving frames (" + receiver.DecodedFrames + " decoded).";
                }
                else
                {
                    message = "Relay: connected. Waiting for a sender frame in room '" + _relayRoom + "'.";
                    if (receiver != null && receiver.DecodeErrors > 0)
                    {
                        message += " Decode errors: " + receiver.DecodeErrors + ".";
                    }
                }
            }
            else if (connecting)
            {
                message = "Relay: connecting to " + RelayHostForDisplay() + "...";
            }
            else
            {
                message = "Relay: not connected to " + RelayHostForDisplay() +
                          ". Check the relay, token, Wi-Fi, and port 8787.";
            }

            Color previous = GUI.color;
            GUI.color = connected
                ? SuccessColor
                : (connecting ? WarningColor : DangerColor);

            GUILayout.Label(message, _smallStyle);
            GUI.color = previous;
        }

        private string RelayHostForDisplay()
        {
            var config = TransportConfig.ParseWebSocketUrl(_relayUrl);
            if (string.IsNullOrEmpty(config.RemoteHost))
            {
                return "the relay";
            }

            return config.RemoteHost + ":" + config.RemotePort;
        }

        private Texture GetPreviewTexture()
        {
            if (_mode == SetupMode.Stream)
            {
                return sender != null ? sender.PreviewTexture : null;
            }

            if (_mode == SetupMode.Multi)
            {
                return multiReceiver != null && multiReceiver.IsReceiving
                    ? multiReceiver.GetSourceTexture(multiReceiver.ActiveSourceIndex)
                    : null;
            }

            return receiver != null ? receiver.StreamTexture : null;
        }

        private string GetLiveSummary()
        {
            if (_mode == SetupMode.Stream)
            {
                if (sender == null)
                {
                    return "No sender in this scene.";
                }

                if (!sender.IsStreaming)
                {
                    return "Ready to stream.";
                }

                string link;
                if (sender.Transport == null)
                {
                    link = "starting";
                }
                else if (sender.Transport.PeerCount > 0)
                {
                    link = sender.transportKind == TransportKind.Relay
                        ? "relay connected"
                        : "peers " + sender.Transport.PeerCount;
                }
                else
                {
                    link = sender.transportKind == TransportKind.Relay
                        ? "relay not connected"
                        : "no peers";
                }

                return "Streaming | " + sender.transportKind + " | " + link;
            }

            if (_mode == SetupMode.Multi)
            {
                if (multiReceiver == null || !multiReceiver.IsReceiving)
                {
                    return "Ready to watch several sources.";
                }

                return "Watching " + multiReceiver.SourceCount + " sources | connected " +
                       multiReceiver.ConnectedSourceCount + " | active " + multiReceiver.ActiveSourceIndex +
                       (multiReceiver.showAllSources ? " | grid" : " | single");
            }

            if (receiver == null)
            {
                return "No receiver in this scene.";
            }

            if (!receiver.IsReceiving)
            {
                return "Ready to watch.";
            }

            string connection = receiver.Transport == null
                ? "starting"
                : (receiver.Transport.PeerCount > 0 ? "connected" : "not connected");

            return "Watching | " + receiver.transportKind + " | " + connection +
                   " | frames " + receiver.DecodedFrames +
                   " | skipped " + receiver.SkippedFrames;
        }

        private void OnExitPressed()
        {
            if (!_exitArmed)
            {
                _exitArmed = true;
                _exitArmedUntil = Time.unscaledTime + 3f;
                SetStatus("Press Exit again to close the application.", false);
                return;
            }

            _exitArmed = false;
            StopSelected();

#if UNITY_WEBGL && !UNITY_EDITOR
            SetStatus("Exit is not available in the browser. Close the tab instead.", true);
#else
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
#endif
        }

        private static int ParsePort(string text, int fallback)
        {
            int port;
            return int.TryParse(text, out port) && port > 0 && port <= 65535 ? port : fallback;
        }

        private static bool IsValidRoom(string room)
        {
            if (string.IsNullOrEmpty(room) || room.Length > 64)
            {
                return false;
            }

            for (int i = 0; i < room.Length; i++)
            {
                char c = room[i];
                bool valid = (c >= 'a' && c <= 'z') ||
                             (c >= 'A' && c <= 'Z') ||
                             (c >= '0' && c <= '9') ||
                             c == '_' || c == '-';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidWebSocketUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            var config = TransportConfig.ParseWebSocketUrl(url);
            return !string.IsNullOrEmpty(config.RemoteHost) && config.RemotePort > 0;
        }

        private static bool IsRelayUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            var config = TransportConfig.ParseWebSocketUrl(url);
            return config.WebSocketPath != null &&
                   config.WebSocketPath.IndexOf("fxm-relay", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsLoopbackHost(string url)
        {
            var config = TransportConfig.ParseWebSocketUrl(url);
            string host = config.RemoteHost;
            if (string.IsNullOrEmpty(host))
            {
                return false;
            }

            return host == "127.0.0.1" ||
                   host == "0.0.0.0" ||
                   host == "::1" ||
                   string.Equals(host, "localhost", System.StringComparison.OrdinalIgnoreCase);
        }

        private void EnsureStyles()
        {
            if (_stylesReady && _darkSkin != null)
            {
                return;
            }

            _themeTextures = new List<Texture2D>();

            Texture2D control = ThemeTexture(ControlBackground);
            Texture2D controlHover = ThemeTexture(ControlHover);
            Texture2D controlActive = ThemeTexture(ControlActive);
            Texture2D accent = ThemeTexture(AccentColor);
            Texture2D accentHover = ThemeTexture(AccentHover);
            Texture2D accentActive = ThemeTexture(AccentActive);
            Texture2D field = ThemeTexture(FieldBackground);
            Texture2D fieldFocused = ThemeTexture(FieldFocused);
            Texture2D danger = ThemeTexture(new Color(0.560f, 0.200f, 0.200f, 1f));
            Texture2D dangerHover = ThemeTexture(new Color(0.680f, 0.250f, 0.250f, 1f));
            Texture2D overlay = ThemeTexture(new Color(1f, 1f, 1f, 0.12f));
            Texture2D overlayHover = ThemeTexture(new Color(1f, 1f, 1f, 0.20f));
            Texture2D overlayActive = ThemeTexture(new Color(1f, 1f, 1f, 0.28f));
            Texture2D scrollTrack = ThemeTexture(new Color(0.043f, 0.051f, 0.063f, 1f));
            Texture2D scrollThumb = ThemeTexture(new Color(0.239f, 0.278f, 0.333f, 1f));

            // A dark clone of the built-in skin keeps the controls this panel does not
            // draw itself (scrollbar thumbs, text selection, focus rings) from falling
            // back to the light runtime skin.
            _darkSkin = Instantiate(GUI.skin);
            _darkSkin.name = "FXM WebStream Dark";
            _darkSkin.hideFlags = HideFlags.HideAndDontSave;

            GUIStyle skinLabel = new GUIStyle(GUI.skin.label);
            skinLabel.normal.textColor = TextSecondary;
            _darkSkin.label = skinLabel;

            GUIStyle skinBox = new GUIStyle(GUI.skin.box);
            skinBox.normal.background = control;
            _darkSkin.box = skinBox;

            GUIStyle skinButton = new GUIStyle(GUI.skin.button);
            skinButton.normal.background = control;
            skinButton.hover.background = controlHover;
            skinButton.active.background = controlActive;
            skinButton.focused.background = controlHover;
            skinButton.onNormal.background = accent;
            skinButton.onHover.background = accentHover;
            skinButton.onActive.background = accentActive;
            skinButton.normal.textColor = TextPrimary;
            skinButton.hover.textColor = TextPrimary;
            skinButton.active.textColor = TextPrimary;
            skinButton.focused.textColor = TextPrimary;
            skinButton.onNormal.textColor = Color.white;
            skinButton.onHover.textColor = Color.white;
            skinButton.onActive.textColor = Color.white;
            skinButton.border = new RectOffset(0, 0, 0, 0);
            _darkSkin.button = skinButton;

            GUIStyle skinField = new GUIStyle(GUI.skin.textField);
            skinField.normal.background = field;
            skinField.hover.background = fieldFocused;
            skinField.active.background = fieldFocused;
            skinField.focused.background = fieldFocused;
            skinField.normal.textColor = TextPrimary;
            skinField.hover.textColor = Color.white;
            skinField.active.textColor = Color.white;
            skinField.focused.textColor = Color.white;
            skinField.padding = new RectOffset(8, 8, 4, 4);
            skinField.border = new RectOffset(0, 0, 0, 0);
            _darkSkin.textField = skinField;

            GUIStyle skinTextArea = new GUIStyle(skinField);
            skinTextArea.wordWrap = true;
            skinTextArea.alignment = TextAnchor.UpperLeft;
            skinTextArea.padding = new RectOffset(8, 8, 6, 6);
            _darkSkin.textArea = skinTextArea;

            GUIStyle skinToggle = new GUIStyle(GUI.skin.toggle);
            skinToggle.normal.textColor = TextPrimary;
            skinToggle.hover.textColor = TextPrimary;
            skinToggle.active.textColor = TextPrimary;
            skinToggle.onNormal.textColor = TextPrimary;
            skinToggle.onHover.textColor = TextPrimary;
            _darkSkin.toggle = skinToggle;

            GUIStyle skinScrollTrack = new GUIStyle(GUI.skin.verticalScrollbar);
            skinScrollTrack.normal.background = scrollTrack;
            skinScrollTrack.hover.background = scrollTrack;
            skinScrollTrack.active.background = scrollTrack;
            skinScrollTrack.fixedWidth = 8;
            skinScrollTrack.border = new RectOffset(0, 0, 0, 0);
            _darkSkin.verticalScrollbar = skinScrollTrack;

            GUIStyle skinScrollThumb = new GUIStyle(GUI.skin.verticalScrollbarThumb);
            skinScrollThumb.normal.background = scrollThumb;
            skinScrollThumb.hover.background = scrollThumb;
            skinScrollThumb.active.background = scrollThumb;
            skinScrollThumb.fixedWidth = 8;
            skinScrollThumb.border = new RectOffset(0, 0, 0, 0);
            _darkSkin.verticalScrollbarThumb = skinScrollThumb;

            GUIStyle skinHorizontalTrack = new GUIStyle(GUI.skin.horizontalScrollbar);
            skinHorizontalTrack.normal.background = scrollTrack;
            skinHorizontalTrack.hover.background = scrollTrack;
            skinHorizontalTrack.active.background = scrollTrack;
            skinHorizontalTrack.fixedHeight = 8;
            skinHorizontalTrack.border = new RectOffset(0, 0, 0, 0);
            _darkSkin.horizontalScrollbar = skinHorizontalTrack;

            GUIStyle skinHorizontalThumb = new GUIStyle(GUI.skin.horizontalScrollbarThumb);
            skinHorizontalThumb.normal.background = scrollThumb;
            skinHorizontalThumb.hover.background = scrollThumb;
            skinHorizontalThumb.active.background = scrollThumb;
            skinHorizontalThumb.fixedHeight = 8;
            skinHorizontalThumb.border = new RectOffset(0, 0, 0, 0);
            _darkSkin.horizontalScrollbarThumb = skinHorizontalThumb;

            GUIStyle skinScrollView = new GUIStyle(GUI.skin.scrollView);
            skinScrollView.normal.background = null;
            _darkSkin.scrollView = skinScrollView;

            _titleStyle = new GUIStyle(_darkSkin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                wordWrap = false
            };
            _titleStyle.normal.textColor = TextPrimary;

            _sectionStyle = new GUIStyle(_darkSkin.label)
            {
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                wordWrap = false,
                alignment = TextAnchor.MiddleLeft
            };
            _sectionStyle.normal.textColor = AccentText;

            _smallStyle = new GUIStyle(_darkSkin.label)
            {
                fontSize = 10,
                wordWrap = true
            };
            _smallStyle.normal.textColor = TextPrimary;

            _hintStyle = new GUIStyle(_darkSkin.label)
            {
                fontSize = 10,
                wordWrap = true
            };
            _hintStyle.normal.textColor = TextMuted;

            _fieldLabelStyle = new GUIStyle(_darkSkin.label)
            {
                fontSize = 11,
                wordWrap = false,
                alignment = TextAnchor.MiddleLeft
            };
            _fieldLabelStyle.normal.textColor = TextSecondary;

            _valueStyle = new GUIStyle(_darkSkin.label)
            {
                fontSize = 11,
                wordWrap = false,
                alignment = TextAnchor.MiddleRight
            };
            _valueStyle.normal.textColor = TextPrimary;

            _centerStyle = new GUIStyle(_darkSkin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                fontSize = 11
            };
            _centerStyle.normal.textColor = TextMuted;

            _footerStatusStyle = new GUIStyle(_darkSkin.label)
            {
                fontSize = 11,
                wordWrap = true
            };
            _footerStatusStyle.normal.textColor = Color.white;

            _secondaryButtonStyle = new GUIStyle(_darkSkin.button)
            {
                fontSize = 11,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(12, 12, 6, 6)
            };
            _secondaryButtonStyle.normal.background = control;
            _secondaryButtonStyle.hover.background = controlHover;
            _secondaryButtonStyle.active.background = controlActive;
            _secondaryButtonStyle.focused.background = controlHover;
            _secondaryButtonStyle.normal.textColor = TextPrimary;
            _secondaryButtonStyle.hover.textColor = Color.white;
            _secondaryButtonStyle.active.textColor = Color.white;
            _secondaryButtonStyle.border = new RectOffset(0, 0, 0, 0);

            _primaryButtonStyle = new GUIStyle(_secondaryButtonStyle)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(14, 14, 8, 8)
            };
            _primaryButtonStyle.normal.background = accent;
            _primaryButtonStyle.hover.background = accentHover;
            _primaryButtonStyle.active.background = accentActive;
            _primaryButtonStyle.focused.background = accentHover;
            _primaryButtonStyle.normal.textColor = Color.white;
            _primaryButtonStyle.hover.textColor = Color.white;
            _primaryButtonStyle.active.textColor = Color.white;

            _dangerButtonStyle = new GUIStyle(_secondaryButtonStyle)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            _dangerButtonStyle.normal.background = danger;
            _dangerButtonStyle.hover.background = dangerHover;
            _dangerButtonStyle.active.background = danger;
            _dangerButtonStyle.focused.background = dangerHover;
            _dangerButtonStyle.normal.textColor = Color.white;
            _dangerButtonStyle.hover.textColor = Color.white;
            _dangerButtonStyle.active.textColor = Color.white;

            _fieldStyle = new GUIStyle(_darkSkin.textField)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 8, 4, 4)
            };
            _fieldStyle.border = new RectOffset(0, 0, 0, 0);

            _textAreaStyle = new GUIStyle(_darkSkin.textArea)
            {
                fontSize = 11,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(8, 8, 6, 6)
            };
            _textAreaStyle.border = new RectOffset(0, 0, 0, 0);

            _tabStyle = new GUIStyle(_darkSkin.button)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 5, 5)
            };
            _tabStyle.normal.background = control;
            _tabStyle.hover.background = controlHover;
            _tabStyle.active.background = controlActive;
            _tabStyle.onNormal.background = accent;
            _tabStyle.onHover.background = accentHover;
            _tabStyle.onActive.background = accentActive;
            _tabStyle.normal.textColor = TextSecondary;
            _tabStyle.hover.textColor = Color.white;
            _tabStyle.active.textColor = Color.white;
            _tabStyle.onNormal.textColor = Color.white;
            _tabStyle.onHover.textColor = Color.white;
            _tabStyle.onActive.textColor = Color.white;
            _tabStyle.border = new RectOffset(0, 0, 0, 0);

            _selectionGridStyle = new GUIStyle(_tabStyle)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(10, 10, 5, 5)
            };

            _overlayLabelStyle = new GUIStyle(_darkSkin.label)
            {
                fontSize = 11,
                wordWrap = false,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };
            _overlayLabelStyle.normal.textColor = Color.white;

            _overlaySmallStyle = new GUIStyle(_darkSkin.label)
            {
                fontSize = 10,
                wordWrap = false,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };
            _overlaySmallStyle.normal.textColor = new Color(0.92f, 0.94f, 0.96f);

            _overlayButtonStyle = new GUIStyle(_darkSkin.button)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(6, 6, 4, 4)
            };
            _overlayButtonStyle.normal.background = overlay;
            _overlayButtonStyle.hover.background = overlayHover;
            _overlayButtonStyle.active.background = overlayActive;
            _overlayButtonStyle.focused.background = overlayHover;
            _overlayButtonStyle.normal.textColor = Color.white;
            _overlayButtonStyle.hover.textColor = Color.white;
            _overlayButtonStyle.active.textColor = Color.white;
            _overlayButtonStyle.border = new RectOffset(0, 0, 0, 0);

            _fullscreenMessageStyle = new GUIStyle(_darkSkin.label)
            {
                fontSize = 13,
                wordWrap = true,
                alignment = TextAnchor.MiddleCenter
            };
            _fullscreenMessageStyle.normal.textColor = TextSecondary;

            _stylesReady = true;
        }

        private Texture2D ThemeTexture(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.SetPixel(0, 0, color);
            texture.Apply();
            _themeTextures.Add(texture);
            return texture;
        }
    }
}
