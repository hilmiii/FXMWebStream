using System;

namespace FXM.WebStream
{
    /// <summary>Available transport kinds.</summary>
    public enum TransportKind
    {
        /// <summary>Raw UDP: lowest latency, best for LAN, desktop, and mobile.</summary>
        Udp = 0,

        /// <summary>WebSocket: passes through firewalls and proxies; the only option for WebGL.</summary>
        WebSocket = 1,

        /// <summary>
        /// WebSocket relay: sender and receiver both connect as clients to a public relay.
        /// This works across NAT and mobile networks without port forwarding.
        /// </summary>
        Relay = 2
    }

    /// <summary>Role of a peer inside a relay room.</summary>
    public enum RelayPeerRole
    {
        Sender = 0,
        Receiver = 1,

        /// <summary>
        /// Symmetric gameplay channel. Every player state packet is forwarded to the
        /// other players in the room; video senders and receivers are unaffected.
        /// </summary>
        Player = 2
    }

    /// <summary>Communication direction of a transport.</summary>
    public enum TransportRole
    {
        /// <summary>Send only (used by StreamingSender).</summary>
        SendOnly = 0,

        /// <summary>Receive only (used by StreamingReceiver).</summary>
        ReceiveOnly = 1,

        /// <summary>Can send and receive on the same socket.</summary>
        Bidirectional = 2
    }

    /// <summary>
    /// Transport configuration. Every value has a safe default, so callers only need to
    /// set the fields relevant to them (see the static helpers below).
    /// </summary>
    [Serializable]
    public sealed class TransportConfig
    {
        /// <summary>Destination address for client/send mode.</summary>
        public string RemoteHost = "127.0.0.1";

        /// <summary>Destination port.</summary>
        public int RemotePort = 47777;

        /// <summary>Local port to bind. 0 = random ephemeral port.</summary>
        public int LocalPort = 47777;

        /// <summary>Communication direction.</summary>
        public TransportRole Role = TransportRole.Bidirectional;

        /// <summary>MTU including the packet header. 1200 is safe for IPv4/IPv6 and VPNs.</summary>
        public int Mtu = 1200;

        /// <summary>Outgoing frame queue limit; the oldest frame is dropped when full.</summary>
        public int SendQueueCapacity = 4;

        /// <summary>Maximum number of frames waiting for reassembly on the receiving side.</summary>
        public int MaxPendingFrames = 4;

        /// <summary>Seconds before an incomplete frame is discarded.</summary>
        public float ReassemblyTimeoutSeconds = 2f;

        /// <summary>WebSocket: run as a server (TcpListener) instead of a client.</summary>
        public bool WebSocketServer;

        /// <summary>WebSocket: HTTP path served by the server, for example "/fxm-stream/".</summary>
        public string WebSocketPath = "/fxm-stream/";

        /// <summary>WebSocket: use the wss:// scheme (required when a WebGL page is served over HTTPS).</summary>
        public bool WebSocketSecure;

        /// <summary>
        /// Query string appended to the WebSocket URL (without the leading '?').
        /// The relay uses it for room, role, and token.
        /// </summary>
        public string WebSocketQuery;

        /// <summary>Reconnect automatically when a WebSocket client connection drops.</summary>
        public bool AutoReconnect;

        /// <summary>Host resolution happens once during Start().</summary>
        public static TransportConfig Udp(string remoteHost, int remotePort, TransportRole role, int mtu = 1200)
        {
            return new TransportConfig
            {
                RemoteHost = remoteHost,
                RemotePort = remotePort,
                LocalPort = role == TransportRole.SendOnly ? 0 : remotePort,
                Role = role,
                Mtu = mtu
            };
        }

        /// <summary>
        /// UDP configuration with an explicit local port. This lets a sender listen for
        /// playback commands on a control port (for example 47800) while still sending
        /// frames to a receiver port (for example 47777) on the same machine.
        /// </summary>
        public static TransportConfig CreateUdp(int localPort, string remoteHost, int remotePort,
            TransportRole role, int mtu = 1200)
        {
            return new TransportConfig
            {
                RemoteHost = remoteHost,
                RemotePort = remotePort,
                LocalPort = Math.Max(0, localPort),
                Role = role,
                Mtu = mtu
            };
        }

        /// <summary>WebSocket client transport (used by desktop and WebGL receivers).</summary>
        public static TransportConfig WebSocketClient(string url)
        {
            return ParseWebSocketUrl(url);
        }

        /// <summary>WebSocket server transport (usually on a desktop sender).</summary>
        public static TransportConfig CreateWebSocketServer(int port, string path = "/fxm-stream/")
        {
            return new TransportConfig
            {
                WebSocketServer = true,
                LocalPort = port,
                RemotePort = port,
                WebSocketPath = string.IsNullOrEmpty(path) ? "/fxm-stream/" : path,
                Role = TransportRole.Bidirectional
            };
        }

