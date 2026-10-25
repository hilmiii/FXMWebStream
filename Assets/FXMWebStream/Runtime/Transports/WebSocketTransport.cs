#if UNITY_WEBGL && !UNITY_EDITOR
// ============================================================================
//  WebGL implementation: the browser WebSocket is reached through a jslib plugin
//  because WebGL has no sockets, threads, or ClientWebSocket.
// ============================================================================
using System;

namespace FXM.WebStream
{
    public sealed class WebSocketTransport : INetworkTransport
    {
        private readonly TransportStatistics _statistics = new TransportStatistics();
        private readonly FrameBufferPool _receivePool = new FrameBufferPool(4);
        private WebGLWebSocketClient _client;
        private bool _running;
        private long _lastReceiveTimestamp;

        public bool IsRunning { get { return _running; } }
        public int PeerCount { get { return _running && _client != null && _client.IsOpen ? 1 : 0; } }
        public TransportStatistics Statistics { get { return _statistics; } }

        public event Action<ArraySegment<byte>> FrameReceived;
        public event Action<ArraySegment<byte>> ControlReceived;
        public event Action<string> Log;

        public bool Start(TransportConfig config)
        {
            Stop();

            if (config == null)
            {
                config = new TransportConfig();
            }

            if (config.WebSocketServer)
            {
                RaiseLog("A WebSocket server is not supported on WebGL. Run the sender as a server on desktop, or use the relay.");
                return false;
            }

            _client = new WebGLWebSocketClient();
            if (!_client.Connect(config.BuildWebSocketUrl()))
            {
                RaiseLog("WebSocket WebGL failed to connect to " + config.RemoteHost + ":" +
                         config.RemotePort + config.WebSocketPath);
                _client.Dispose();
                _client = null;
                return false;
            }

            _statistics.Reset();
            _running = true;
            RaiseLog("WebSocket WebGL connecting to " + config.RemoteHost + ":" +
                     config.RemotePort + config.WebSocketPath);
            return true;
        }

        public void Send(ArraySegment<byte> frame)
        {
            if (!_running || _client == null || frame.Array == null || frame.Count <= 0)
            {
                return;
            }

            if (_client.Send(frame.Array, frame.Offset, frame.Count))
            {
                _statistics.AddFrameSent();
                _statistics.AddBytesSent(frame.Count);
            }
        }

        public void SendControl(ArraySegment<byte> message)
        {
            if (!_running || _client == null || message.Array == null || message.Count <= 0)
            {
                return;
            }

            if (_client.Send(message.Array, message.Offset, message.Count))
            {
                _statistics.AddControlSent();
            }
        }

        public void Poll()
        {
            if (!_running || _client == null)
            {
                return;
            }

            // One buffer serves every message in this Poll: handlers run synchronously
            // and do not keep the ArraySegment, so reuse is safe.
            byte[] buffer = _receivePool.Rent(2 * 1024 * 1024);

            try
            {
                for (int i = 0; i < 8; i++)
                {
                    int length = _client.PollMessage(buffer);
                    if (length <= 0)
                    {
                        if (length == -2)
                        {
                            RaiseLog("WebSocket WebGL disconnected.");
                            _running = false;
                        }

                        break;
                    }

                    _statistics.AddBytesReceived(length);
                    _lastReceiveTimestamp = DateTime.UtcNow.Ticks;

                    if (StreamControlProtocol.IsControlPacket(buffer, 0, length))
                    {
                        _statistics.AddControlReceived();
                        var controlHandler = ControlReceived;
                        if (controlHandler != null)
                        {
                            controlHandler(new ArraySegment<byte>(buffer, 0, length));
                        }

                        continue;
                    }

                    _statistics.AddFrameReceived();
                    var handler = FrameReceived;
                    if (handler != null)
                    {
                        handler(new ArraySegment<byte>(buffer, 0, length));
                    }
                }
            }
            finally
            {
                _receivePool.Return(buffer);
            }
        }

