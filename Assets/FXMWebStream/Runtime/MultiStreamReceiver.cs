using System;
using UnityEngine;
using UnityEngine.UI;

namespace FXM.WebStream
{
    /// <summary>
    /// Configuration for one source of a <see cref="MultiStreamReceiver"/>.
    /// Each source has its own transport and port, so the receiver can show several
    /// senders at once (many-to-many) or select one of them (many-to-one).
    /// </summary>
    [Serializable]
    public sealed class StreamSourceConfig
    {
        [Tooltip("Display name, for example \"Camera A\".")]
        public string name = "Source";

        public TransportKind transportKind = TransportKind.Udp;

        [Tooltip("Sender address, used to send playback commands.")]
        public string host = "127.0.0.1";

        [Tooltip("Local UDP port to listen on. Must be unique for each source on this machine.")]
        public int localPort = 47777;

        [Tooltip("Sender control UDP port (StreamingSender.localControlPort).")]
        public int senderControlPort = 47800;

        [Tooltip("WebSocket URL used when transportKind = WebSocket.")]
        public string webSocketUrl = "ws://127.0.0.1:47778/fxm-stream/";

        [Tooltip("Run as a WebSocket server (rarely used on the receiving side).")]
        public bool webSocketServer;

        [Header("Internet Relay")]
        [Tooltip("Public relay WebSocket URL. Use wss:// in production.")]
        public string relayUrl = "ws://127.0.0.1:8787/fxm-relay";

        [Tooltip("Relay room name for this source. One sender per room.")]
        public string relayRoom = "demo";

        [Tooltip("Optional shared secret matching the relay server's FXM_RELAY_TOKEN value.")]
        public string relayToken = "";

        [Range(576, 1400)]
        public int mtu = 1200;
    }

    /// <summary>
    /// Receives several streams at once.
    ///
    /// Supported topologies:
    /// - Many-to-one: register N senders and show one active source
    ///   (<see cref="showAllSources"/> = false, switch with <see cref="SetActiveSource"/>).
    /// - Many-to-many: every node runs a sender plus this multi receiver; all sources
    ///   are shown in a grid (<see cref="showAllSources"/> = true).
    ///
    /// UDP note: each source needs its own local port because a socket binds one port.
    /// Point each sender at the matching source port. WebSocket and relay sources do not
    /// have this limitation (one client connection per URL).
    ///
    /// Decoding stays on the main thread (Unity limitation), but each source reuses one
    /// Texture2D and pooled buffers.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FXM WebStream/Multi Stream Receiver")]
    public sealed class MultiStreamReceiver : MonoBehaviour, IStreamPlaybackController
    {
        [Header("Sources")]
        public StreamSourceConfig[] sources = new StreamSourceConfig[0];

        [Tooltip("One RawImage per source (same order as sources). May be empty to decode without display.")]
        public RawImage[] views;

        [Tooltip("True: show every source in a grid. False: show only the active source (many-to-one).")]
        public bool showAllSources = true;

        [Tooltip("Source index shown when showAllSources = false.")]
        public int activeSourceIndex;

        [Header("Performance")]
        [Range(1, 120)]
        public int maxDecodeFps = 60;

        [Tooltip("Decode rate for sources that are not currently visible (saves CPU).")]
        [Range(1, 60)]
        public int backgroundDecodeFps = 5;

        [Header("Audio")]
        [Tooltip("Play audio from the received sources.")]
        public bool playAudio = true;

        [Tooltip("Only play the active (visible) source. Disable to mix every source.")]
        public bool audioFollowsActiveSource = true;

        [Range(0f, 1f)]
        public float audioVolume = 1f;

        [Header("Rendering")]
        public bool preserveAspectRatio = true;

        [Tooltip("Clear a source view when that source reports Stopped.")]
        public bool clearOnStop = true;

        [Tooltip("Start automatically when the component becomes active.")]
        public bool autoStart = true;

