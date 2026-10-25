using System;
using UnityEngine;
using UnityEngine.UI;

namespace FXM.WebStream
{
    /// <summary>
    /// Receiver component for a SINGLE stream source.
    ///
    /// Per-frame flow:
    /// 1. <see cref="StreamReceiverCore.Poll"/> moves complete frames from the network thread
    ///    to the main thread and processes control messages.
    /// 2. JPEG is decoded with <c>ImageConversion.LoadImage</c> into ONE reused Texture2D
    ///    (see <see cref="StreamReceiverCore"/>).
    /// 3. The texture is assigned to a <see cref="RawImage"/> (optional, with letterboxing).
    ///
    /// Playback control:
    /// - <see cref="Play"/>, <see cref="Pause"/>, <see cref="Stop"/>, <see cref="Restart"/>,
    ///   and <see cref="Seek"/> send commands to the sender through the control channel.
    /// - The sender always publishes the authoritative state (one-second heartbeat), so the
    ///   UI stays in sync even if a command packet is lost.
    ///
    /// For many-to-one / many-to-many use <see cref="MultiStreamReceiver"/>, which runs one
    /// core per source.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FXM WebStream/Streaming Receiver")]
    public sealed class StreamingReceiver : MonoBehaviour, IStreamPlaybackController
    {
        [Header("Network")]
        public TransportKind transportKind = TransportKind.Udp;

        [Tooltip("UDP port to listen on. Bidirectional role also allows sending playback commands back.")]
        public int listenPort = 47777;

        [Tooltip("Sender address, used to send playback commands over UDP.")]
        public string senderHost = "127.0.0.1";

        [Tooltip("Sender control UDP port (StreamingSender.localControlPort).")]
        public int senderControlPort = 47800;

        [Tooltip("WebSocket URL, used when transportKind = WebSocket and webSocketServer is disabled.")]
        public string webSocketUrl = "ws://127.0.0.1:47778/fxm-stream/";

        [Tooltip("When enabled, the receiver runs as a WebSocket server instead of a client.")]
        public bool webSocketServer;

        [Header("Internet Relay")]
        [Tooltip("Public relay WebSocket URL. Use wss:// in production. Used when transportKind = Relay.")]
        public string relayUrl = "ws://127.0.0.1:8787/fxm-relay";

        [Tooltip("Room name shared with the sender. One sender per room.")]
        public string relayRoom = "demo";

        [Tooltip("Optional shared secret. Must match the relay server's FXM_RELAY_TOKEN value.")]
        public string relayToken = "";

        [Tooltip("Maximum UDP datagram size; must be equal to or larger than the sender's value.")]
        [Range(576, 1400)]
        public int mtu = 1200;

        [Tooltip("Start automatically when the component becomes active.")]
        public bool autoStart = true;

        [Tooltip("Allow this component to send play/pause/stop commands to the sender.")]
        public bool allowRemoteCommands = true;

        [Tooltip("Only accept state messages from this sender id (0 = accept all).")]
        public uint sourceIdFilter;

        [Header("Rendering")]
        [Tooltip("Target RawImage. Empty = the RawImage component on this GameObject.")]
        public RawImage targetRawImage;

        [Tooltip("Letterbox the image so it keeps its aspect ratio inside the RectTransform.")]
        public bool preserveAspectRatio = true;

        [Tooltip("Release the CPU-side pixel copy after decoding (saves memory; the texture can no longer be read back).")]
        public bool markTextureNonReadable;

        [Tooltip("Clear the view when the sender reports Stopped.")]
        public bool clearOnStop = true;

        [Header("Performance")]
        [Tooltip("Decode rate limit per second. Frames arriving faster are skipped.")]
        [Range(1, 120)]
        public int maxDecodeFps = 60;

        [Header("Audio")]
        [Tooltip("Play audio sent by the sender. The sender must have Stream audio enabled.")]
        public bool playAudio = true;

        [Range(0f, 1f)]
        public float audioVolume = 1f;

        [Header("Diagnostics")]
        public bool showStatistics;

        /// <summary>Raised after a frame is decoded (main thread).</summary>
        public event Action<Texture2D> FrameDecoded;

        /// <summary>Raised when playback state changes (from the sender or a local action).</summary>
        public event Action<StreamPlaybackState> PlaybackStateChanged;

        private INetworkTransport _transport;
        private StreamReceiverCore _core;
        private StreamAudioPlayer _audioPlayer;
        private bool _running;
        private bool _textureAssigned;
        private bool _audioMuted;
        private uint _lastStateSequence;
        private bool _hasStateSequence;

        private AspectRatioFitter _aspectFitter;
        private int _lastAspectTextureWidth;
        private int _lastAspectTextureHeight;

