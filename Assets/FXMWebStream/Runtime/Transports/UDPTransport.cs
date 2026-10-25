#if UNITY_WEBGL && !UNITY_EDITOR
// WebGL does not support System.Net.Sockets (UDP). The stub below keeps the project
// buildable for WebGL; use WebSocketTransport or the relay on that platform.
using System;

namespace FXM.WebStream
{
    public sealed class UDPTransport : INetworkTransport
    {
        public bool IsRunning { get { return false; } }
        public int PeerCount { get { return 0; } }
        public TransportStatistics Statistics { get; private set; }

        public UDPTransport()
        {
            Statistics = new TransportStatistics();
        }

#pragma warning disable 0067 // The WebGL stub never raises these events.
        public event Action<ArraySegment<byte>> FrameReceived;
        public event Action<ArraySegment<byte>> ControlReceived;
#pragma warning restore 0067
        public event Action<string> Log;

        public bool Start(TransportConfig config)
        {
            RaiseLog("UDP is unavailable on WebGL. Use TransportKind.WebSocket or Relay.");
            return false;
        }

        public void Send(ArraySegment<byte> frame)
        {
            // Nothing can be sent on WebGL.
        }

        public void SendControl(ArraySegment<byte> message)
        {
            // Nothing can be sent on WebGL.
        }

        public void Poll()
        {
        }

        public void Stop()
        {
        }

        public bool SetTargets(string[] targets)
        {
            return false;
        }

        public bool AddTarget(string hostAndPort)
        {
            return false;
        }

        public bool RemoveTarget(string hostAndPort)
        {
            return false;
        }