        /// <summary>
        /// Build a relay client configuration. Both peers connect to the same public
        /// WebSocket endpoint and are separated by room name.
        /// </summary>
        public static TransportConfig CreateRelay(string relayUrl, string room, string token, RelayPeerRole role)
        {
            string url = string.IsNullOrWhiteSpace(relayUrl)
                ? "ws://127.0.0.1:8787/fxm-relay"
                : relayUrl.Trim();

            var config = ParseWebSocketUrl(url, "/fxm-relay");
            config.Role = TransportRole.Bidirectional;
            config.AutoReconnect = true;

            string safeRoom = string.IsNullOrWhiteSpace(room) ? "demo" : room.Trim();
            string roleName = role == RelayPeerRole.Sender
                ? "sender"
                : (role == RelayPeerRole.Receiver ? "receiver" : "player");
            string query = "room=" + Uri.EscapeDataString(safeRoom) + "&role=" + roleName;

            // Mobile keyboards often append a trailing space; the relay compares tokens exactly.
            string safeToken = token != null ? token.Trim() : string.Empty;
            if (safeToken.Length > 0)
            {
                query += "&token=" + Uri.EscapeDataString(safeToken);
            }

            config.WebSocketQuery = query;
            return config;
        }

        /// <summary>
        /// Turn a WebSocket URL into a client configuration. Accepts <c>ws://</c> and
        /// <c>wss://</c> URLs, maps <c>http(s)://</c> to <c>ws(s)://</c>, and tolerates a
        /// missing scheme (for example <c>relay.example.com:8787</c>).
        /// </summary>
        public static TransportConfig ParseWebSocketUrl(string url, string defaultPath = "/fxm-stream/")
        {
            var config = new TransportConfig
            {
                WebSocketServer = false,
                WebSocketPath = string.IsNullOrEmpty(defaultPath) ? "/" : defaultPath,
                Role = TransportRole.Bidirectional
            };

            if (string.IsNullOrWhiteSpace(url))
            {
                return config;
            }

            string normalized = NormalizeWebSocketUrl(url);
            Uri uri;
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out uri))
            {
                return config;
            }

            bool secure = uri.Scheme == "wss" || uri.Scheme == "https";
            int port = uri.IsDefaultPort ? (secure ? 443 : 80) : uri.Port;
            if (port <= 0)
            {
                port = secure ? 443 : 80;
            }

            config.RemoteHost = uri.Host;
            config.RemotePort = port;
            config.LocalPort = port;
            config.WebSocketSecure = secure;
            if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
            {
                config.WebSocketPath = uri.AbsolutePath;
            }