        private StreamPlaybackState _playbackState = StreamPlaybackState.Stopped;
        private uint _sourceId;
        private float _positionSeconds;
        private float _durationSeconds;

        public bool IsReceiving { get { return _running; } }

        /// <summary>Decoded texture (reused every frame).</summary>
        public Texture2D StreamTexture { get { return _core != null ? _core.Texture : null; } }

        public INetworkTransport Transport { get { return _transport; } }
        public int DecodedFrames { get { return _core != null ? _core.DecodedFrames : 0; } }
        public int SkippedFrames { get { return _core != null ? _core.SkippedFrames : 0; } }
        public int DecodeErrors { get { return _core != null ? _core.DecodeErrors : 0; } }

        /// <summary>Last known playback state (updated by the sender or a local action).</summary>
        public StreamPlaybackState PlaybackState { get { return _playbackState; } }

        /// <summary>True while received audio is muted locally (the sender is unaffected).</summary>
        public bool AudioMuted { get { return _audioMuted; } }

        /// <summary>True when an audio player exists for this receiver.</summary>
        public bool AudioAvailable { get { return _audioPlayer != null; } }

        /// <summary>Mute or unmute received audio without stopping the stream.</summary>
        public void SetAudioMuted(bool muted)
        {
            _audioMuted = muted;
            if (_audioPlayer != null)
            {
                _audioPlayer.SetMuted(muted);
            }
        }

        /// <summary>Id of the sender that last published state (0 before the first message).</summary>
        public uint SourceId { get { return _sourceId; } }

        public float PositionSeconds { get { return _positionSeconds; } }
        public float DurationSeconds { get { return _durationSeconds; } }

        /// <summary>Seeking only makes sense when the sender reports a duration (video source).</summary>
        public bool CanSeek { get { return _durationSeconds > 0.01f; } }

        private void Start()
        {
            if (autoStart)
            {
                StartReceiving();
            }
        }

        private void Update()
        {
            if (!_running || _core == null)
            {
                return;
            }

            _core.MaxDecodeFps = maxDecodeFps;
            _core.Poll();

            if (preserveAspectRatio)
            {
                UpdateAspectRatio();
            }
        }

        private void OnDisable()
        {
            StopReceiving();
        }

        private void OnDestroy()
        {
            StopReceiving();
        }

        private void OnGUI()
        {
            if (!showStatistics)
            {
                return;
            }

            string text = _running && _core != null
                ? string.Format(
                    "FXM Receiver ({0}) {1} | source {2}\n{3}x{4} | {5:F1} fps | decode {6:F1} ms | {7:F1} KB\nframes {8} | skipped {9} | errors {10} | peers {11}",
                    transportKind, _playbackState, _sourceId,
                    _core.TextureWidth, _core.TextureHeight, _core.ReceiveFps,
                    _core.LastDecodeMilliseconds, _core.LastBytes / 1024f,
                    _core.DecodedFrames, _core.SkippedFrames, _core.DecodeErrors,
                    _transport != null ? _transport.PeerCount : 0)
                : "FXM Receiver (stopped)";

            GUI.Box(new Rect(10, 10, 520, 84), GUIContent.none);
            GUI.Label(new Rect(18, 14, 510, 80), text);
        }

        /// <summary>Start the transport and prepare the texture. Safe to call repeatedly.</summary>
        public void StartReceiving()
        {
            if (_running)
            {
                return;
            }

            if (targetRawImage == null)
            {
                targetRawImage = GetComponent<RawImage>();
            }

            if (targetRawImage == null)
            {
                UnityEngine.Debug.LogWarning("[FXM.WebStream] StreamingReceiver found no RawImage; frames are decoded but not displayed.");
            }

            if (!CreateAndStartTransport())
            {
                return;
            }

            _core = new StreamReceiverCore(_transport, maxDecodeFps, markTextureNonReadable);
            _core.FrameDecoded += OnFrameDecoded;
            _core.ControlReceived += OnControlReceived;
            _core.Attach();
            SetupAudio();

            _running = true;
            _lastStateSequence = 0;
            _hasStateSequence = false;
            _sourceId = 0;
            _positionSeconds = 0f;
            _durationSeconds = 0f;
            SetPlaybackState(StreamPlaybackState.Playing, false);
        }