        public void Stop()
        {
            _running = false;

            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void RaiseLog(string message)
        {
            var handler = Log;
            if (handler != null)
            {
                handler(message);
            }
        }
    }
}
#else
// ============================================================================
//  Desktop/mobile implementation: TcpListener server + ClientWebSocket client.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace FXM.WebStream
{
    /// <summary>
    /// WebSocket transport for WebGL compatibility and firewall/proxy traversal.
    ///
    /// Two modes:
    /// - Server (the default on a sender): TcpListener + RFC 6455 handshake + manual framing.
    ///   It avoids HttpListener so Windows needs no urlacl/admin rights and it also runs on
    ///   standalone and mobile players.
    /// - Client (the default on receivers and WebGL): the .NET ClientWebSocket.
    ///
    /// One binary WebSocket message = one complete JPEG frame, so no chunking is needed.
    /// The same client mode also connects to the relay with a room/role/token query.
    /// </summary>
    public sealed class WebSocketTransport : INetworkTransport
    {
        private const int MaxMessageBytes = 16 * 1024 * 1024;
        private const int MaxPendingIncoming = 8;

        private struct OutgoingFrame
        {
            public byte[] Buffer;
            public int Length;
        }

        private struct IncomingFrame
        {
            public byte[] Buffer;
            public int Length;
        }

        private sealed class ServerPeer
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object SendLock = new object();
            public volatile bool Alive;
        }

        private readonly TransportStatistics _statistics = new TransportStatistics();
        private readonly FrameBufferPool _sendPool = new FrameBufferPool(4);
        private readonly FrameBufferPool _receivePool = new FrameBufferPool(8);
        private readonly Queue<OutgoingFrame> _sendQueue = new Queue<OutgoingFrame>();
        private readonly Queue<OutgoingFrame> _controlSendQueue = new Queue<OutgoingFrame>();
        private readonly Queue<IncomingFrame> _incomingQueue = new Queue<IncomingFrame>();
        private readonly Queue<IncomingFrame> _controlQueue = new Queue<IncomingFrame>();
        private readonly List<ServerPeer> _peers = new List<ServerPeer>();
        private readonly object _sendLock = new object();
        private readonly object _incomingLock = new object();
        private readonly object _controlLock = new object();
        private readonly object _controlSendLock = new object();
        private readonly object _peersLock = new object();
        private readonly object _clientSendLock = new object();

        private TransportConfig _config;
        private Uri _serverUri;
        private TcpListener _listener;
        private volatile ClientWebSocket _client;
        private CancellationTokenSource _clientCancellation;
        private Thread _sendThread;
        private Thread _acceptThread;
        private Thread _clientThread;
        private volatile bool _running;
        private volatile bool _clientConnected;
        private int _sendQueueCapacity = 4;
        private long _lastLogTimestamp;

        public bool IsRunning { get { return _running; } }
        public TransportStatistics Statistics { get { return _statistics; } }

        public int PeerCount
        {
            get
            {
                if (!_running)
                {
                    return 0;
                }

                if (_config != null && !_config.WebSocketServer)
                {
                    return _clientConnected ? 1 : 0;
                }

                lock (_peersLock)
                {
                    int count = 0;
                    for (int i = 0; i < _peers.Count; i++)
                    {
                        if (_peers[i].Alive)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }
        }

        public event Action<ArraySegment<byte>> FrameReceived;
        public event Action<ArraySegment<byte>> ControlReceived;
        public event Action<string> Log;

        public bool Start(TransportConfig config)
        {
            Stop();

            _config = config ?? new TransportConfig();
            _sendQueueCapacity = Math.Max(1, _config.SendQueueCapacity);
            _statistics.Reset();
            _clientConnected = false;

            if (_config.WebSocketServer)
            {
                try
                {
                    _listener = new TcpListener(IPAddress.Any, Math.Max(0, _config.LocalPort));
                    _listener.Start();
                }
                catch (Exception exception)
                {
                    RaiseLog("WebSocket server failed to bind port " + _config.LocalPort + ": " + exception.Message);
                    _listener = null;
                    return false;
                }

                _running = true;
                _acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "FXM.WsAccept"
                };
                _acceptThread.Start();
                RaiseLog("WebSocket server listening on ws://0.0.0.0:" + _config.LocalPort + _config.WebSocketPath);
            }
            else
            {
                string url = _config.BuildWebSocketUrl();
                if (!Uri.TryCreate(url, UriKind.Absolute, out _serverUri))
                {
                    RaiseLog("Invalid WebSocket URL: " + url);
                    return false;
                }

                _clientCancellation = new CancellationTokenSource();
                _running = true;
                _clientThread = new Thread(ClientLoop)
                {
                    IsBackground = true,
                    Name = "FXM.WsClient"
                };
                _clientThread.Start();
                RaiseLog("WebSocket client connecting to " + DescribeEndpoint() +
                         (_config.WebSocketSecure ? "" : " (plain ws://, use wss:// outside a trusted network)"));
            }

            _sendThread = new Thread(SendLoop)
            {
                IsBackground = true,
                Name = "FXM.WsSend"
            };
            _sendThread.Start();

            return true;
        }

        public void Send(ArraySegment<byte> frame)
        {
            if (!_running || frame.Count <= 0 || frame.Array == null)
            {
                return;
            }

            byte[] copy = _sendPool.Rent(frame.Count);
            Buffer.BlockCopy(frame.Array, frame.Offset, copy, 0, frame.Count);

            OutgoingFrame dropped = default(OutgoingFrame);
            bool hasDropped = false;

            lock (_sendLock)
            {
                if (_sendQueue.Count >= _sendQueueCapacity)
                {
                    dropped = _sendQueue.Dequeue();
                    hasDropped = true;
                    _statistics.AddSendQueueDrop();
                }

                _sendQueue.Enqueue(new OutgoingFrame { Buffer = copy, Length = frame.Count });
                Monitor.Pulse(_sendLock);
            }

            if (hasDropped)
            {
                _sendPool.Return(dropped.Buffer);
            }
        }

        /// <summary>
        /// Queue a control message. The main thread only copies a few dozen bytes; the send
        /// thread owns the socket, so the game never waits on a slow client. Control messages
        /// are always sent before the next video frame.
        /// </summary>
        public void SendControl(ArraySegment<byte> message)
        {
            if (!_running || message.Array == null || message.Count <= 0)
            {
                return;
            }

            byte[] copy = _sendPool.Rent(message.Count);
            Buffer.BlockCopy(message.Array, message.Offset, copy, 0, message.Count);

            bool dropped = false;
            OutgoingFrame old = default(OutgoingFrame);

            lock (_controlSendLock)
            {
                if (_controlSendQueue.Count >= 16)
                {
                    old = _controlSendQueue.Dequeue();
                    dropped = true;
                }

                _controlSendQueue.Enqueue(new OutgoingFrame { Buffer = copy, Length = message.Count });
            }

            if (dropped)
            {
                _sendPool.Return(old.Buffer);
                _statistics.AddDropped();
            }

            lock (_sendLock)
            {
                Monitor.Pulse(_sendLock);
            }
        }

        private void SendControlNow(byte[] buffer, int length)
        {
            if (_config != null && _config.WebSocketServer)
            {
                ServerPeer[] peers;
                lock (_peersLock)
                {
                    peers = _peers.ToArray();
                }

                for (int i = 0; i < peers.Length; i++)
                {
                    if (peers[i].Alive)
                    {
                        SendControlFrame(peers[i], WebSocketProtocol.OpcodeBinary, buffer, length);
                        _statistics.AddControlSent();
                    }
                }

                return;
            }

            try
            {
                lock (_clientSendLock)
                {
                    if (_client != null && _client.State == WebSocketState.Open)
                    {
                        var token = _clientCancellation != null ? _clientCancellation.Token : CancellationToken.None;
                        _client.SendAsync(
                            new ArraySegment<byte>(buffer, 0, length),
                            WebSocketMessageType.Binary,
                            true,
                            token).GetAwaiter().GetResult();
                        _statistics.AddControlSent();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ignored: the connection is closing.
            }
            catch (Exception exception)
            {
                RaiseLogThrottled("WebSocket control send failed: " + exception.Message);
            }
        }

        public void Poll()
        {
            for (int i = 0; i < MaxPendingIncoming; i++)
            {
                IncomingFrame control;
                lock (_controlLock)
                {
                    if (_controlQueue.Count == 0)
                    {
                        break;
                    }

                    control = _controlQueue.Dequeue();
                }

                try
                {
                    var controlHandler = ControlReceived;
                    if (controlHandler != null)
                    {
                        controlHandler(new ArraySegment<byte>(control.Buffer, 0, control.Length));
                    }
                }
                catch (Exception exception)
                {
                    RaiseLogThrottled("WebSocket: ControlReceived handler threw: " + exception.Message);
                }
                finally
                {
                    _receivePool.Return(control.Buffer);
                }
            }

            for (int i = 0; i < MaxPendingIncoming; i++)
            {
                IncomingFrame frame;
                lock (_incomingLock)
                {
                    if (_incomingQueue.Count == 0)
                    {
                        break;
                    }

                    frame = _incomingQueue.Dequeue();
                }

                try
                {
                    var handler = FrameReceived;
                    if (handler != null)
                    {
                        handler(new ArraySegment<byte>(frame.Buffer, 0, frame.Length));
                    }
                }
                catch (Exception exception)
                {
                    RaiseLogThrottled("WebSocket: FrameReceived handler threw: " + exception.Message);
                }
                finally
                {
                    _receivePool.Return(frame.Buffer);
                }
            }
        }

        public void Stop()
        {
            if (!_running && _listener == null && _client == null)
            {
                return;
            }

            _running = false;

            try
            {
                if (_clientCancellation != null)
                {
                    _clientCancellation.Cancel();
                }
            }
            catch
            {
                // Ignored.
            }

            try
            {
                if (_client != null)
                {
                    _client.Abort();
                }
            }
            catch
            {
                // Ignored.
            }

            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                }
            }
            catch
            {
                // Ignored.
            }

            lock (_sendLock)
            {
                Monitor.PulseAll(_sendLock);
            }

            // Close every peer so per-peer receive threads stop.
            ServerPeer[] peers;
            lock (_peersLock)
            {
                peers = _peers.ToArray();
                _peers.Clear();
            }

            for (int i = 0; i < peers.Length; i++)
            {
                ClosePeer(peers[i]);
            }

            JoinThread(_sendThread);
            JoinThread(_acceptThread);
            JoinThread(_clientThread);
            _sendThread = null;
            _acceptThread = null;
            _clientThread = null;

            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }

            if (_clientCancellation != null)
            {
                _clientCancellation.Dispose();
                _clientCancellation = null;
            }

            _listener = null;
            _clientConnected = false;

            lock (_sendLock)
            {
                while (_sendQueue.Count > 0)
                {
                    _sendPool.Return(_sendQueue.Dequeue().Buffer);
                }
            }

            lock (_incomingLock)
            {
                while (_incomingQueue.Count > 0)
                {
                    _receivePool.Return(_incomingQueue.Dequeue().Buffer);
                }
            }

            lock (_controlLock)
            {
                while (_controlQueue.Count > 0)
                {
                    _receivePool.Return(_controlQueue.Dequeue().Buffer);
                }
            }

            lock (_controlSendLock)
            {
                while (_controlSendQueue.Count > 0)
                {
                    _sendPool.Return(_controlSendQueue.Dequeue().Buffer);
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        // ------------------------------------------------------------------
        // Client (ClientWebSocket)
        // ------------------------------------------------------------------

        private void ClientLoop()
        {
            CancellationToken token = _clientCancellation.Token;
            int attempt = 0;

            while (_running && !token.IsCancellationRequested)
            {
                var client = new ClientWebSocket();
                client.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                _client = client;

                try
                {
                    using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                        client.ConnectAsync(_serverUri, connectTimeout.Token).GetAwaiter().GetResult();
                    }

                    _clientConnected = true;
                    attempt = 0;
                    RaiseLog("WebSocket connected to " + DescribeEndpoint());

                    ReceiveLoop(client, token);
                }
                catch (OperationCanceledException)
                {
                    if (_running)
                    {
                        RaiseLog("WebSocket connect timed out or was cancelled.");
                    }
                }
                catch (Exception exception)
                {
                    if (_running)
                    {
                        RaiseLog(DescribeConnectFailure(exception));
                    }
                }
                finally
                {
                    _clientConnected = false;

                    if (ReferenceEquals(_client, client))
                    {
                        _client = null;
                    }

                    try
                    {
                        client.Dispose();
                    }
                    catch
                    {
                        // Ignored.
                    }
                }

                if (!_running || _config == null || !_config.AutoReconnect)
                {
                    return;
                }

                int delayMs = Math.Min(10000, 500 * (1 << Math.Min(attempt, 5)));
                attempt++;
                RaiseLog("WebSocket disconnected; reconnecting in " + delayMs + " ms.");

                for (int waited = 0; waited < delayMs && _running; waited += 100)
                {
                    Thread.Sleep(100);
                }
            }
        }

        private void ReceiveLoop(ClientWebSocket client, CancellationToken token)
        {
            var receiveBuffer = new byte[64 * 1024];
            byte[] messageBuffer = null;
            int messageLength = 0;

            try
            {
                while (_running && client.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = client.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (WebSocketException exception)
                    {
                        if (_running)
                        {
                            RaiseLogThrottled("WebSocket receive failed: " + exception.Message);
                        }

                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    if (messageLength + result.Count > MaxMessageBytes)
                    {
                        RaiseLogThrottled("WebSocket: message exceeds " + MaxMessageBytes + " bytes; dropped.");
                        _receivePool.Return(messageBuffer);
                        messageBuffer = null;
                        messageLength = 0;
                        continue;
                    }

                    if (messageBuffer == null)
                    {
                        messageBuffer = _receivePool.Rent(Math.Max(4096, result.Count));
                    }
                    else if (messageLength + result.Count > messageBuffer.Length)
                    {
                        int required = messageLength + result.Count;
                        int size = messageBuffer.Length;
                        while (size < required)
                        {
                            size *= 2;
                        }

                        var larger = _receivePool.Rent(size);
                        Buffer.BlockCopy(messageBuffer, 0, larger, 0, messageLength);
                        _receivePool.Return(messageBuffer);
                        messageBuffer = larger;
                    }

                    Buffer.BlockCopy(receiveBuffer, 0, messageBuffer, messageLength, result.Count);
                    messageLength += result.Count;

                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    if (result.MessageType == WebSocketMessageType.Binary && messageLength > 0)
                    {
                        _statistics.AddBytesReceived(messageLength);

                        if (StreamControlProtocol.IsControlPacket(messageBuffer, 0, messageLength))
                        {
                            EnqueueControl(messageBuffer, messageLength);
                        }
                        else
                        {
                            _statistics.AddFrameReceived();
                            EnqueueIncoming(messageBuffer, messageLength);
                        }

                        messageBuffer = null;
                        messageLength = 0;
                    }
                    else
                    {
                        _receivePool.Return(messageBuffer);
                        messageBuffer = null;
                        messageLength = 0;
                    }
                }
            }
            finally
            {
                _receivePool.Return(messageBuffer);
            }
        }

        // ------------------------------------------------------------------
        // Server (TcpListener + framing RFC 6455)
        // ------------------------------------------------------------------

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;

                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    if (!_running)
                    {
                        return;
                    }

                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    if (!_running)
                    {
                        return;
                    }

                    RaiseLogThrottled("WebSocket accept failed: " + exception.Message);
                    continue;
                }

                client.NoDelay = true;
                var thread = new Thread(() => HandleClient(client))
                {
                    IsBackground = true,
                    Name = "FXM.WsPeer"
                };
                thread.Start();
            }
        }

        private void HandleClient(TcpClient client)
        {
            ServerPeer peer = null;

            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 3000;
                NetworkStream stream = client.GetStream();
                stream.ReadTimeout = 5000;

                var request = new byte[WebSocketProtocol.MaxHandshakeBytes];
                int length = 0;
                bool handshakeComplete = false;

                while (length < request.Length)
                {
                    int read = stream.Read(request, length, request.Length - length);
                    if (read <= 0)
                    {
                        break;
                    }

                    length += read;
                    if (WebSocketProtocol.FindHeaderEnd(request, length) > 0)
                    {
                        handshakeComplete = true;
                        break;
                    }
                }

                string path;
                string key;
                if (!handshakeComplete || !WebSocketProtocol.TryParseHandshake(request, length, out path, out key))
                {
                    WriteRaw(stream, WebSocketProtocol.BuildHttpErrorResponse(400, "Bad Request"));
                    return;
                }

                if (!PathMatches(path))
                {
                    WriteRaw(stream, WebSocketProtocol.BuildHttpErrorResponse(404, "Not Found"));
                    RaiseLog("WebSocket: path '" + path + "' does not match '" + _config.WebSocketPath + "'.");
                    return;
                }

                string response = WebSocketProtocol.BuildHandshakeResponse(WebSocketProtocol.CreateAcceptKey(key));
                WriteRaw(stream, response);

                peer = new ServerPeer
                {
                    Client = client,
                    Stream = stream,
                    Alive = true
                };

                lock (_peersLock)
                {
                    _peers.Add(peer);
                }

                stream.ReadTimeout = Timeout.Infinite;
                RaiseLog("WebSocket client connected (" + client.Client.RemoteEndPoint + ", total " + PeerCount + ").");

                ReadPeerLoop(peer);
            }
            catch (Exception exception)
            {
                if (_running)
                {
                    RaiseLogThrottled("WebSocket peer stopped: " + exception.Message);
                }
            }
            finally
            {
                if (peer != null)
                {
                    RemovePeer(peer);
                }

                ClosePeer(peer);
                try
                {
                    client.Close();
                }
                catch
                {
                    // Ignored.
                }
            }
        }

