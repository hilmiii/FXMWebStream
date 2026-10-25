using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FXM.WebStream
{
    /// <summary>Pixel readback method.</summary>
    public enum CaptureMethod
    {
        /// <summary>Asynchronous GPU to CPU copy. Default; does not block the main thread.</summary>
        AsyncGPUReadback = 0,

        /// <summary>Synchronous Texture2D.ReadPixels. Fallback for platforms without AsyncGPUReadback.</summary>
        ReadPixels = 1
    }

    /// <summary>
    /// Sender component.
    ///
    /// Per-frame flow:
    /// 1. A camera (or VideoPlayer/RenderTexture) fills a downscaled RenderTexture.
    /// 2. Pixels are read with AsyncGPUReadback (default) or ReadPixels.
    /// 3. The RGBA copy goes to <see cref="AsyncJpegEncoder"/>, which encodes JPEG on a
    ///    worker thread so the main thread never waits for encoding.
    /// 4. The JPEG is dequeued in Update and sent through <see cref="INetworkTransport"/>.
    ///
    /// Playback control:
    /// - The sender owns the state (Playing/Paused/Stopped). It publishes the state on the
    ///   control channel whenever it changes and as a one-second heartbeat, so late receivers
    ///   stay in sync.
    /// - Receiver commands (play/pause/stop/restart/seek/quality/fps) use the same transport
    ///   when <see cref="allowRemoteControl"/> is enabled.
    /// - When the source is a <see cref="VideoPlayerStreamSource"/>, those commands also drive
    ///   the underlying VideoPlayer.
    ///
    /// Topologies:
    /// - One-to-many UDP: fill <see cref="udpReceivers"/> or call <see cref="AddUdpReceiver"/>.
    /// - One-to-many WebSocket: the server broadcasts to every connected client.
    /// - Many-to-one / many-to-many: run multiple senders and add a
    ///   <see cref="MultiStreamReceiver"/> on the receiving side.
    /// - Internet: use <see cref="TransportKind.Relay"/> so both peers connect outbound.
    ///
    /// All buffers are pooled; steady state allocates no memory per frame.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FXM WebStream/Streaming Sender")]
    public sealed class StreamingSender : MonoBehaviour, IStreamPlaybackController
    {
        [Header("Image Source")]
        [Tooltip("Camera to render. Empty = Camera.main when no source texture or video source is set.")]
        public Camera sourceCamera;

        [Tooltip("Optional. When set, this texture is captured instead (for example another camera's RenderTexture).")]
        public RenderTexture sourceTexture;

        [Tooltip("Optional. Component implementing IStreamVideoSource (for example VideoPlayerStreamSource).")]
        public MonoBehaviour videoSourceBehaviour;

        [Tooltip("When enabled, a video source found on this GameObject or its children is used automatically.")]
        public bool autoDetectVideoSource = true;

        [Tooltip("Output resolution scale (0.25 = 1/4 width and height). Downscaling happens while the source renders.")]
        [Range(0.1f, 1f)]
        public float resolutionScale = 1f;

        [Tooltip("AsyncGPUReadback is fast and asynchronous. ReadPixels is synchronous and works as a compatibility fallback.")]
        public CaptureMethod captureMethod = CaptureMethod.AsyncGPUReadback;

        [Tooltip("Flip row order while encoding. Keep enabled for Unity captures, whose texture rows start at the bottom.")]
        public bool flipVertically = true;

        [Header("Encoding")]
        [Tooltip("Maximum frames sent per second.")]
        [Range(1, 240)]
        public int targetFps = 30;

        [Tooltip("JPEG quality 1..100. 60-80 is usually enough for streaming.")]
        [Range(1, 100)]
        public int jpegQuality = 70;

        [Tooltip("Encoder thread count. Increase when using a large resolution and the CPU still has headroom.")]
        [Range(1, 4)]
        public int encoderThreads = 1;

        [Header("Playback")]
        [Tooltip("Start in Playing state when StartStreaming runs. When disabled, the stream starts Stopped.")]
        public bool autoPlay = true;

        [Tooltip("Accept play/pause/stop/seek commands from receivers. There is no authentication; enable only on trusted networks.")]
        public bool allowRemoteControl = true;

        [Tooltip("Local UDP port used to receive playback commands. Must differ from the receiver's frame port.")]
        public int localControlPort = 47800;

        [Header("Network")]
        public TransportKind transportKind = TransportKind.Udp;

        [Tooltip("Primary UDP destination address (LAN or public IP).")]
        public string remoteHost = "127.0.0.1";

        [Tooltip("UDP destination port, or the WebSocket server port when webSocketServer is enabled.")]
        public int remotePort = 47777;

        [Tooltip("Additional UDP destinations for one-to-many, in \"host:port\" format.")]
        public string[] udpReceivers = new string[0];

        [Tooltip("WebSocket URL for client mode, and the source of host/port/path in server mode.")]
        public string webSocketUrl = "ws://127.0.0.1:47778/fxm-stream/";

        [Tooltip("When enabled, the sender runs a WebSocket server (required for browser/WebGL clients).")]
        public bool webSocketServer = true;

        [Header("Internet Relay")]
        [Tooltip("Public relay WebSocket URL. Use wss:// in production. Used when transportKind = Relay.")]
        public string relayUrl = "ws://127.0.0.1:8787/fxm-relay";

        [Tooltip("Room name shared by this sender and its receivers. One sender per room.")]
        public string relayRoom = "demo";

        [Tooltip("Optional shared secret. Must match the relay server's FXM_RELAY_TOKEN value.")]
        public string relayToken = "";

        [Tooltip("Maximum UDP datagram size including the header. 1200 is safe for the internet and VPNs.")]
        [Range(576, 1400)]
        public int mtu = 1200;

        [Header("Audio")]
        [Tooltip("Stream the scene audio mix (AudioListener), including VideoPlayer clips routed through an AudioSource.")]
        public bool streamAudio;

        [Tooltip("Audio sample rate sent to receivers. 24000 mono is a good balance.")]
        [Range(StreamAudioProtocol.MinSampleRate, StreamAudioProtocol.MaxSampleRate)]
        public int audioSampleRate = 24000;

        [Tooltip("Send stereo instead of mono. Doubles audio bandwidth.")]
        public bool audioStereo;

        [Tooltip("Capture gain applied before encoding.")]
        [Range(0f, 2f)]
        public float audioGain = 1f;

        [Tooltip("Optional capture component. When empty, one is added to the AudioListener automatically.")]
        public StreamAudioCapture audioCapture;

        [Tooltip("Start automatically when the component becomes active.")]
        public bool autoStart = true;

        [Header("Diagnostics")]
        public bool showStatistics;

        /// <summary>Raised when the sender playback state changes.</summary>
        public event Action<StreamPlaybackState> PlaybackStateChanged;

        private Camera _camera;
        private RenderTexture _captureTexture;
        private RenderTexture _previousCameraTarget;
        private Texture2D _readbackTexture;
        private INetworkTransport _transport;
        private AsyncJpegEncoder _encoder;
        private FrameBufferPool _capturePool;
        private IStreamVideoSource _videoSource;

        private bool _running;
        private bool _ownsCaptureTexture;
#if !UNITY_WEBGL || UNITY_EDITOR
        private bool _loggedReadbackError;
#endif
        private bool _loggedRemoteBlocked;
        private int _captureWidth;
        private int _captureHeight;
#if !UNITY_WEBGL || UNITY_EDITOR
        private int _pendingReadbacks;
#endif
        private float _nextCaptureTime;
        private int _warmupFrames;
        private int _captureGeneration;
        private readonly Queue<int> _readbackGenerations = new Queue<int>(4);

        private byte[] _audioPacket;
        private uint _audioSequence;
        private long _audioSampleClock;

        private StreamPlaybackState _playbackState = StreamPlaybackState.Stopped;
        private uint _sourceId;
        private uint _controlSequence;
        private float _lastStateBroadcastTime;
        private float _lastKnownPositionSeconds;

        private int _capturedInWindow;
        private int _encodedInWindow;
        private int _sentInWindow;
        private float _statAccumulator;
        private float _captureFps;
        private float _encodeFps;
        private float _sendFps;
        private int _lastJpegBytes;
        private double _lastEncodeMilliseconds;

        public bool IsStreaming { get { return _running; } }
        public INetworkTransport Transport { get { return _transport; } }

        /// <summary>Current playback state (owned by the sender, mirrored to receivers).</summary>
        public StreamPlaybackState PlaybackState { get { return _playbackState; } }

        /// <summary>Unique sender id used by receivers to filter state messages.</summary>
        public uint SourceId { get { return _sourceId; } }

        public bool CanSeek { get { return _videoSource != null && _videoSource.CanSeek; } }
        public float PositionSeconds { get { return GetPositionSeconds(); } }
        public float DurationSeconds { get { return _videoSource != null ? _videoSource.DurationSeconds : 0f; } }

        /// <summary>
        /// Texture suitable for a local preview: the capture target while streaming,
        /// otherwise the video source texture or an explicit source texture.
        /// </summary>
        public Texture PreviewTexture
        {
            get
            {
                if (_videoSource != null && _videoSource.SourceTexture != null)
                {
                    return _videoSource.SourceTexture;
                }

                if (_running && _captureTexture != null)
                {
                    return _captureTexture;
                }

                return sourceTexture;
            }
        }

        private void Awake()
        {
            _sourceId = StreamIdentity.Next();
        }

        private void Start()
        {
            ResolveVideoSource();

            if (autoStart)
            {
                StartStreaming();
            }
        }

        private void Update()
        {
            if (!_running)
            {
                return;
            }

            _transport.Poll();
            PumpAudio();
            PumpEncodedFrames();
            PumpStateHeartbeat();
            UpdateStatistics();

            if (_playbackState != StreamPlaybackState.Playing)
            {
                return;
            }

            if (Time.unscaledTime < _nextCaptureTime)
            {
                return;
            }

            RequestCapture();
        }

        private void OnDisable()
        {
            StopStreaming();
        }

        private void OnDestroy()
        {
            ReleaseResources();
        }

        private void OnGUI()
        {
            if (!showStatistics)
            {
                return;
            }

            string text = _running
                ? string.Format(
                    "FXM Sender ({0}) {1} | id {2}\n{3}x{4} | capture {5:F1} fps | encode {6:F1} fps | send {7:F1} fps\nJPEG {8:F1} KB | encode {9:F1} ms | dropped {10} | peers {11}",
                    transportKind, _playbackState, _sourceId,
                    _captureWidth, _captureHeight, _captureFps, _encodeFps, _sendFps,
                    _lastJpegBytes / 1024f, _lastEncodeMilliseconds,
                    _encoder != null ? _encoder.DroppedFrames : 0,
                    _transport != null ? _transport.PeerCount : 0)
                : "FXM Sender (stopped)";

            GUI.Box(new Rect(10, 10, 540, 84), GUIContent.none);
            GUI.Label(new Rect(18, 14, 530, 80), text);
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        /// <summary>Start capture, encoding, and transport. Safe to call repeatedly.</summary>
        public void StartStreaming()
        {
            if (_running)
            {
                return;
            }

            ResolveVideoSource();

            if (!PrepareCapture())
            {
                return;
            }

            if (!CreateAndStartTransport())
            {
                ReleaseResources();
                return;
            }

            _encoder = new AsyncJpegEncoder(encoderThreads, 2, 4, flipVertically);
            _capturePool = new FrameBufferPool(3);
            _running = true;
            _captureGeneration++;
            _readbackGenerations.Clear();
#if !UNITY_WEBGL || UNITY_EDITOR
            _loggedReadbackError = false;
#endif
            _nextCaptureTime = Time.unscaledTime + 0.1f; // give the source time to render its first frame
            _warmupFrames = 2;
            ResetStatistics();

            _playbackState = autoPlay ? StreamPlaybackState.Playing : StreamPlaybackState.Stopped;
            if (autoPlay && _videoSource != null)
            {
                _videoSource.Play();
            }

            StartAudioCapture();
            BroadcastState();
            RaisePlaybackStateChanged();
        }

        /// <summary>Stop streaming and release network/GPU resources.</summary>
        public void StopStreaming()
        {
            bool wasPlaying = _playbackState == StreamPlaybackState.Playing;
            _running = false;
            StopAudioCapture();
#if !UNITY_WEBGL || UNITY_EDITOR
            _pendingReadbacks = 0;
#endif
            _captureGeneration++;
            _readbackGenerations.Clear();

            if (_transport != null)
            {
                _transport.FrameReceived -= OnTransportFrameReceived;
                _transport.ControlReceived -= OnTransportControlReceived;
                _transport.Log -= OnTransportLog;
                _transport.Stop();
                _transport.Dispose();
                _transport = null;
            }

            if (_encoder != null)
            {
                _encoder.Dispose();
                _encoder = null;
            }

            if (_previousCameraTarget != null)
            {
                if (_camera != null)
                {
                    _camera.targetTexture = _previousCameraTarget;
                }

                _previousCameraTarget = null;
            }

            ReleaseCaptureTexture();

            if (_readbackTexture != null)
            {
                DestroyResource(_readbackTexture);
                _readbackTexture = null;
            }

            if (_capturePool != null)
            {
                _capturePool.Clear();
                _capturePool = null;
            }

            if (wasPlaying)
            {
                _playbackState = StreamPlaybackState.Stopped;
                RaisePlaybackStateChanged();
            }
        }

        // ------------------------------------------------------------------
        // Playback control (IStreamPlaybackController)
        // ------------------------------------------------------------------

        /// <summary>Start or resume streaming. Restarts the transport if it was stopped.</summary>
        public void Play()
        {
            if (!_running)
            {
                StartStreaming();
                if (!_running)
                {
                    return;
                }
            }

            SetPlaybackState(StreamPlaybackState.Playing, true);
        }

        /// <summary>Pause capture; the transport stays alive so Play commands can still arrive.</summary>
        public void Pause()
        {
            SetPlaybackState(StreamPlaybackState.Paused, true);
        }

        /// <summary>Stop playback. The transport stays alive so a receiver can wake the stream again.</summary>
        public void Stop()
        {
            SetPlaybackState(StreamPlaybackState.Stopped, true);
        }

        /// <summary>Restart from the beginning (for video sources) and play again.</summary>
        public void Restart()
        {
            if (_videoSource != null)
            {
                _videoSource.Restart();
                _lastKnownPositionSeconds = 0f;
                SetPlaybackState(StreamPlaybackState.Playing, false);
            }
            else
            {
                Play();
            }
        }

        /// <summary>Seek to a position in seconds on the video source.</summary>
        public void Seek(float seconds)
        {
            if (_videoSource == null || !_videoSource.CanSeek)
            {
                return;
            }

            float clamped = Mathf.Max(0f, seconds);
            _videoSource.Seek(clamped);
            _lastKnownPositionSeconds = clamped;
            BroadcastState();
        }

        /// <summary>Change JPEG quality at runtime.</summary>
        public void SetQuality(int quality)
        {
            jpegQuality = Mathf.Clamp(quality, 1, 100);
        }

        /// <summary>Change the target frame rate at runtime.</summary>
        public void SetTargetFps(int fps)
        {
            targetFps = Mathf.Clamp(fps, 1, 240);
        }

        /// <summary>Assign a video source (for example VideoPlayerStreamSource) before or during streaming.</summary>
        public void SetVideoSource(IStreamVideoSource source)
        {
            _videoSource = source;
            _lastKnownPositionSeconds = source != null ? source.PositionSeconds : 0f;
        }

        private void SetPlaybackState(StreamPlaybackState state, bool applyToVideoSource)
        {
            bool changed = _playbackState != state;
            _playbackState = state;

            if (applyToVideoSource && _videoSource != null)
            {
                switch (state)
                {
                    case StreamPlaybackState.Playing:
                        _videoSource.Play();
                        break;
                    case StreamPlaybackState.Paused:
                        _videoSource.Pause();
                        break;
                    case StreamPlaybackState.Stopped:
                        _videoSource.Stop();
                        _lastKnownPositionSeconds = 0f;
                        break;
                }
            }

            if (audioCapture != null)
            {
                audioCapture.Capturing = streamAudio && state == StreamPlaybackState.Playing;
                if (!audioCapture.Capturing)
                {
                    audioCapture.Clear();
                }
            }

            if (changed)
            {
                RaisePlaybackStateChanged();
            }

            // Always publish; this also acts as an ACK for receiver commands.
            BroadcastState();
        }

        private void RaisePlaybackStateChanged()
        {
            var handler = PlaybackStateChanged;
            if (handler != null)
            {
                handler(_playbackState);
            }
        }

        private float GetPositionSeconds()
        {
            if (_videoSource != null)
            {
                _lastKnownPositionSeconds = _videoSource.PositionSeconds;
            }

            return _lastKnownPositionSeconds;
        }

        // ------------------------------------------------------------------
        // Capture
        // ------------------------------------------------------------------

        /// <summary>
        /// Assign the video source used for capture. Passing null disables automatic detection
        /// so a previous video source is not picked up again when switching back to a camera.
        /// </summary>
        public void AssignVideoSource(MonoBehaviour source)
        {
            videoSourceBehaviour = source;
            autoDetectVideoSource = false;
            _videoSource = source as IStreamVideoSource;
        }

        private void ResolveVideoSource()
        {
            if (videoSourceBehaviour != null)
            {
                _videoSource = videoSourceBehaviour as IStreamVideoSource;
                if (_videoSource == null)
                {
                    Debug.LogWarning("[FXM.WebStream] videoSourceBehaviour does not implement IStreamVideoSource.");
                }

                return;
            }

            if (!autoDetectVideoSource)
            {
                _videoSource = null;
                return;
            }

            if (_videoSource != null)
            {
                return;
            }

            // Find one automatically on this GameObject or its children (for example VideoPlayerStreamSource).
            var behaviours = GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                var candidate = behaviours[i] as IStreamVideoSource;
                if (candidate != null)
                {
                    _videoSource = candidate;
                    return;
                }
            }
        }

        private Texture GetSourceTexture()
        {
            if (_videoSource != null && _videoSource.SourceTexture != null)
            {
                return _videoSource.SourceTexture;
            }

            return sourceTexture;
        }

        private bool PrepareCapture()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // WebGL does not support AsyncGPUReadback.
            captureMethod = CaptureMethod.ReadPixels;
#endif

            _camera = sourceCamera != null ? sourceCamera : Camera.main;

            Texture source = GetSourceTexture();
            int baseWidth;
            int baseHeight;

            if (source != null)
            {
                baseWidth = source.width;
                baseHeight = source.height;
            }
            else if (_camera != null && _camera.targetTexture != null)
            {
                baseWidth = _camera.targetTexture.width;
                baseHeight = _camera.targetTexture.height;
            }
            else
            {
                baseWidth = Mathf.Max(16, Screen.width);
                baseHeight = Mathf.Max(16, Screen.height);
            }

            if (_camera == null && source == null)
            {
                Debug.LogError("[FXM.WebStream] StreamingSender needs a Camera, Source Texture, or Video Source.");
                return false;
            }

            // Downscale on the GPU: the source renders directly at the small size, with no blit cost.
            _captureWidth = Mathf.Max(16, Mathf.RoundToInt(baseWidth * resolutionScale));
            _captureHeight = Mathf.Max(16, Mathf.RoundToInt(baseHeight * resolutionScale));

            // Keep dimensions even so 4:2:0 subsampling stays clean.
            _captureWidth &= ~1;
            _captureHeight &= ~1;

            bool created = false;

            if (_captureTexture == null ||
                _captureTexture.width != _captureWidth ||
                _captureTexture.height != _captureHeight)
            {
                ReleaseCaptureTexture();

                _captureTexture = new RenderTexture(
                    _captureWidth,
                    _captureHeight,
                    24,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.sRGB)
                {
                    name = "FXM.Capture",
                    useMipMap = false,
                    autoGenerateMips = false,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                _captureTexture.Create();
                created = true;
            }

            if (source == null && _camera != null)
            {
                // The camera renders directly into the sender's RenderTexture.
                if (created || _camera.targetTexture != _captureTexture)
                {
                    if (_previousCameraTarget == null)
                    {
                        _previousCameraTarget = _camera.targetTexture;
                    }

                    _camera.targetTexture = _captureTexture;
                    _ownsCaptureTexture = true;
                }
            }
            else
            {
                // Texture source (another camera / VideoPlayer): do not change the camera target.
                _ownsCaptureTexture = false;
            }

            // The intermediate Texture2D is only needed for ReadPixels; AsyncGPUReadback
            // reads the RenderTexture directly without a Texture2D.
            if (captureMethod == CaptureMethod.ReadPixels)
            {
                if (_readbackTexture == null || _readbackTexture.width != _captureWidth || _readbackTexture.height != _captureHeight)
                {
                    if (_readbackTexture != null)
                    {
                        DestroyResource(_readbackTexture);
                    }

                    _readbackTexture = new Texture2D(_captureWidth, _captureHeight, TextureFormat.RGBA32, false, false)
                    {
                        name = "FXM.Readback",
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp
                    };
                }
            }

            return true;
        }

        private void RequestCapture()
        {
            float interval = 1f / Mathf.Max(1, targetFps);
            _nextCaptureTime += interval;

            // If the process stalls, do not chase missed frames; a backlog increases latency.
            if (_nextCaptureTime < Time.unscaledTime)
            {
                _nextCaptureTime = Time.unscaledTime + interval;
            }

            if (_warmupFrames > 0)
            {
                _warmupFrames--;
                return;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            CaptureWebGLFallback();
#else
            if (captureMethod == CaptureMethod.AsyncGPUReadback)
            {
                RequestAsyncCapture();
            }
            else
            {
                CaptureWithReadPixels();
            }
#endif
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        private void RequestAsyncCapture()
        {
            // Limit in-flight readbacks so GPU requests do not pile up.
            if (_pendingReadbacks >= 2 || _captureTexture == null)
            {
                return;
            }

            BlitSourceIfNeeded();
            _pendingReadbacks++;
            _readbackGenerations.Enqueue(_captureGeneration);
            AsyncGPUReadback.Request(_captureTexture, 0, TextureFormat.RGBA32, OnAsyncReadbackCompleted);
        }

        private void OnAsyncReadbackCompleted(AsyncGPUReadbackRequest request)
        {
            _pendingReadbacks = Mathf.Max(0, _pendingReadbacks - 1);

            // Readback requests cannot be cancelled. If the sender was stopped or
            // restarted, drop results from the old generation so buffer sizes stay valid.
            int generation = _readbackGenerations.Count > 0 ? _readbackGenerations.Dequeue() : -1;

            if (!_running || !isActiveAndEnabled || request.hasError || generation != _captureGeneration)
            {
                if (request.hasError && !_loggedReadbackError)
                {
                    _loggedReadbackError = true;
                    Debug.LogWarning("[FXM.WebStream] AsyncGPUReadback failed. Try CaptureMethod.ReadPixels.");
                }

                return;
            }

            var pixels = request.GetData<byte>();

            // Requested format is RGBA32: 4 bytes per pixel.
            if (pixels.Length < _captureWidth * _captureHeight * 4)
            {
                return;
            }

            byte[] buffer = _capturePool.Rent(pixels.Length);
            pixels.CopyTo(buffer);

            if (_playbackState != StreamPlaybackState.Playing)
            {
                _capturePool.Return(buffer);
                return;
            }

            _capturedInWindow++;
            _encoder.Submit(buffer, _captureWidth, _captureHeight, jpegQuality, DateTime.UtcNow.Ticks);
        }

        private void CaptureWithReadPixels()
        {
            if (_captureTexture == null || _readbackTexture == null)
            {
                return;
            }

            BlitSourceIfNeeded();

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _captureTexture;

            try
            {
                _readbackTexture.ReadPixels(new Rect(0, 0, _captureWidth, _captureHeight), 0, 0, false);
                _readbackTexture.Apply(false, false);
            }
            finally
            {
                RenderTexture.active = previous;
            }

            var raw = _readbackTexture.GetRawTextureData<byte>();
            byte[] buffer = _capturePool.Rent(raw.Length);
            raw.CopyTo(buffer);

            _capturedInWindow++;
            _encoder.Submit(buffer, _captureWidth, _captureHeight, jpegQuality, DateTime.UtcNow.Ticks);
        }
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>
        /// WebGL has no worker threads or AsyncGPUReadback. Encoding uses
        /// ImageConversion.EncodeToJPG on the main thread (which can hitch),
        /// so lower resolution/FPS on this platform.
        /// </summary>
        private void CaptureWebGLFallback()
        {
            if (_captureTexture == null || _readbackTexture == null)
            {
                return;
            }

            BlitSourceIfNeeded();

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _captureTexture;

            try
            {
                _readbackTexture.ReadPixels(new Rect(0, 0, _captureWidth, _captureHeight), 0, 0, false);
                _readbackTexture.Apply(false, false);
            }
            finally
            {
                RenderTexture.active = previous;
            }

            byte[] jpeg = _readbackTexture.EncodeToJPG(jpegQuality);
            if (jpeg == null || jpeg.Length == 0)
            {
                return;
            }

            _capturedInWindow++;
            _encodedInWindow++;
            _lastJpegBytes = jpeg.Length;
            _transport.Send(new ArraySegment<byte>(jpeg));
            _sentInWindow++;
        }
#endif

        private void BlitSourceIfNeeded()
        {
            Texture source = GetSourceTexture();
            if (source != null && source != _captureTexture)
            {
                Graphics.Blit(source, _captureTexture);
            }
        }

        // ------------------------------------------------------------------
        // Encode and send
        // ------------------------------------------------------------------

        private void PumpEncodedFrames()
        {
            if (_encoder == null || _transport == null)
            {
                return;
            }

            EncodedFrame frame;
            while (_encoder.TryDequeue(out frame))
            {
                try
                {
                    if (frame.Length > 0 && _playbackState == StreamPlaybackState.Playing)
                    {
                        // Send() copies the payload, so the buffer can return to the pool immediately.
                        _transport.Send(new ArraySegment<byte>(frame.Buffer, 0, frame.Length));
                        _sentInWindow++;
                        _lastJpegBytes = frame.Length;
                        _lastEncodeMilliseconds = frame.EncodeMilliseconds;
                    }

                    _encodedInWindow++;
                }
                finally
                {
                    _encoder.ReleaseFrame(frame);
                }
            }
        }

        private void StartAudioCapture()
        {
            if (!streamAudio)
            {
                return;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            Debug.LogWarning("[FXM.WebStream] Audio capture is unavailable in WebGL. The stream continues without audio.");
            return;
#else
            if (audioCapture == null)
            {
                var listener = FindAnyObjectByType<AudioListener>();
                if (listener == null)
                {
                    Debug.LogWarning("[FXM.WebStream] No AudioListener in the scene, so audio cannot be captured. " +
                                     "Add one, or turn off Stream audio.");
                    return;
                }

                audioCapture = listener.GetComponent<StreamAudioCapture>();
                if (audioCapture == null)
                {
                    audioCapture = listener.gameObject.AddComponent<StreamAudioCapture>();
                }
            }

            audioCapture.Configure(audioSampleRate, audioStereo ? 2 : 1, audioGain);
            _audioPacket = new byte[StreamAudioProtocol.HeaderSize + audioCapture.PacketBytes];
            _audioSequence = 0;
            _audioSampleClock = 0;
            audioCapture.Capturing = _playbackState == StreamPlaybackState.Playing;
#endif
        }

        private void StopAudioCapture()
        {
            if (audioCapture == null)
            {
                return;
            }

            audioCapture.Capturing = false;
            audioCapture.Clear();
        }

        private void PumpAudio()
        {
            if (!streamAudio)
            {
                if (audioCapture != null && audioCapture.Capturing)
                {
                    audioCapture.Capturing = false;
                    audioCapture.Clear();
                }

                return;
            }

            if (_transport == null || audioCapture == null || _audioPacket == null)
            {
                return;
            }

            if (_playbackState != StreamPlaybackState.Playing)
            {
                audioCapture.Capturing = false;
                audioCapture.Clear();
                return;
            }

            int channels = audioCapture.targetChannels;
            int sampleRate = audioCapture.targetSampleRate;
            int sampleBytes = Mathf.Max(1, channels * 2);
            int pcmLength;

            while (audioCapture.TryDequeuePacket(_audioPacket, StreamAudioProtocol.HeaderSize, out pcmLength))
            {
                uint timestamp = (uint)(_audioSampleClock * 1000L / Mathf.Max(1, sampleRate));
                StreamAudioProtocol.WriteHeader(_audioPacket, 0, _audioSequence++, timestamp,
                    sampleRate, channels, pcmLength);

                _transport.Send(new ArraySegment<byte>(_audioPacket, 0,
                    StreamAudioProtocol.HeaderSize + pcmLength));
                _audioSampleClock += pcmLength / sampleBytes;
            }
        }

        private void PumpStateHeartbeat()
        {
            if (Time.unscaledTime - _lastStateBroadcastTime < 1f)
            {
                return;
            }

            BroadcastState();
        }

        private void BroadcastState()
        {
            if (_transport == null)
            {
                return;
            }

            var message = StreamControlMessage.StateMessage(
                _sourceId,
                _playbackState,
                Mathf.RoundToInt(GetPositionSeconds() * 1000f),
                Mathf.RoundToInt(DurationSeconds * 1000f));

            message.Sequence = StreamControlProtocol.NextSequence(ref _controlSequence);

            var buffer = new byte[StreamControlProtocol.HeaderSize];
            StreamControlProtocol.Write(buffer, 0, message);
            _transport.SendControl(new ArraySegment<byte>(buffer));
            _lastStateBroadcastTime = Time.unscaledTime;
        }

        private bool CreateAndStartTransport()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (transportKind == TransportKind.Udp)
            {
                Debug.LogWarning("[FXM.WebStream] UDP is unavailable on WebGL. Switching to WebSocket.");
                transportKind = TransportKind.WebSocket;
            }

            if (webSocketServer)
            {
                Debug.LogWarning("[FXM.WebStream] A browser cannot run a WebSocket server. " +
                                 "The WebGL sender switches to client mode; run the server on a desktop.");
                webSocketServer = false;
            }
#endif

            _transport = TransportFactory.Create(transportKind);
            _transport.Log += OnTransportLog;
            _transport.FrameReceived += OnTransportFrameReceived;
            _transport.ControlReceived += OnTransportControlReceived;

            TransportConfig config;
            if (transportKind == TransportKind.Udp)
            {
                // Fixed local port so receivers can send playback commands back.
                int localPort = allowRemoteControl ? Mathf.Max(0, localControlPort) : 0;
                var role = allowRemoteControl ? TransportRole.Bidirectional : TransportRole.SendOnly;
                config = TransportConfig.CreateUdp(localPort, remoteHost, remotePort, role, mtu);
            }
            else if (transportKind == TransportKind.Relay)
            {
                config = TransportConfig.CreateRelay(relayUrl, relayRoom, relayToken, RelayPeerRole.Sender);
            }
            else if (webSocketServer)
            {
                TransportConfig parsed = TransportConfig.ParseWebSocketUrl(webSocketUrl);
                int port = parsed.RemotePort > 0 ? parsed.RemotePort : remotePort;
                config = TransportConfig.CreateWebSocketServer(port, parsed.WebSocketPath);
            }
            else
            {
                config = TransportConfig.WebSocketClient(webSocketUrl);
            }

            if (!_transport.Start(config))
            {
                Debug.LogError("[FXM.WebStream] Transport failed to start.");
                _transport.Log -= OnTransportLog;
                _transport.FrameReceived -= OnTransportFrameReceived;
                _transport.ControlReceived -= OnTransportControlReceived;
                _transport.Dispose();
                _transport = null;
                return false;
            }

            ApplyUdpReceivers();
            return true;
        }

        private void ApplyUdpReceivers()
        {
            var udp = _transport as UDPTransport;
            if (udp == null || udpReceivers == null || udpReceivers.Length == 0)
            {
                return;
            }

            if (!udp.SetTargets(udpReceivers))
            {
                Debug.LogWarning("[FXM.WebStream] No valid udpReceivers address; using remoteHost:remotePort.");
            }
        }

        /// <summary>Add a UDP target at runtime (one-to-many).</summary>
        public bool AddUdpReceiver(string hostAndPort)
        {
            var udp = _transport as UDPTransport;
            if (udp == null)
            {
                Debug.LogWarning("[FXM.WebStream] AddUdpReceiver is only available for the UDP transport.");
                return false;
            }

            return udp.AddTarget(hostAndPort);
        }

        /// <summary>Remove a UDP target at runtime.</summary>
        public bool RemoveUdpReceiver(string hostAndPort)
        {
            var udp = _transport as UDPTransport;
            return udp != null && udp.RemoveTarget(hostAndPort);
        }

        /// <summary>Active UDP targets in "host:port" format.</summary>
        public string[] GetUdpReceivers()
        {
            var udp = _transport as UDPTransport;
            return udp != null ? udp.GetTargets() : new string[0];
        }

        private void OnTransportLog(string message)
        {
            Debug.Log("[FXM.WebStream][Sender] " + message);
        }

        private void OnTransportFrameReceived(ArraySegment<byte> frame)
        {
            // The sender does not use inbound video data; ignore it.
        }

        /// <summary>
        /// Playback commands from receivers. Accepted only when allowRemoteControl is on
        /// and the target id matches (0 = every sender).
        /// </summary>
        private void OnTransportControlReceived(ArraySegment<byte> payload)
        {
            StreamControlMessage message;
            if (!StreamControlProtocol.TryRead(payload.Array, payload.Offset, payload.Count, out message))
            {
                return;
            }

            if (message.Kind != StreamMessageKind.Command)
            {
                return;
            }

            if (message.SourceId != 0 && message.SourceId != _sourceId)
            {
                return;
            }

            if (!allowRemoteControl)
            {
                if (!_loggedRemoteBlocked)
                {
                    _loggedRemoteBlocked = true;
                    Debug.LogWarning("[FXM.WebStream] Playback command ignored because allowRemoteControl = false.");
                }

                return;
            }

            switch (message.Command)
            {
                case StreamCommandType.Play:
                    Play();
                    break;
                case StreamCommandType.Pause:
                    Pause();
                    break;
                case StreamCommandType.Stop:
                    Stop();
                    break;
                case StreamCommandType.Restart:
                    Restart();
                    break;
                case StreamCommandType.Seek:
                    Seek(message.Value / 1000f);
                    break;
                case StreamCommandType.SetQuality:
                    SetQuality(message.Value);
                    BroadcastState();
                    break;
                case StreamCommandType.SetTargetFps:
                    SetTargetFps(message.Value);
                    BroadcastState();
                    break;
            }
        }

        // ------------------------------------------------------------------
        // Resources and statistics
        // ------------------------------------------------------------------

        private void ReleaseCaptureTexture()
        {
            if (_captureTexture != null && _ownsCaptureTexture)
            {
                if (_camera != null && _camera.targetTexture == _captureTexture)
                {
                    _camera.targetTexture = _previousCameraTarget;
                }
            }

            if (_captureTexture != null)
            {
                _captureTexture.Release();
                DestroyResource(_captureTexture);
                _captureTexture = null;
            }

            _ownsCaptureTexture = false;
        }

        private static void DestroyResource(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }

        private void ReleaseResources()
        {
            StopStreaming();
        }

        private void ResetStatistics()
        {
            _capturedInWindow = 0;
            _encodedInWindow = 0;
            _sentInWindow = 0;
            _statAccumulator = 0f;
            _captureFps = 0f;
            _encodeFps = 0f;
            _sendFps = 0f;
        }

        private void UpdateStatistics()
        {
            _statAccumulator += Time.unscaledDeltaTime;
            if (_statAccumulator < 1f)
            {
                return;
            }

            _captureFps = _capturedInWindow / _statAccumulator;
            _encodeFps = _encodedInWindow / _statAccumulator;
            _sendFps = _sentInWindow / _statAccumulator;

            _capturedInWindow = 0;
            _encodedInWindow = 0;
            _sentInWindow = 0;
            _statAccumulator = 0f;
        }
    }
}