        [Header("Diagnostics")]
        public bool showStatistics = true;

        /// <summary>(source index, new state)</summary>
        public event Action<int, StreamPlaybackState> SourceStateChanged;

        /// <summary>(source index, latest texture)</summary>
        public event Action<int, Texture2D> SourceFrameDecoded;

        private sealed class SourceRuntime
        {
            public StreamSourceConfig Config;
            public INetworkTransport Transport;
            public StreamReceiverCore Core;
            public StreamPlaybackState State = StreamPlaybackState.Stopped;
            public uint SourceId;
            public uint LastStateSequence;
            public bool HasStateSequence;
            public float PositionSeconds;
            public float DurationSeconds;
            public StreamAudioPlayer Audio;
            public GameObject AudioObject;
        }

        private SourceRuntime[] _runtimes = new SourceRuntime[0];
        private bool _running;

        public bool IsReceiving { get { return _running; } }
        public int SourceCount { get { return _runtimes.Length; } }

        /// <summary>Number of sources whose transport currently has a live peer.</summary>
        public int ConnectedSourceCount
        {
            get
            {
                int connected = 0;
                for (int i = 0; i < _runtimes.Length; i++)
                {
                    var runtime = _runtimes[i];
                    if (runtime != null && runtime.Transport != null && runtime.Transport.PeerCount > 0)
                    {
                        connected++;
                    }
                }

                return connected;
            }
        }

        public int ActiveSourceIndex
        {
            get { return _runtimes.Length == 0 ? 0 : Mathf.Clamp(activeSourceIndex, 0, _runtimes.Length - 1); }
        }

        public StreamPlaybackState PlaybackState
        {
            get
            {
                var runtime = GetActiveRuntime();
                return runtime != null ? runtime.State : StreamPlaybackState.Stopped;
            }
        }