        private void ReadPeerLoop(ServerPeer peer)
        {
            var header = new byte[8];
            var mask = new byte[4];
            var controlPayload = new byte[125];
            byte[] messageBuffer = null;
            int messageLength = 0;
            bool messageIsBinary = false;

            try
            {
                while (_running && peer.Alive)
                {
                    if (!ReadExact(peer.Stream, header, 0, 2))
                    {
                        break;
                    }

                    bool fin = (header[0] & 0x80) != 0;
                    int opcode = header[0] & 0x0F;
                    bool masked = (header[1] & 0x80) != 0;
                    long payloadLength = header[1] & 0x7F;

                    if (payloadLength == 126)
                    {
                        var extended = new byte[2];
                        if (!ReadExact(peer.Stream, extended, 0, 2))
                        {
                            break;
                        }

                        payloadLength = (extended[0] << 8) | extended[1];
                    }
                    else if (payloadLength == 127)
                    {
                        var extended = new byte[8];
                        if (!ReadExact(peer.Stream, extended, 0, 8))
                        {
                            break;
                        }

                        payloadLength = 0;
                        for (int i = 0; i < 8; i++)
                        {
                            payloadLength = (payloadLength << 8) | extended[i];
                        }
                    }

                    if (!masked || payloadLength < 0 || payloadLength > MaxMessageBytes)
                    {
                        break;
                    }

                    if (!ReadExact(peer.Stream, mask, 0, 4))
                    {
                        break;
                    }

                    if (opcode >= 0x8)
                    {
                        if (payloadLength > 125)
                        {
                            break;
                        }

                        int controlLength = (int)payloadLength;
                        if (!ReadExact(peer.Stream, controlPayload, 0, controlLength))
                        {
                            break;
                        }

                        Unmask(controlPayload, 0, controlLength, mask);

                        if (opcode == WebSocketProtocol.OpcodePing)
                        {
                            SendControlFrame(peer, WebSocketProtocol.OpcodePong, controlPayload, controlLength);
                        }
                        else if (opcode == WebSocketProtocol.OpcodeClose)
                        {
                            SendControlFrame(peer, WebSocketProtocol.OpcodeClose, controlPayload, controlLength);
                            break;
                        }

                        continue;
                    }

                    if (opcode == WebSocketProtocol.OpcodeText || opcode == WebSocketProtocol.OpcodeBinary)
                    {
                        // New frame: drop the previous incomplete message.
                        _receivePool.Return(messageBuffer);
                        messageBuffer = null;
                        messageLength = 0;
                        messageIsBinary = opcode == WebSocketProtocol.OpcodeBinary;
                    }
                    else if (opcode != WebSocketProtocol.OpcodeContinuation || messageBuffer == null)
                    {
                        break;
                    }

                    int required = messageLength + (int)payloadLength;
                    if (required > MaxMessageBytes)
                    {
                        break;
                    }

                    if (messageBuffer == null)
                    {
                        messageBuffer = _receivePool.Rent(Math.Max(4096, required));
                    }
                    else if (required > messageBuffer.Length)
                    {
                        int size = messageBuffer.Length;
                        while (size < required)
                        {
                            size *= 2;
                        }

                        var larger = _receivePool.Rent(size);
                        Buffer.BlockCopy(messageBuffer, 0, larger, 0, messageLength);
                        _receivePool.Return(messageBuffer);
                        messageBuffer = larger;
                    }

                    if (!ReadExact(peer.Stream, messageBuffer, messageLength, (int)payloadLength))
                    {
                        break;
                    }

                    Unmask(messageBuffer, messageLength, (int)payloadLength, mask);
                    messageLength = required;

                    if (!fin)
                    {
                        continue;
                    }

                    if (messageIsBinary && messageLength > 0)
                    {
                        _statistics.AddBytesReceived(messageLength);

                        if (StreamControlProtocol.IsControlPacket(messageBuffer, 0, messageLength))
                        {
                            EnqueueControl(messageBuffer, messageLength);
                        }
                        else
                        {
                            _statistics.AddFrameReceived();
                            EnqueueIncoming(messageBuffer, messageLength);
                        }

                        messageBuffer = null;
                        messageLength = 0;
                    }
                    else
                    {
                        _receivePool.Return(messageBuffer);
                        messageBuffer = null;
                        messageLength = 0;
                    }
                }
            }
            finally
            {
                _receivePool.Return(messageBuffer);
            }
        }