        public string[] GetTargets()
        {
            return new string[0];
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
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FXM.WebStream
{
    /// <summary>
    /// UDP transport for LAN, desktop, and mobile. Frames larger than the MTU are split
    /// into datagrams and reassembled on the receiver.
    ///
    /// Thread architecture:
    /// - Main thread: Send() only copies the payload into a queue (fast, no I/O).
    /// - Send thread: splits frames into chunks and calls SendTo (never blocks the game).
    /// - Receive thread: ReceiveFrom plus reassembly (all heavy work off the main thread).
    /// - Poll(): moves complete frames to the main thread for decoding.
    /// </summary>
    public sealed class UDPTransport : INetworkTransport
    {
        private const int MaxDatagramSize = 65535;

        private struct OutgoingFrame
        {
            public byte[] Buffer;
            public int Length;
        }

        private readonly TransportStatistics _statistics = new TransportStatistics();
        private readonly FrameBufferPool _sendPool = new FrameBufferPool(4);
        private readonly FrameBufferPool _receivePool = new FrameBufferPool(8);
        private readonly Queue<OutgoingFrame> _sendQueue = new Queue<OutgoingFrame>();
        private readonly Queue<AssembledFrame> _receivedQueue = new Queue<AssembledFrame>();
        private readonly object _sendLock = new object();
        private readonly object _receiveLock = new object();

        private Socket _socket;
        private IPEndPoint _remoteEndPoint;
        private UdpFrameAssembler _assembler;

        // Fan-out target list. One entry = one receiver; used for one-to-many.
        // With ReceiveOnly, this list holds the sender address for playback commands.
        private readonly List<IPEndPoint> _targets = new List<IPEndPoint>();
        private readonly object _targetsLock = new object();
        private readonly object _controlLock = new object();
        private readonly object _socketSendLock = new object();
        private readonly Queue<byte[]> _controlQueue = new Queue<byte[]>();
        private readonly FrameBufferPool _controlPool = new FrameBufferPool(8);
        private Thread _sendThread;
        private Thread _receiveThread;
        private byte[] _datagram;
        private volatile bool _running;
        private int _chunkPayloadSize;
        private int _sendQueueCapacity = 4;
        private int _maxFramesPerPoll = 8;
        private long _frameId;
        private long _lastLogTimestamp;

        public UDPTransport()
        {
        }

        public bool IsRunning { get { return _running; } }

        public int PeerCount
        {
            get
            {
                if (!_running)
                {
                    return 0;
                }

                lock (_targetsLock)
                {
                    return _targets.Count;
                }
            }
        }

        public TransportStatistics Statistics { get { return _statistics; } }

        /// <summary>Number of active send targets (receivers).</summary>
        public int TargetCount { get { return PeerCount; } }

        /// <summary>Packets waiting to be sent (backpressure indicator).</summary>
        public int PendingSendFrames
        {
            get
            {
                lock (_sendLock)
                {
                    return _sendQueue.Count;
                }
            }
        }

        public event Action<ArraySegment<byte>> FrameReceived;
        public event Action<ArraySegment<byte>> ControlReceived;
        public event Action<string> Log;

        public bool Start(TransportConfig config)
        {
            if (config == null)
            {
                config = new TransportConfig();
            }

            Stop();

            int maxMtu = Math.Min(MaxDatagramSize, UdpFrameProtocol.HeaderSize + UdpFrameProtocol.MaxChunkPayloadSize);
            int mtu = Math.Max(UdpFrameProtocol.HeaderSize + 64, Math.Min(maxMtu, config.Mtu));
            _chunkPayloadSize = mtu - UdpFrameProtocol.HeaderSize;
            _sendQueueCapacity = Math.Max(1, config.SendQueueCapacity);
            _datagram = new byte[mtu];

            try
            {
                IPAddress address;
                if (!TryResolve(config.RemoteHost, out address))
                {
                    RaiseLog("UDP: could not resolve host '" + config.RemoteHost + "'.");
                    return false;
                }

                _remoteEndPoint = new IPEndPoint(address, config.RemotePort);
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                TrySetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 4 * 1024 * 1024);
                TrySetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, 1024 * 1024);
                TrySetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);

                bool needsReceive = config.Role != TransportRole.SendOnly;
                bool needsBind = needsReceive || config.LocalPort > 0;

                if (needsBind)
                {
                    int localPort = Math.Max(0, config.LocalPort);
                    _socket.Bind(new IPEndPoint(IPAddress.Any, localPort));
                }

                lock (_targetsLock)
                {
                    _targets.Clear();
                    _targets.Add(_remoteEndPoint);
                }

                _assembler = new UdpFrameAssembler(_receivePool, config.MaxPendingFrames, config.ReassemblyTimeoutSeconds);
                _statistics.Reset();
                _running = true;

                _sendThread = new Thread(SendLoop)
                {
                    IsBackground = true,
                    Name = "FXM.UdpSend"
                };
                _sendThread.Start();

                if (needsReceive)
                {
                    _receiveThread = new Thread(ReceiveLoop)
                    {
                        IsBackground = true,
                        Name = "FXM.UdpReceive"
                    };
                    _receiveThread.Start();
                }

                RaiseLog("UDP ready: " + _remoteEndPoint + " (MTU " + mtu + ", payload " + _chunkPayloadSize +
                         " byte/chunk, target " + PeerCount + ").");
                return true;
            }
            catch (Exception exception)
            {
                RaiseLog("UDP failed to start: " + exception.Message);
                Stop();
                return false;
            }
        }