            return config;
        }

        private static string NormalizeWebSocketUrl(string url)
        {
            string value = url.Trim();

            if (value.StartsWith("//", StringComparison.Ordinal))
            {
                return "ws:" + value;
            }

            int schemeSeparator = value.IndexOf("://", StringComparison.Ordinal);
            if (schemeSeparator < 0)
            {
                // A bare host, host:port, or host/path value means plain WebSocket.
                return "ws://" + value;
            }

            string scheme = value.Substring(0, schemeSeparator).ToLowerInvariant();
            switch (scheme)
            {
                case "http":
                    return "ws" + value.Substring(schemeSeparator);
                case "https":
                    return "wss" + value.Substring(schemeSeparator);
                default:
                    return value;
            }
        }

        /// <summary>Rebuild the WebSocket URL from the configuration (used by clients).</summary>
        public string BuildWebSocketUrl()
        {
            string path = WebSocketPath;
            if (string.IsNullOrEmpty(path))
            {
                path = "/";
            }

            if (!path.StartsWith("/", StringComparison.Ordinal))
            {
                path = "/" + path;
            }

            string authority = RemoteHost;
            if (RemotePort > 0)
            {
                authority += ":" + RemotePort;
            }

            string url = (WebSocketSecure ? "wss://" : "ws://") + authority + path;

            if (!string.IsNullOrEmpty(WebSocketQuery))
            {
                url += (url.IndexOf('?') >= 0 ? "&" : "?") + WebSocketQuery;
            }

            return url;
        }
    }

    /// <summary>
    /// Transport statistics. All counters are cumulative since Start() and can be read
    /// safely from the main thread while network threads write them.
    /// </summary>
    public sealed class TransportStatistics
    {
        private long _framesSent;
        private long _framesReceived;
        private long _bytesSent;
        private long _bytesReceived;
        private long _packetsSent;
        private long _packetsReceived;
        private long _packetsMalformed;
        private long _framesDropped;
        private long _sendQueueDrops;
        private long _controlMessagesSent;
        private long _controlMessagesReceived;

        public long FramesSent { get { return Read(ref _framesSent); } }
        public long FramesReceived { get { return Read(ref _framesReceived); } }
        public long BytesSent { get { return Read(ref _bytesSent); } }
        public long BytesReceived { get { return Read(ref _bytesReceived); } }
        public long PacketsSent { get { return Read(ref _packetsSent); } }
        public long PacketsReceived { get { return Read(ref _packetsReceived); } }
        public long PacketsMalformed { get { return Read(ref _packetsMalformed); } }
        public long FramesDropped { get { return Read(ref _framesDropped); } }
        public long SendQueueDrops { get { return Read(ref _sendQueueDrops); } }
        public long ControlMessagesSent { get { return Read(ref _controlMessagesSent); } }
        public long ControlMessagesReceived { get { return Read(ref _controlMessagesReceived); } }

        internal void AddFrameSent() { System.Threading.Interlocked.Increment(ref _framesSent); }
        internal void AddFrameReceived() { System.Threading.Interlocked.Increment(ref _framesReceived); }
        internal void AddBytesSent(long value) { System.Threading.Interlocked.Add(ref _bytesSent, value); }
        internal void AddBytesReceived(long value) { System.Threading.Interlocked.Add(ref _bytesReceived, value); }
        internal void AddPacketSent() { System.Threading.Interlocked.Increment(ref _packetsSent); }
        internal void AddPacketReceived() { System.Threading.Interlocked.Increment(ref _packetsReceived); }
        internal void AddMalformed() { System.Threading.Interlocked.Increment(ref _packetsMalformed); }
        internal void AddDropped() { System.Threading.Interlocked.Increment(ref _framesDropped); }
        internal void AddSendQueueDrop() { System.Threading.Interlocked.Increment(ref _sendQueueDrops); }
        internal void AddControlSent() { System.Threading.Interlocked.Increment(ref _controlMessagesSent); }
        internal void AddControlReceived() { System.Threading.Interlocked.Increment(ref _controlMessagesReceived); }

        internal void Reset()
        {
            System.Threading.Interlocked.Exchange(ref _framesSent, 0);
            System.Threading.Interlocked.Exchange(ref _framesReceived, 0);
            System.Threading.Interlocked.Exchange(ref _bytesSent, 0);
            System.Threading.Interlocked.Exchange(ref _bytesReceived, 0);
            System.Threading.Interlocked.Exchange(ref _packetsSent, 0);
            System.Threading.Interlocked.Exchange(ref _packetsReceived, 0);
            System.Threading.Interlocked.Exchange(ref _packetsMalformed, 0);
            System.Threading.Interlocked.Exchange(ref _framesDropped, 0);
            System.Threading.Interlocked.Exchange(ref _sendQueueDrops, 0);
            System.Threading.Interlocked.Exchange(ref _controlMessagesSent, 0);
            System.Threading.Interlocked.Exchange(ref _controlMessagesReceived, 0);
        }

        private static long Read(ref long value)
        {
            return System.Threading.Interlocked.Read(ref value);
        }
    }

    /// <summary>
    /// Network transport contract. Built-in implementations: UDPTransport and
    /// WebSocketTransport. Other protocols (WebRTC DataChannel, TCP, named pipes) only
    /// need to implement this interface without changing sender or receiver code.
    /// </summary>
    public interface INetworkTransport : IDisposable
    {
        /// <summary>True after Start() succeeds and before Stop().</summary>
        bool IsRunning { get; }

        /// <summary>Connected peer count (UDP = 1 while running; WebSocket server = client count).</summary>
        int PeerCount { get; }

        /// <summary>Cumulative transport statistics.</summary>
        TransportStatistics Statistics { get; }

        /// <summary>
        /// A complete frame was received. Called ONLY from Poll() on the main thread.
        /// The ArraySegment is valid only during the callback; copy it if you need to keep it.
        /// </summary>
        event Action<ArraySegment<byte>> FrameReceived;

        /// <summary>
        /// A control message (play/pause/stop/seek) was received. Like FrameReceived, this
        /// callback only runs inside Poll() on the main thread. Control messages are not
        /// chunked and do not share the frame queue, so video cannot overwrite them.
        /// </summary>
        event Action<ArraySegment<byte>> ControlReceived;

        /// <summary>Diagnostic message (error or state change). May be called from another thread.</summary>
        event Action<string> Log;

        /// <summary>Start the transport. Returns false when the socket or port cannot be used.</summary>
        bool Start(TransportConfig config);

        /// <summary>Queue one complete frame. Data is copied, so the caller's buffer is immediately reusable.</summary>
        void Send(ArraySegment<byte> frame);

        /// <summary>
        /// Send a small control message (a few dozen bytes) outside the video frame queue.
        /// Implementations try to send immediately so commands do not wait behind queued
        /// frames. Delivery is still best-effort (UDP can drop packets).
        /// </summary>
        void SendControl(ArraySegment<byte> message);

        /// <summary>Process the incoming frame queue and raise FrameReceived. Call every frame from the main thread.</summary>
        void Poll();

        /// <summary>Stop threads and close sockets. Safe to call repeatedly.</summary>
        void Stop();
    }

    /// <summary>Simple factory so sender and receiver do not depend on concrete classes.</summary>
    public static class TransportFactory
    {
        public static INetworkTransport Create(TransportKind kind)
        {
            switch (kind)
            {
                case TransportKind.WebSocket:
                case TransportKind.Relay:
                    return new WebSocketTransport();
                default:
                    return new UDPTransport();
            }
        }
    }
}