        private void SendLoop()
        {
            while (_running)
            {
                OutgoingFrame control;
                bool hasControl;

                lock (_controlSendLock)
                {
                    hasControl = _controlSendQueue.Count > 0;
                    control = hasControl ? _controlSendQueue.Dequeue() : default(OutgoingFrame);
                }

                if (hasControl)
                {
                    try
                    {
                        SendControlNow(control.Buffer, control.Length);
                    }
                    finally
                    {
                        _sendPool.Return(control.Buffer);
                    }

                    continue;
                }

                OutgoingFrame frame;

                lock (_sendLock)
                {
                    while (_sendQueue.Count == 0 && !HasPendingControl() && _running)
                    {
                        Monitor.Wait(_sendLock, 250);
                    }

                    if (!_running)
                    {
                        return;
                    }

                    if (HasPendingControl())
                    {
                        continue;
                    }

                    if (_sendQueue.Count == 0)
                    {
                        continue;
                    }

                    frame = _sendQueue.Dequeue();
                }

                try
                {
                    if (_config != null && _config.WebSocketServer)
                    {
                        BroadcastToPeers(frame.Buffer, frame.Length);
                    }
                    else if (_clientConnected && _client != null)
                    {
                        lock (_clientSendLock)
                        {
                            _client.SendAsync(
                                new ArraySegment<byte>(frame.Buffer, 0, frame.Length),
                                WebSocketMessageType.Binary,
                                true,
                                CancellationToken.None).GetAwaiter().GetResult();
                        }

                        CountFrameSent(frame.Length);
                    }
                }
                catch (Exception exception)
                {
                    if (_running)
                    {
                        RaiseLogThrottled("WebSocket send failed: " + exception.Message);
                    }
                }
                finally
                {
                    _sendPool.Return(frame.Buffer);
                }
            }
        }