        /// <summary>True when at least one source has an audio player.</summary>
        public bool AudioAvailable
        {
            get
            {
                for (int i = 0; i < _runtimes.Length; i++)
                {
                    if (_runtimes[i] != null && _runtimes[i].Audio != null)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public bool CanSeek
        {
            get
            {
                var runtime = GetActiveRuntime();
                return runtime != null && runtime.DurationSeconds > 0.01f;
            }
        }

        public float PositionSeconds
        {
            get
            {
                var runtime = GetActiveRuntime();
                return runtime != null ? runtime.PositionSeconds : 0f;
            }
        }

        public float DurationSeconds
        {
            get
            {
                var runtime = GetActiveRuntime();
                return runtime != null ? runtime.DurationSeconds : 0f;
            }
        }

        private void Start()
        {
            if (autoStart)
            {
                StartReceiving();
            }
        }

        private void Update()
        {
            if (!_running)
            {
                return;
            }

            int active = ActiveSourceIndex;

            for (int i = 0; i < _runtimes.Length; i++)
            {
                var runtime = _runtimes[i];
                if (runtime == null || runtime.Core == null)
                {
                    continue;
                }

                bool visible = showAllSources || i == active;
                runtime.Core.MaxDecodeFps = visible ? maxDecodeFps : Mathf.Min(backgroundDecodeFps, maxDecodeFps);
                runtime.Core.Poll();

                if (runtime.Audio != null)
                {
                    bool audible = playAudio &&
                                   (!audioFollowsActiveSource || i == active) &&
                                   runtime.State == StreamPlaybackState.Playing;
                    runtime.Audio.SetMuted(!audible);
                }
            }

            if (preserveAspectRatio)
            {
                UpdateAspects();
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
            if (!showStatistics || !_running)
            {
                return;
            }

            var builder = new System.Text.StringBuilder(256);
            builder.Append("FXM Multi Receiver (").Append(_runtimes.Length).Append(" sources)\n");

            for (int i = 0; i < _runtimes.Length; i++)
            {
                var runtime = _runtimes[i];
                if (runtime == null || runtime.Core == null)
                {
                    builder.Append(i).Append(": failed to start\n");
                    continue;
                }

                builder.Append(i)
                    .Append(": ").Append(runtime.Config.name)
                    .Append(" | ").Append(runtime.State)
                    .Append(" | ").Append(runtime.Core.TextureWidth).Append('x').Append(runtime.Core.TextureHeight)
                    .Append(" | ").Append(runtime.Core.ReceiveFps.ToString("F1")).Append(" fps\n");
            }

            GUI.Box(new Rect(10, 10, 560, 40 + _runtimes.Length * 20), GUIContent.none);
            GUI.Label(new Rect(18, 14, 550, 30 + _runtimes.Length * 20), builder.ToString());
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        public void StartReceiving()
        {
            if (_running)
            {
                return;
            }

            if (sources == null)
            {
                sources = new StreamSourceConfig[0];
            }

            _runtimes = new SourceRuntime[sources.Length];

            for (int i = 0; i < sources.Length; i++)
            {
                var config = sources[i] ?? new StreamSourceConfig();
                var runtime = new SourceRuntime { Config = config };
                _runtimes[i] = runtime;

                if (!StartSource(i, runtime))
                {
                    UnityEngine.Debug.LogError("[FXM.WebStream] Source " + i + " (" + config.name + ") failed to start.");
                }
            }

            _running = true;
            ApplyViewVisibility();

            if (audioFollowsActiveSource)
            {
                SetActiveSource(ActiveSourceIndex);
            }
        }

        public void StopReceiving()
        {
            if (_runtimes != null)
            {
                for (int i = 0; i < _runtimes.Length; i++)
                {
                    StopSource(_runtimes[i]);
                }
            }

            _runtimes = new SourceRuntime[0];
            _running = false;

            if (views != null)
            {
                for (int i = 0; i < views.Length; i++)
                {
                    if (views[i] != null)
                    {
                        views[i].texture = null;
                    }
                }
            }
        }

        private bool StartSource(int index, SourceRuntime runtime)
        {
            var config = runtime.Config;

#if UNITY_WEBGL && !UNITY_EDITOR
            if (config.transportKind == TransportKind.Udp)
            {
                UnityEngine.Debug.LogWarning("[FXM.WebStream] UDP is unavailable on WebGL; source " + index + " uses WebSocket.");
                config.transportKind = TransportKind.WebSocket;
            }

            config.webSocketServer = false;
#endif

            runtime.Transport = TransportFactory.Create(config.transportKind);
            runtime.Transport.Log += OnTransportLog;

            TransportConfig transportConfig;
            if (config.transportKind == TransportKind.Udp)
            {
                transportConfig = TransportConfig.CreateUdp(
                    config.localPort, config.host, config.senderControlPort,
                    TransportRole.Bidirectional, config.mtu);
            }
            else if (config.transportKind == TransportKind.Relay)
            {
                transportConfig = TransportConfig.CreateRelay(
                    config.relayUrl, config.relayRoom, config.relayToken, RelayPeerRole.Receiver);
            }
            else if (config.webSocketServer)
            {
                TransportConfig parsed = TransportConfig.ParseWebSocketUrl(config.webSocketUrl);
                int port = parsed.RemotePort > 0 ? parsed.RemotePort : config.localPort;
                transportConfig = TransportConfig.CreateWebSocketServer(port, parsed.WebSocketPath);
            }
            else
            {
                transportConfig = TransportConfig.WebSocketClient(config.webSocketUrl);
            }

            if (!runtime.Transport.Start(transportConfig))
            {
                runtime.Transport.Log -= OnTransportLog;
                runtime.Transport.Dispose();
                runtime.Transport = null;
                return false;
            }

            int sourceIndex = index;
            runtime.Core = new StreamReceiverCore(runtime.Transport, maxDecodeFps, false);
            runtime.Core.FrameDecoded += texture => OnFrameDecoded(sourceIndex, texture);
            runtime.Core.ControlReceived += message => OnControlReceived(sourceIndex, message);
            runtime.Core.Attach();
            runtime.State = StreamPlaybackState.Playing;
            runtime.Core.AcceptFrames = true;

            if (playAudio)
            {
                runtime.AudioObject = new GameObject("FXM Audio " + index);
                runtime.AudioObject.transform.SetParent(transform, false);
                runtime.Audio = runtime.AudioObject.AddComponent<StreamAudioPlayer>();
                runtime.Audio.SetVolume(audioVolume);
                runtime.Core.AudioReceived += runtime.Audio.Push;
            }

            return true;
        }

        private void StopSource(SourceRuntime runtime)
        {
            if (runtime == null)
            {
                return;
            }

            if (runtime.Core != null)
            {
                if (runtime.Audio != null)
                {
                    runtime.Core.AudioReceived -= runtime.Audio.Push;
                }

                runtime.Core.Dispose();
                runtime.Core = null;
            }

            if (runtime.AudioObject != null)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(runtime.AudioObject);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(runtime.AudioObject);
                }

                runtime.AudioObject = null;
                runtime.Audio = null;
            }

            if (runtime.Transport != null)
            {
                runtime.Transport.Log -= OnTransportLog;
                runtime.Transport.Stop();
                runtime.Transport.Dispose();
                runtime.Transport = null;
            }
        }

        private void OnTransportLog(string message)
        {
            UnityEngine.Debug.Log("[FXM.WebStream][MultiReceiver] " + message);
        }

        // ------------------------------------------------------------------
        // Frames, state, and presentation
        // ------------------------------------------------------------------

        private void OnFrameDecoded(int index, Texture2D texture)
        {
            var image = GetView(index);
            if (image != null && (showAllSources || index == ActiveSourceIndex))
            {
                if (image.texture != texture)
                {
                    image.texture = texture;
                }

                if (preserveAspectRatio)
                {
                    ApplyAspect(image, texture);
                }
            }

            var handler = SourceFrameDecoded;
            if (handler != null)
            {
                handler(index, texture);
            }
        }

        private void OnControlReceived(int index, StreamControlMessage message)
        {
            if (index < 0 || index >= _runtimes.Length)
            {
                return;
            }

            var runtime = _runtimes[index];
            if (runtime == null)
            {
                return;
            }

            if (message.Kind != StreamMessageKind.State)
            {
                return;
            }

            if (message.SourceId != 0 && message.SourceId != runtime.SourceId)
            {
                // New or restarted sender: the state sequence starts over.
                runtime.SourceId = message.SourceId;
                runtime.HasStateSequence = false;
            }

            if (runtime.HasStateSequence && !IsNewer(message.Sequence, runtime.LastStateSequence))
            {
                return;
            }

            runtime.HasStateSequence = true;
            runtime.LastStateSequence = message.Sequence;
            runtime.PositionSeconds = message.PositionMs / 1000f;
            runtime.DurationSeconds = message.DurationMs / 1000f;

            if (runtime.State != message.State)
            {
                runtime.State = message.State;
                SetSourceAcceptFrames(runtime, message.State == StreamPlaybackState.Playing);

                if (runtime.Audio != null)
                {
                    runtime.Audio.SetPlaying(message.State == StreamPlaybackState.Playing);
                }

                if (message.State == StreamPlaybackState.Stopped && clearOnStop)
                {
                    var image = GetView(index);
                    if (image != null)
                    {
                        image.texture = null;
                    }
                }

                var handler = SourceStateChanged;
                if (handler != null)
                {
                    handler(index, message.State);
                }
            }
        }

        private static void SetSourceAcceptFrames(SourceRuntime runtime, bool accept)
        {
            if (runtime.Core != null)
            {
                runtime.Core.AcceptFrames = accept;
            }
        }

        private void UpdateAspects()
        {
            for (int i = 0; i < _runtimes.Length; i++)
            {
                var runtime = _runtimes[i];
                var image = GetView(i);
                if (runtime == null || runtime.Core == null || image == null || image.texture == null)
                {
                    continue;
                }

                ApplyAspect(image, runtime.Core.Texture);
            }
        }

        private static void ApplyAspect(RawImage image, Texture texture)
        {
            if (image == null || texture == null || texture.height == 0)
            {
                return;
            }

            Rect rect = image.rectTransform.rect;
            if (rect.height <= 0f || rect.width <= 0f)
            {
                return;
            }

            float targetAspect = rect.width / rect.height;
            float sourceAspect = texture.width / (float)texture.height;

            if (sourceAspect > targetAspect)
            {
                // Source is wider than the view: crop left/right, keep full height.
                float width = targetAspect / sourceAspect;
                image.uvRect = new Rect((1f - width) * 0.5f, 0f, width, 1f);
            }
            else
            {
                // Source is taller than the view: crop top/bottom, keep full width.
                float height = sourceAspect / targetAspect;
                image.uvRect = new Rect(0f, (1f - height) * 0.5f, 1f, height);
            }
        }

        private RawImage GetView(int index)
        {
            if (views == null || index < 0 || index >= views.Length)
            {
                return null;
            }

            return views[index];
        }

        private void ApplyViewVisibility()
        {
            if (views == null)
            {
                return;
            }

            int active = ActiveSourceIndex;
            for (int i = 0; i < views.Length; i++)
            {
                if (views[i] != null)
                {
                    views[i].enabled = showAllSources || i == active;
                }
            }
        }

        private SourceRuntime GetActiveRuntime()
        {
            if (_runtimes == null || _runtimes.Length == 0)
            {
                return null;
            }

            return _runtimes[ActiveSourceIndex];
        }

        /// <summary>Switch between the grid and the single active source at runtime.</summary>
        public void SetShowAllSources(bool value)
        {
            if (showAllSources == value)
            {
                return;
            }

            showAllSources = value;
            ApplyViewVisibility();
            SetActiveSource(ActiveSourceIndex);
        }

        /// <summary>Select the visible source in many-to-one mode.</summary>
        public void SetActiveSource(int index)
        {
            if (_runtimes.Length == 0)
            {
                return;
            }

            activeSourceIndex = Mathf.Clamp(index, 0, _runtimes.Length - 1);
            ApplyViewVisibility();

            if (audioFollowsActiveSource)
            {
                for (int i = 0; i < _runtimes.Length; i++)
                {
                    var audioRuntime = _runtimes[i];
                    if (audioRuntime == null || audioRuntime.Audio == null)
                    {
                        continue;
                    }

                    if (i == activeSourceIndex)
                    {
                        // Drop audio that arrived while this source was muted, then restart
                        // from fresh packets so switching sources does not add seconds of lag.
                        audioRuntime.Audio.Clear();
                        audioRuntime.Audio.SetPlaying(true);
                    }
                    else
                    {
                        audioRuntime.Audio.SetPlaying(false);
                    }
                }
            }

            // Show the last decoded frame so the view does not wait for a new one.
            var runtime = _runtimes[activeSourceIndex];
            var image = GetView(activeSourceIndex);
            if (image != null && runtime != null && runtime.Core != null && runtime.Core.Texture != null)
            {
                image.texture = runtime.Core.Texture;
            }
        }

        // ------------------------------------------------------------------
        // Playback control
        // ------------------------------------------------------------------

        /// <summary>Send Play to all sources (or only the active source when showAllSources = false).</summary>
        public void Play() { SendToSources(StreamCommandType.Play, 0); }
        public void Pause() { SendToSources(StreamCommandType.Pause, 0); }
        public void Stop() { SendToSources(StreamCommandType.Stop, 0); }
        public void Restart() { SendToSources(StreamCommandType.Restart, 0); }

        public void Seek(float seconds)
        {
            SendToSources(StreamCommandType.Seek, Mathf.Max(0, Mathf.RoundToInt(seconds * 1000f)));
        }

        public StreamPlaybackState GetSourceState(int index)
        {
            if (_runtimes == null || index < 0 || index >= _runtimes.Length || _runtimes[index] == null)
            {
                return StreamPlaybackState.Stopped;
            }

            return _runtimes[index].State;
        }

        public uint GetSourceId(int index)
        {
            if (_runtimes == null || index < 0 || index >= _runtimes.Length || _runtimes[index] == null)
            {
                return 0;
            }

            return _runtimes[index].SourceId;
        }

        public Texture2D GetSourceTexture(int index)
        {
            if (_runtimes == null || index < 0 || index >= _runtimes.Length || _runtimes[index] == null ||
                _runtimes[index].Core == null)
            {
                return null;
            }

            return _runtimes[index].Core.Texture;
        }

        public string GetSourceName(int index)
        {
            if (_runtimes == null || index < 0 || index >= _runtimes.Length || _runtimes[index] == null)
            {
                return string.Empty;
            }

            return _runtimes[index].Config.name;
        }

        public INetworkTransport GetSourceTransport(int index)
        {
            if (_runtimes == null || index < 0 || index >= _runtimes.Length || _runtimes[index] == null)
            {
                return null;
            }

            return _runtimes[index].Transport;
        }

        /// <summary>Mute or unmute the audio of one source (many-to-one selection).</summary>
        public void SetSourceAudioMuted(int index, bool muted)
        {
            if (_runtimes == null || index < 0 || index >= _runtimes.Length || _runtimes[index] == null ||
                _runtimes[index].Audio == null)
            {
                return;
            }

            _runtimes[index].Audio.SetMuted(muted);
        }

        private void SendToSources(StreamCommandType command, int value)
        {
            if (!_running || _runtimes == null)
            {
                return;
            }

            int active = ActiveSourceIndex;

            for (int i = 0; i < _runtimes.Length; i++)
            {
                var runtime = _runtimes[i];
                if (runtime == null || runtime.Transport == null)
                {
                    continue;
                }

                if (!showAllSources && i != active)
                {
                    continue;
                }

                var message = StreamControlMessage.CommandMessage(command, runtime.SourceId, value);
                var buffer = new byte[StreamControlProtocol.HeaderSize];
                StreamControlProtocol.Write(buffer, 0, message);
                runtime.Transport.SendControl(new ArraySegment<byte>(buffer));

                // Optimistic update so the UI reacts immediately; the sender heartbeat
                // corrects it if the command is lost.
                if (command == StreamCommandType.Play || command == StreamCommandType.Restart)
                {
                    runtime.State = StreamPlaybackState.Playing;
                    SetSourceAcceptFrames(runtime, true);
                    if (runtime.Audio != null)
                    {
                        runtime.Audio.SetPlaying(true);
                    }
                }
                else if (command == StreamCommandType.Pause)
                {
                    runtime.State = StreamPlaybackState.Paused;
                    SetSourceAcceptFrames(runtime, false);
                    if (runtime.Audio != null)
                    {
                        runtime.Audio.SetPlaying(false);
                    }
                }
                else if (command == StreamCommandType.Stop)
                {
                    runtime.State = StreamPlaybackState.Stopped;
                    SetSourceAcceptFrames(runtime, false);
                    if (runtime.Audio != null)
                    {
                        runtime.Audio.SetPlaying(false);
                    }

                    if (clearOnStop)
                    {
                        var image = GetView(i);
                        if (image != null)
                        {
                            image.texture = null;
                        }
                    }
                }
            }
        }

        private static bool IsNewer(uint candidate, uint reference)
        {
            return unchecked((int)(candidate - reference)) > 0;
        }
    }
}