        /// <summary>Stop the transport and release the texture.</summary>
        public void StopReceiving()
        {
            _running = false;
            TeardownAudio();

            if (_core != null)
            {
                _core.FrameDecoded -= OnFrameDecoded;
                _core.ControlReceived -= OnControlReceived;
                _core.Dispose();
                _core = null;
            }

            if (_transport != null)
            {
                _transport.Log -= OnTransportLog;
                _transport.Stop();
                _transport.Dispose();
                _transport = null;
            }

            if (targetRawImage != null && targetRawImage.texture != null)
            {
                targetRawImage.texture = null;
            }

            _textureAssigned = false;
            _lastAspectTextureWidth = 0;
            _lastAspectTextureHeight = 0;
        }

        // ------------------------------------------------------------------
        // Playback control (IStreamPlaybackController)
        // ------------------------------------------------------------------

        /// <summary>Ask the sender to resume the stream.</summary>
        public void Play()
        {
            SendCommand(StreamCommandType.Play);
            SetPlaybackState(StreamPlaybackState.Playing, true);
        }

        /// <summary>Ask the sender to pause; the last frame stays visible.</summary>
        public void Pause()
        {
            SendCommand(StreamCommandType.Pause);
            SetPlaybackState(StreamPlaybackState.Paused, true);
        }

        /// <summary>Ask the sender to stop and clear the view (when clearOnStop is on).</summary>
        public void Stop()
        {
            SendCommand(StreamCommandType.Stop);
            SetPlaybackState(StreamPlaybackState.Stopped, true);
        }

        /// <summary>Restart from the beginning and play (for VideoPlayer sources).</summary>
        public void Restart()
        {
            SendCommand(StreamCommandType.Restart);
            SetPlaybackState(StreamPlaybackState.Playing, true);
        }

        /// <summary>Seek to a position in seconds (only meaningful for video sources).</summary>
        public void Seek(float seconds)
        {
            int milliseconds = Mathf.Max(0, Mathf.RoundToInt(seconds * 1000f));
            SendCommand(StreamCommandType.Seek, milliseconds);
        }

        /// <summary>Change JPEG quality on the sender (1..100).</summary>
        public void SetQuality(int quality)
        {
            SendCommand(StreamCommandType.SetQuality, Mathf.Clamp(quality, 1, 100));
        }

        /// <summary>Change the target FPS on the sender (1..240).</summary>
        public void SetTargetFps(int fps)
        {
            SendCommand(StreamCommandType.SetTargetFps, Mathf.Clamp(fps, 1, 240));
        }

        private void SendCommand(StreamCommandType command, int value = 0)
        {
            if (!_running || _transport == null || !allowRemoteCommands)
            {
                return;
            }

            var message = StreamControlMessage.CommandMessage(command, _sourceId, value);
            var buffer = new byte[StreamControlProtocol.HeaderSize];
            StreamControlProtocol.Write(buffer, 0, message);
            _transport.SendControl(new ArraySegment<byte>(buffer));
        }

        private void SetPlaybackState(StreamPlaybackState state, bool applyLocally)
        {
            if (_audioPlayer != null)
            {
                _audioPlayer.SetPlaying(state == StreamPlaybackState.Playing);
            }

            if (_playbackState == state && applyLocally)
            {
                // Keep updating frame acceptance even when the state is unchanged.
                if (_core != null)
                {
                    _core.AcceptFrames = state == StreamPlaybackState.Playing;
                }

                return;
            }

            _playbackState = state;

            if (_core != null)
            {
                _core.AcceptFrames = state == StreamPlaybackState.Playing;
            }

            if (state == StreamPlaybackState.Stopped && clearOnStop && targetRawImage != null)
            {
                targetRawImage.texture = null;
                _textureAssigned = false;
            }

            var handler = PlaybackStateChanged;
            if (handler != null)
            {
                handler(state);
            }
        }

        // ------------------------------------------------------------------
        // Transport
        // ------------------------------------------------------------------

        private bool CreateAndStartTransport()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (transportKind == TransportKind.Udp)
            {
                UnityEngine.Debug.LogWarning("[FXM.WebStream] UDP is unavailable on WebGL. Switching to WebSocket.");
                transportKind = TransportKind.WebSocket;
            }

            if (webSocketServer)
            {
                UnityEngine.Debug.LogWarning("[FXM.WebStream] A browser cannot run a WebSocket server. " +
                                              "The WebGL receiver switches to client mode.");
                webSocketServer = false;
            }
#endif

            _transport = TransportFactory.Create(transportKind);
            _transport.Log += OnTransportLog;