        private void BroadcastToPeers(byte[] payload, int length)
        {
            ServerPeer[] peers;
            lock (_peersLock)
            {
                peers = _peers.ToArray();
            }

            if (peers.Length == 0)
            {
                _statistics.AddDropped();
                return;
            }

            var header = new byte[10];
            int headerLength = WebSocketProtocol.WriteServerFrameHeader(header, WebSocketProtocol.OpcodeBinary, length);
            bool sentToAny = false;

            for (int i = 0; i < peers.Length; i++)
            {
                ServerPeer peer = peers[i];
                if (!peer.Alive)
                {
                    continue;
                }

                try
                {
                    lock (peer.SendLock)
                    {
                        peer.Stream.Write(header, 0, headerLength);
                        peer.Stream.Write(payload, 0, length);
                        peer.Stream.Flush();
                    }

                    sentToAny = true;
                }
                catch (Exception exception)
                {
                    RaiseLogThrottled("WebSocket peer disconnected during broadcast: " + exception.Message);
                    RemovePeer(peer);
                    ClosePeer(peer);
                }
            }

            if (sentToAny)
            {
                CountFrameSent(length);
            }
            else
            {
                _statistics.AddDropped();
            }
        }

        private void SendControlFrame(ServerPeer peer, int opcode, byte[] payload, int length)
        {
            try
            {
                var header = new byte[10];
                int headerLength = WebSocketProtocol.WriteServerFrameHeader(header, opcode, length);
                lock (peer.SendLock)
                {
                    peer.Stream.Write(header, 0, headerLength);
                    if (length > 0)
                    {
                        peer.Stream.Write(payload, 0, length);
                    }

                    peer.Stream.Flush();
                }
            }
            catch
            {
                RemovePeer(peer);
            }
        }