        public void Send(ArraySegment<byte> frame)
        {
            if (!_running || frame.Count <= 0 || frame.Array == null)
            {
                return;
            }

            // Copy now so the caller can immediately reuse its buffer.
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
        /// Send a small control message immediately to every target (bypassing the frame queue).
        /// Called from the main thread; safe alongside the frame send thread.
        /// </summary>
        public void SendControl(ArraySegment<byte> message)
        {
            if (!_running || _socket == null || message.Array == null || message.Count <= 0)
            {
                return;
            }

            IPEndPoint[] targets = SnapshotTargets();
            if (targets.Length == 0)
            {
                return;
            }

            for (int i = 0; i < targets.Length; i++)
            {
                try
                {
                    lock (_socketSendLock)
                    {
                        _socket.SendTo(message.Array, message.Offset, message.Count, SocketFlags.None, targets[i]);
                    }

                    _statistics.AddControlSent();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException exception)
                {
                    RaiseLogThrottled("UDP control send failed: " + exception.SocketErrorCode + ".");
                }
                catch (Exception exception)
                {
                    RaiseLogThrottled("UDP control send failed: " + exception.Message);
                }
            }
        }

        /// <summary>Replace the whole fan-out target list, for example ["192.168.1.10:47777", "192.168.1.11:47777"].</summary>
        public bool SetTargets(string[] endpoints)
        {
            if (endpoints == null || endpoints.Length == 0)
            {
                return false;
            }

            var resolved = new List<IPEndPoint>(endpoints.Length);
            for (int i = 0; i < endpoints.Length; i++)
            {
                IPEndPoint endpoint;
                if (TryResolveEndpoint(endpoints[i], out endpoint))
                {
                    AddUnique(resolved, endpoint);
                }
            }

            if (resolved.Count == 0)
            {
                return false;
            }

            lock (_targetsLock)
            {
                _targets.Clear();
                _targets.AddRange(resolved);
            }

            RaiseLog("UDP target diperbarui: " + resolved.Count + " tujuan.");
            return true;
        }

        /// <summary>Add one fan-out target (one-to-many).</summary>
        public bool AddTarget(string hostAndPort)
        {
            IPEndPoint endpoint;
            if (!TryResolveEndpoint(hostAndPort, out endpoint))
            {
                return false;
            }

            lock (_targetsLock)
            {
                if (ContainsTarget(endpoint))
                {
                    return true;
                }

                _targets.Add(endpoint);
            }

            RaiseLog("UDP target ditambah: " + endpoint + " (total " + PeerCount + ").");
            return true;
        }

        /// <summary>Remove one fan-out target.</summary>
        public bool RemoveTarget(string hostAndPort)
        {
            IPEndPoint endpoint;
            if (!TryResolveEndpoint(hostAndPort, out endpoint))
            {
                return false;
            }

            lock (_targetsLock)
            {
                for (int i = _targets.Count - 1; i >= 0; i--)
                {
                    if (_targets[i].Equals(endpoint))
                    {
                        _targets.RemoveAt(i);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Copy of the target list in "host:port" format.</summary>
        public string[] GetTargets()
        {
            lock (_targetsLock)
            {
                var result = new string[_targets.Count];
                for (int i = 0; i < _targets.Count; i++)
                {
                    result[i] = _targets[i].Address + ":" + _targets[i].Port;
                }

                return result;
            }
        }

        public void Poll()
        {
            // Control first: play/pause commands must not wait behind queued frames.
            for (int i = 0; i < 8; i++)
            {
                byte[] control;
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
                        controlHandler(new ArraySegment<byte>(control));
                    }
                }
                catch (Exception exception)
                {
                    RaiseLog("UDP: handler ControlReceived melempar exception: " + exception.Message);
                }
                finally
                {
                    _controlPool.Return(control);
                }
            }

            for (int i = 0; i < _maxFramesPerPoll; i++)
            {
                AssembledFrame frame;
                lock (_receiveLock)
                {
                    if (_receivedQueue.Count == 0)
                    {
                        break;
                    }

                    frame = _receivedQueue.Dequeue();
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
                    RaiseLog("UDP: handler FrameReceived melempar exception: " + exception.Message);
                }
                finally
                {
                    _receivePool.Return(frame.Buffer);
                }
            }
        }

        public void Stop()
        {
            if (!_running && _socket == null)
            {
                return;
            }

            _running = false;

            lock (_sendLock)
            {
                Monitor.PulseAll(_sendLock);
            }

            try
            {
                lock (_socketSendLock)
                {
                    if (_socket != null)
                    {
                        _socket.Close();
                    }
                }
            }
            catch
            {
                // Ignored: the socket is closing.
            }

            JoinThread(_sendThread);
            JoinThread(_receiveThread);
            _sendThread = null;
            _receiveThread = null;

            lock (_sendLock)
            {
                while (_sendQueue.Count > 0)
                {
                    _sendPool.Return(_sendQueue.Dequeue().Buffer);
                }
            }

            lock (_receiveLock)
            {
                while (_receivedQueue.Count > 0)
                {
                    _receivePool.Return(_receivedQueue.Dequeue().Buffer);
                }
            }

            lock (_controlLock)
            {
                while (_controlQueue.Count > 0)
                {
                    _controlPool.Return(_controlQueue.Dequeue());
                }
            }

            if (_assembler != null)
            {
                _assembler.Reset();
                _assembler = null;
            }

            if (_socket != null)
            {
                _socket.Dispose();
                _socket = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void SendLoop()
        {
            Socket socket = _socket;

            while (_running)
            {
                OutgoingFrame frame;

                lock (_sendLock)
                {
                    while (_sendQueue.Count == 0 && _running)
                    {
                        Monitor.Wait(_sendLock, 250);
                    }

                    if (!_running)
                    {
                        return;
                    }

                    if (_sendQueue.Count == 0)
                    {
                        continue;
                    }

                    frame = _sendQueue.Dequeue();
                }

                try
                {
                    IPEndPoint[] targets = SnapshotTargets();
                    uint frameId = unchecked((uint)Interlocked.Increment(ref _frameId));
                    int chunkCount = UdpFrameChunker.GetChunkCount(frame.Length, _chunkPayloadSize);

                    if (targets.Length == 0 || chunkCount <= 0 || chunkCount > UdpFrameProtocol.MaxChunksPerFrame)
                    {
                        _statistics.AddDropped();
                        continue;
                    }

                    bool sentToAny = false;

                    for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
                    {
                        try
                        {
                            for (int chunkIndex = 0; chunkIndex < chunkCount && _running; chunkIndex++)
                            {
                                int datagramLength = UdpFrameChunker.WriteChunk(
                                    frame.Buffer, frame.Length, frameId, chunkIndex, chunkCount, _chunkPayloadSize, _datagram);

                                lock (_socketSendLock)
                                {
                                    socket.SendTo(_datagram, datagramLength, SocketFlags.None, targets[targetIndex]);
                                }

                                _statistics.AddPacketSent();
                            }

                            sentToAny = true;
                        }
                        catch (ObjectDisposedException)
                        {
                            return;
                        }
                        catch (Exception exception)
                        {
                            RaiseLogThrottled("UDP send to " + targets[targetIndex] + " failed: " + exception.Message);
                        }
                    }

                    if (sentToAny)
                    {
                        _statistics.AddFrameSent();
                        _statistics.AddBytesSent(
                            (frame.Length + (long)chunkCount * UdpFrameProtocol.HeaderSize) * targets.Length);
                    }
                    else
                    {
                        _statistics.AddDropped();
                    }
                }
                catch (SocketException exception)
                {
                    RaiseLogThrottled("UDP send failed: " + exception.SocketErrorCode + " (" + exception.Message + ").");
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    RaiseLogThrottled("UDP send failed: " + exception.Message);
                }
                finally
                {
                    _sendPool.Return(frame.Buffer);
                }
            }
        }

        private void ReceiveLoop()
        {
            var buffer = new byte[MaxDatagramSize];
            var remote = new IPEndPoint(IPAddress.Any, 0) as EndPoint;
            Socket socket = _socket;
            UdpFrameAssembler assembler = _assembler;

            while (_running)
            {
                int length;

                try
                {
                    length = socket.ReceiveFrom(buffer, ref remote);
                }
                catch (SocketException exception)
                {
                    if (!_running)
                    {
                        return;
                    }

                    RaiseLogThrottled("UDP receive failed: " + exception.SocketErrorCode + " (" + exception.Message + ").");
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

                    RaiseLogThrottled("UDP receive failed: " + exception.Message);
                    continue;
                }

                if (length <= 0)
                {
                    continue;
                }

                _statistics.AddPacketReceived();
                _statistics.AddBytesReceived(length);

                // Control messages use a different magic; separate them before reassembly.
                if (StreamControlProtocol.IsControlPacket(buffer, 0, length))
                {
                    if (length == StreamControlProtocol.HeaderSize)
                    {
                        byte[] control = _controlPool.Rent(StreamControlProtocol.HeaderSize);
                        Buffer.BlockCopy(buffer, 0, control, 0, StreamControlProtocol.HeaderSize);

                        lock (_controlLock)
                        {
                            if (_controlQueue.Count >= 32)
                            {
                                _controlPool.Return(_controlQueue.Dequeue());
                                _statistics.AddDropped();
                            }

                            _controlQueue.Enqueue(control);
                        }

                        _statistics.AddControlReceived();
                    }

                    continue;
                }

                AssembledFrame completed;
                if (!assembler.TryAddPacket(buffer, length, out completed))
                {
                    continue;
                }

                _statistics.AddFrameReceived();

                lock (_receiveLock)
                {
                    if (_receivedQueue.Count >= 8)
                    {
                        // Receiver is too slow: drop the oldest frame to stay close to real time.
                        var old = _receivedQueue.Dequeue();
                        _receivePool.Return(old.Buffer);
                        _statistics.AddDropped();
                    }

                    _receivedQueue.Enqueue(completed);
                }
            }
        }

        private IPEndPoint[] SnapshotTargets()
        {
            lock (_targetsLock)
            {
                return _targets.ToArray();
            }
        }

        private bool TryResolveEndpoint(string hostAndPort, out IPEndPoint endpoint)
        {
            endpoint = null;

            string host;
            int port;
            if (!TryParseEndpoint(hostAndPort, out host, out port))
            {
                RaiseLog("UDP: invalid target format '" + hostAndPort + "'. Use host:port.");
                return false;
            }

            IPAddress address;
            if (!TryResolve(host, out address))
            {
                return false;
            }

            endpoint = new IPEndPoint(address, port);
            return true;
        }

        private static bool TryParseEndpoint(string value, out string host, out int port)
        {
            host = null;
            port = 0;

            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            int separator = trimmed.LastIndexOf(':');
            if (separator <= 0 || separator >= trimmed.Length - 1)
            {
                return false;
            }

            host = trimmed.Substring(0, separator).Trim();
            if (host.Length > 1 && host[0] == '[' && host[host.Length - 1] == ']')
            {
                host = host.Substring(1, host.Length - 2);
            }

            if (host.Length == 0 || !int.TryParse(trimmed.Substring(separator + 1), out port))
            {
                return false;
            }

            return port > 0 && port <= 65535;
        }

        private static void AddUnique(List<IPEndPoint> list, IPEndPoint endpoint)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Equals(endpoint))
                {
                    return;
                }
            }

            list.Add(endpoint);
        }

        private bool ContainsTarget(IPEndPoint endpoint)
        {
            for (int i = 0; i < _targets.Count; i++)
            {
                if (_targets[i].Equals(endpoint))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryResolve(string host, out IPAddress address)
        {
            address = null;
            if (string.IsNullOrEmpty(host) || host == "0.0.0.0")
            {
                address = IPAddress.Loopback;
                return true;
            }

            IPAddress parsed;
            if (IPAddress.TryParse(host, out parsed))
            {
                address = parsed;
                return true;
            }

            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(host);
                for (int i = 0; i < addresses.Length; i++)
                {
                    if (addresses[i].AddressFamily == AddressFamily.InterNetwork)
                    {
                        address = addresses[i];
                        return true;
                    }
                }

                if (addresses.Length > 0)
                {
                    address = addresses[0];
                    return true;
                }
            }
            catch (Exception exception)
            {
                RaiseLog("UDP: DNS lookup failed for '" + host + "': " + exception.Message);
            }

            return false;
        }

        private void TrySetSocketOption(SocketOptionLevel level, SocketOptionName name, int value)
        {
            try
            {
                _socket.SetSocketOption(level, name, value);
            }
            catch
            {
                // Some platforms reject buffer options; ignore the failure.
            }
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
                // Ignored: the thread exits after the socket closes.
            }
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
                return; // at most one log per second so the console is not flooded during outages
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