            TransportConfig config;
            if (transportKind == TransportKind.Udp)
            {
                // Bind the frame port; target the sender control port for playback commands.
                config = TransportConfig.CreateUdp(listenPort, senderHost, senderControlPort,
                    TransportRole.Bidirectional, mtu);
            }
            else if (transportKind == TransportKind.Relay)
            {
                config = TransportConfig.CreateRelay(relayUrl, relayRoom, relayToken, RelayPeerRole.Receiver);
            }
            else if (webSocketServer)
            {
                TransportConfig parsed = TransportConfig.ParseWebSocketUrl(webSocketUrl);
                int port = parsed.RemotePort > 0 ? parsed.RemotePort : listenPort;
                config = TransportConfig.CreateWebSocketServer(port, parsed.WebSocketPath);
            }
            else
            {
                config = TransportConfig.WebSocketClient(webSocketUrl);
            }

            if (!_transport.Start(config))
            {
                UnityEngine.Debug.LogError("[FXM.WebStream] Receiver transport failed to start (" + transportKind + ").");
                _transport.Log -= OnTransportLog;
                _transport.Dispose();
                _transport = null;
                return false;
            }

            return true;
        }

        private void OnTransportLog(string message)
        {
            UnityEngine.Debug.Log("[FXM.WebStream][Receiver] " + message);
        }

        // ------------------------------------------------------------------
        // Frames and control
        // ------------------------------------------------------------------

        private void SetupAudio()
        {
            if (!playAudio)
            {
                return;
            }

            if (_audioPlayer == null)
            {
                _audioPlayer = GetComponent<StreamAudioPlayer>();
                if (_audioPlayer == null)
                {
                    _audioPlayer = gameObject.AddComponent<StreamAudioPlayer>();
                }
            }

            _audioPlayer.SetVolume(audioVolume);
            _core.AudioReceived += OnAudioReceived;
            _audioPlayer.SetPlaying(true);
            _audioPlayer.SetMuted(_audioMuted);
        }

        private void TeardownAudio()
        {
            if (_core != null)
            {
                _core.AudioReceived -= OnAudioReceived;
            }

            if (_audioPlayer != null)
            {
                _audioPlayer.Clear();
            }
        }

        private void OnAudioReceived(StreamAudioFrame frame)
        {
            if (_audioPlayer != null)
            {
                _audioPlayer.Push(frame);
            }
        }

        private void OnFrameDecoded(Texture2D texture)
        {
            if (targetRawImage != null)
            {
                if (!_textureAssigned || targetRawImage.texture != texture)
                {
                    targetRawImage.texture = texture;
                    _textureAssigned = true;
                    _lastAspectTextureWidth = 0; // force a letterbox recalculation
                }

                if (preserveAspectRatio)
                {
                    UpdateAspectRatio();
                }
            }

            var handler = FrameDecoded;
            if (handler != null)
            {
                handler(texture);
            }
        }

        private void OnControlReceived(StreamControlMessage message)
        {
            if (sourceIdFilter != 0 && message.SourceId != 0 && message.SourceId != sourceIdFilter)
            {
                return;
            }

            if (message.Kind != StreamMessageKind.State)
            {
                return;
            }

            if (message.SourceId != 0 && message.SourceId != _sourceId)
            {
                // New or restarted sender: the state sequence starts over.
                _sourceId = message.SourceId;
                _hasStateSequence = false;
            }

            // Drop late state messages (UDP can reorder packets).
            if (_hasStateSequence && !IsNewer(message.Sequence, _lastStateSequence))
            {
                return;
            }

            _hasStateSequence = true;
            _lastStateSequence = message.Sequence;
            _positionSeconds = message.PositionMs / 1000f;
            _durationSeconds = message.DurationMs / 1000f;

            if (_playbackState != message.State)
            {
                SetPlaybackState(message.State, false);
            }
        }

        private static bool IsNewer(uint candidate, uint reference)
        {
            return unchecked((int)(candidate - reference)) > 0;
        }

        private void UpdateAspectRatio()
        {
            Texture2D texture = StreamTexture;
            if (targetRawImage == null || texture == null || texture.height == 0)
            {
                return;
            }

            if (_lastAspectTextureWidth == texture.width &&
                _lastAspectTextureHeight == texture.height &&
                _aspectFitter != null)
            {
                return;
            }

            _lastAspectTextureWidth = texture.width;
            _lastAspectTextureHeight = texture.height;

            // FitInParent letterboxes the RawImage inside its Canvas instead of
            // stretching it. The parent background shows through the bars.
            if (_aspectFitter == null)
            {
                _aspectFitter = targetRawImage.GetComponent<AspectRatioFitter>();
                if (_aspectFitter == null)
                {
                    _aspectFitter = targetRawImage.gameObject.AddComponent<AspectRatioFitter>();
                }
            }

            _aspectFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            _aspectFitter.aspectRatio = texture.width / (float)texture.height;
            targetRawImage.uvRect = new Rect(0f, 0f, 1f, 1f);
        }
    }
}