        private bool HasPendingControl()
        {
            lock (_controlSendLock)
            {
                return _controlSendQueue.Count > 0;
            }
        }

        private void EnqueueControl(byte[] buffer, int length)
        {
            bool dropped = false;
            IncomingFrame old = default(IncomingFrame);

            lock (_controlLock)
            {
                if (_controlQueue.Count >= 32)
                {
                    old = _controlQueue.Dequeue();
                    dropped = true;
                    _statistics.AddDropped();
                }

                _controlQueue.Enqueue(new IncomingFrame { Buffer = buffer, Length = length });
            }

            if (dropped)
            {
                _receivePool.Return(old.Buffer);
            }

            _statistics.AddControlReceived();
        }

        private void EnqueueIncoming(byte[] buffer, int length)
        {
            bool dropped = false;
            IncomingFrame old = default(IncomingFrame);

            lock (_incomingLock)
            {
                if (_incomingQueue.Count >= MaxPendingIncoming)
                {
                    old = _incomingQueue.Dequeue();
                    dropped = true;
                    _statistics.AddDropped();
                }

                _incomingQueue.Enqueue(new IncomingFrame { Buffer = buffer, Length = length });
            }

            if (dropped)
            {
                _receivePool.Return(old.Buffer);
            }
        }

        private bool PathMatches(string path)
        {
            if (string.IsNullOrEmpty(_config.WebSocketPath) || _config.WebSocketPath == "*")
            {
                return true;
            }

            string expected = _config.WebSocketPath.TrimEnd('/');
            string actual = (path ?? string.Empty).TrimEnd('/');
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }

        private void RemovePeer(ServerPeer peer)
        {
            if (peer == null)
            {
                return;
            }

            peer.Alive = false;
            bool removed;
            lock (_peersLock)
            {
                removed = _peers.Remove(peer);
            }

            if (removed)
            {
                RaiseLog("WebSocket client disconnected (" + PeerCount + " remaining).");
            }
        }

        private static void ClosePeer(ServerPeer peer)
        {
            if (peer == null)
            {
                return;
            }

            peer.Alive = false;
            try
            {
                peer.Stream.Close();
            }
            catch
            {
                // Ignored.
            }

            try
            {
                peer.Client.Close();
            }
            catch
            {
                // Ignored.
            }
        }

        private void CountFrameSent(int length)
        {
            _statistics.AddFrameSent();
            _statistics.AddBytesSent(length);
        }

        private static bool ReadExact(NetworkStream stream, byte[] buffer, int offset, int count)
        {
            if (count <= 0)
            {
                return true;
            }

            int readTotal = 0;
            while (readTotal < count)
            {
                int read = stream.Read(buffer, offset + readTotal, count - readTotal);
                if (read <= 0)
                {
                    return false;
                }

                readTotal += read;
            }

            return true;
        }

        private static void Unmask(byte[] buffer, int offset, int length, byte[] mask)
        {
            for (int i = 0; i < length; i++)
            {
                buffer[offset + i] ^= mask[i & 3];
            }
        }

        private static void WriteRaw(NetworkStream stream, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static void JoinThread(Thread thread)
        {
            if (thread == null || !thread.IsAlive)
            {
                return;
            }

            try
            {
                thread.Join(500);
            }
            catch
            {
                // Ignored.
            }
        }

        private string DescribeEndpoint()
        {
            if (_serverUri == null)
            {
                return "the configured URL";
            }

            // Query string is omitted because it contains the relay token.
            return _serverUri.Scheme + "://" + _serverUri.Host + ":" + _serverUri.Port + _serverUri.AbsolutePath;
        }

        private string DescribeConnectFailure(Exception exception)
        {
            string detail = exception.Message;
            if (exception.InnerException != null)
            {
                detail += " " + exception.InnerException.Message;
            }

            string message = "WebSocket client error: " + exception.Message +
                             " (target " + DescribeEndpoint() + ").";

            if (ContainsIgnoreCase(detail, "refused") ||
                ContainsIgnoreCase(detail, "No connection could be made") ||
                ContainsIgnoreCase(detail, "unreachable"))
            {
                message += " Nothing accepted the connection. Start the relay " +
                           "(cd Tools/RelayServer && npm start) or the stream server, then check the URL host, port, and path.";
            }
            else if (ContainsIgnoreCase(detail, "resolve") ||
                     ContainsIgnoreCase(detail, "unknown host") ||
                     ContainsIgnoreCase(detail, "No such host"))
            {
                message += " The host name could not be resolved. Check the URL for typos.";
            }
            else if (ContainsIgnoreCase(detail, "SSL") ||
                     ContainsIgnoreCase(detail, "certificate") ||
                     ContainsIgnoreCase(detail, "TLS"))
            {
                message += " TLS failed. Use wss:// with a valid certificate, or ws:// only on a trusted local network.";
            }
            else
            {
                message += " Check that the relay or stream server is running and reachable from this device.";
            }

            if (_config != null && !_config.WebSocketSecure && !IsLoopbackHost(_serverUri))
            {
                message += " Plain ws:// is blocked by some browsers and Android builds; use wss:// for internet streaming.";
            }

            return message;
        }

        private static bool IsLoopbackHost(Uri uri)
        {
            if (uri == null)
            {
                return false;
            }

            return uri.IsLoopback ||
                   string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsIgnoreCase(string value, string fragment)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void RaiseLog(string message)
        {
            var handler = Log;
            if (handler != null)
            {
                handler(message);
            }
        }

        private void RaiseLogThrottled(string message)
        {
            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Read(ref _lastLogTimestamp);
            if (now - previous < Stopwatch.Frequency)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastLogTimestamp, now, previous) != previous)
            {
                return;
            }

            RaiseLog(message);
        }
    }
}
#endif
