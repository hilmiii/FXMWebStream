using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FXM.WebStream
{
    /// <summary>UDP packet header (24 bytes, little-endian).</summary>
    public struct UdpFrameHeader
    {
        public byte Version;
        public byte Flags;
        public ushort HeaderSize;
        public uint FrameId;
        public int TotalLength;
        public int ChunkIndex;
        public int ChunkCount;
        public int PayloadLength;
        public int ChunkPayloadSize;
    }

    /// <summary>Successfully reassembled frame; the caller owns the buffer.</summary>
    public struct AssembledFrame
    {
        public byte[] Buffer;
        public int Length;
        public uint FrameId;
        public int ChunkCount;
    }

    /// <summary>
    /// UDP wire format:
    /// <code>
    /// offset 0  : magic 'F','X','S','1'
    /// offset 4  : version (1)
    /// offset 5  : flags (reserved)
    /// offset 6  : headerSize (ushort, = 24)
    /// offset 8  : frameId (uint, monotonically increasing)
    /// offset 12 : totalLength (uint, full JPEG length)
    /// offset 16 : chunkIndex (ushort)
    /// offset 18 : chunkCount (ushort)
    /// offset 20 : payloadLength (ushort, valid bytes in this packet)
    /// offset 22 : chunkPayloadSize (ushort, payload capacity per chunk)
    /// offset 24 : payload...
    /// </code>
    /// The header carries chunkPayloadSize so receivers do not need to guess the sender MTU.
    /// </summary>
    public static class UdpFrameProtocol
    {
        public const int HeaderSize = 24;
        public const byte Version = 1;
        public const int MaxFrameBytes = 16 * 1024 * 1024;
        public const int MaxChunksPerFrame = 8192;
        public const int MaxChunkPayloadSize = 65000;

        private const byte Magic0 = (byte)'F';
        private const byte Magic1 = (byte)'X';
        private const byte Magic2 = (byte)'S';
        private const byte Magic3 = (byte)'1';

        public static void WriteHeader(byte[] destination, int offset, uint frameId, int totalLength,
            int chunkIndex, int chunkCount, int payloadLength, int chunkPayloadSize)
        {
            destination[offset + 0] = Magic0;
            destination[offset + 1] = Magic1;
            destination[offset + 2] = Magic2;
            destination[offset + 3] = Magic3;
            destination[offset + 4] = Version;
            destination[offset + 5] = 0;
            WriteUInt16(destination, offset + 6, HeaderSize);
            WriteUInt32(destination, offset + 8, frameId);
            WriteUInt32(destination, offset + 12, (uint)totalLength);
            WriteUInt16(destination, offset + 16, chunkIndex);
            WriteUInt16(destination, offset + 18, chunkCount);
            WriteUInt16(destination, offset + 20, payloadLength);
            WriteUInt16(destination, offset + 22, chunkPayloadSize);
        }

        public static bool TryReadHeader(byte[] packet, int length, out UdpFrameHeader header)
        {
            header = default(UdpFrameHeader);

            if (packet == null || length < HeaderSize)
            {
                return false;
            }

            if (packet[0] != Magic0 || packet[1] != Magic1 || packet[2] != Magic2 || packet[3] != Magic3)
            {
                return false;
            }

            if (packet[4] != Version)
            {
                return false;
            }

            header.Version = packet[4];
            header.Flags = packet[5];
            header.HeaderSize = (ushort)ReadUInt16(packet, 6);
            header.FrameId = ReadUInt32(packet, 8);
            header.TotalLength = (int)ReadUInt32(packet, 12);
            header.ChunkIndex = ReadUInt16(packet, 16);
            header.ChunkCount = ReadUInt16(packet, 18);
            header.PayloadLength = ReadUInt16(packet, 20);
            header.ChunkPayloadSize = ReadUInt16(packet, 22);

            if (header.HeaderSize != HeaderSize)
            {
                return false;
            }

            if (header.TotalLength <= 0 || header.TotalLength > MaxFrameBytes)
            {
                return false;
            }

            if (header.ChunkPayloadSize <= 0 || header.ChunkPayloadSize > MaxChunkPayloadSize)
            {
                return false;
            }

            if (header.ChunkCount <= 0 || header.ChunkCount > MaxChunksPerFrame)
            {
                return false;
            }

            if (header.ChunkIndex >= header.ChunkCount)
            {
                return false;
            }

            if (header.PayloadLength <= 0 || header.PayloadLength > header.ChunkPayloadSize)
            {
                return false;
            }

            if (HeaderSize + header.PayloadLength > length)
            {
                return false;
            }

            long offset = (long)header.ChunkIndex * header.ChunkPayloadSize;
            if (offset + header.PayloadLength > header.TotalLength)
            {
                return false;
            }

            return true;
        }

        internal static void WriteUInt16(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }

        internal static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        internal static int ReadUInt16(byte[] buffer, int offset)
        {
            return buffer[offset] | (buffer[offset + 1] << 8);
        }

        internal static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
        }
    }

    /// <summary>Splits a JPEG frame into chunks that fit the MTU.</summary>
    public static class UdpFrameChunker
    {
        public static int GetChunkCount(int totalLength, int chunkPayloadSize)
        {
            if (totalLength <= 0 || chunkPayloadSize <= 0)
            {
                return 0;
            }

            return (totalLength + chunkPayloadSize - 1) / chunkPayloadSize;
        }

        /// <summary>
        /// Write one chunk into <paramref name="datagram"/> (capacity at least
        /// <see cref="UdpFrameProtocol.HeaderSize"/> + chunkPayloadSize) and return the datagram length.
        /// </summary>
        public static int WriteChunk(byte[] source, int totalLength, uint frameId, int chunkIndex,
            int chunkCount, int chunkPayloadSize, byte[] datagram)
        {
            int offset = chunkIndex * chunkPayloadSize;
            int payloadLength = Math.Min(chunkPayloadSize, totalLength - offset);

            UdpFrameProtocol.WriteHeader(datagram, 0, frameId, totalLength, chunkIndex, chunkCount, payloadLength, chunkPayloadSize);
            Buffer.BlockCopy(source, offset, datagram, UdpFrameProtocol.HeaderSize, payloadLength);

            return UdpFrameProtocol.HeaderSize + payloadLength;
        }
    }

    /// <summary>
    /// Reassembles UDP chunks into complete frames. Handles lost packets (frame discarded),
    /// duplicates, out-of-order delivery, stale frames, and memory limits. Not thread-safe:
    /// use it from a single receive thread (as UDPTransport does).
    /// </summary>
    public sealed class UdpFrameAssembler
    {
        private sealed class PendingFrame
        {
            public uint FrameId;
            public int TotalLength;
            public int ChunkCount;
            public int ChunkPayloadSize;
            public byte[] Buffer;
            public byte[] Received;
            public int ReceivedCount;
            public long LastPacketTimestamp;
        }

        private readonly FrameBufferPool _pool;
        private readonly Dictionary<uint, PendingFrame> _pending = new Dictionary<uint, PendingFrame>();
        private readonly Stack<PendingFrame> _pendingCache = new Stack<PendingFrame>();
        private readonly int _maxPendingFrames;
        private readonly long _timeoutTicks;

        private uint _latestCompletedFrameId;
        private bool _hasCompletedFrame;
        private long _droppedIncomplete;
        private long _malformed;

        public UdpFrameAssembler(FrameBufferPool pool, int maxPendingFrames = 4, float timeoutSeconds = 2f)
        {
            _pool = pool ?? new FrameBufferPool(8);
            _maxPendingFrames = Math.Max(1, maxPendingFrames);
            _timeoutTicks = (long)(Math.Max(0.1f, timeoutSeconds) * Stopwatch.Frequency);
        }

        public long DroppedIncomplete { get { return _droppedIncomplete; } }
        public long MalformedPackets { get { return _malformed; } }
        public int PendingFrames { get { return _pending.Count; } }

        /// <summary>
        /// Process one datagram. When it completes a frame, returns true and fills
        /// <paramref name="completed"/> with a buffer that must return to the pool after use.
        /// </summary>
        public bool TryAddPacket(byte[] packet, int length, out AssembledFrame completed)
        {
            completed = default(AssembledFrame);

            UdpFrameHeader header;
            if (!UdpFrameProtocol.TryReadHeader(packet, length, out header))
            {
                _malformed++;
                return false;
            }

            // Already-completed or older-than-latest frame: ignore it (for example a late packet).
            if (_hasCompletedFrame && !IsNewer(header.FrameId, _latestCompletedFrameId))
            {
                return false;
            }

            long now = Stopwatch.GetTimestamp();
            PendingFrame pending;
            if (!_pending.TryGetValue(header.FrameId, out pending))
            {
                PruneExpired(now);

                if (_pending.Count >= _maxPendingFrames)
                {
                    DropOldestPending();
                }

                pending = _pendingCache.Count > 0 ? _pendingCache.Pop() : new PendingFrame();
                pending.FrameId = header.FrameId;
                pending.TotalLength = header.TotalLength;
                pending.ChunkCount = header.ChunkCount;
                pending.ChunkPayloadSize = header.ChunkPayloadSize;
                pending.Buffer = _pool.Rent(header.TotalLength);
                pending.Received = new byte[header.ChunkCount];
                pending.ReceivedCount = 0;
                pending.LastPacketTimestamp = now;
                _pending.Add(header.FrameId, pending);
            }
            else if (pending.TotalLength != header.TotalLength ||
                     pending.ChunkCount != header.ChunkCount ||
                     pending.ChunkPayloadSize != header.ChunkPayloadSize)
            {
                // Inconsistent metadata for the same frameId: ignore this malformed packet.
                _malformed++;
                return false;
            }

            pending.LastPacketTimestamp = now;

            if (pending.Received[header.ChunkIndex] != 0)
            {
                return false; // duplicate
            }

            pending.Received[header.ChunkIndex] = 1;
            pending.ReceivedCount++;

            int offset = header.ChunkIndex * header.ChunkPayloadSize;
            Buffer.BlockCopy(packet, UdpFrameProtocol.HeaderSize, pending.Buffer, offset, header.PayloadLength);

            if (pending.ReceivedCount < pending.ChunkCount)
            {
                return false;
            }

            // Lengkap.
            _pending.Remove(header.FrameId);
            _latestCompletedFrameId = header.FrameId;
            _hasCompletedFrame = true;

            completed.Buffer = pending.Buffer;
            completed.Length = header.TotalLength;
            completed.FrameId = header.FrameId;
            completed.ChunkCount = header.ChunkCount;

            pending.Buffer = null;
            pending.Received = null;
            _pendingCache.Push(pending);

            // Drop older incomplete frames (they can no longer complete).
            DropFramesOlderThan(_latestCompletedFrameId);

            return true;
        }

        /// <summary>Discard incomplete frames after the timeout (call periodically).</summary>
        public void PruneExpired()
        {
            PruneExpired(Stopwatch.GetTimestamp());
        }

        /// <summary>Return every buffer to the pool and reset state.</summary>
        public void Reset()
        {
            foreach (var pair in _pending)
            {
                var pending = pair.Value;
                if (pending.Buffer != null)
                {
                    _pool.Return(pending.Buffer);
                    pending.Buffer = null;
                }

                pending.Received = null;
                _pendingCache.Push(pending);
            }

            _pending.Clear();
            _hasCompletedFrame = false;
            _latestCompletedFrameId = 0;
        }

        private void PruneExpired(long now)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            List<uint> expired = null;
            foreach (var pair in _pending)
            {
                if (now - pair.Value.LastPacketTimestamp > _timeoutTicks)
                {
                    if (expired == null)
                    {
                        expired = new List<uint>(2);
                    }

                    expired.Add(pair.Key);
                }
            }

            if (expired == null)
            {
                return;
            }

            for (int i = 0; i < expired.Count; i++)
            {
                RemovePending(expired[i]);
            }
        }

        private void DropOldestPending()
        {
            uint oldestId = 0;
            long oldestTimestamp = long.MaxValue;

            foreach (var pair in _pending)
            {
                if (pair.Value.LastPacketTimestamp < oldestTimestamp)
                {
                    oldestTimestamp = pair.Value.LastPacketTimestamp;
                    oldestId = pair.Key;
                }
            }

            if (oldestTimestamp != long.MaxValue)
            {
                RemovePending(oldestId);
            }
        }

        private void DropFramesOlderThan(uint frameId)
        {
            List<uint> stale = null;
            foreach (var pair in _pending)
            {
                if (!IsNewer(pair.Key, frameId))
                {
                    if (stale == null)
                    {
                        stale = new List<uint>(2);
                    }

                    stale.Add(pair.Key);
                }
            }

            if (stale == null)
            {
                return;
            }

            for (int i = 0; i < stale.Count; i++)
            {
                RemovePending(stale[i]);
            }
        }

        private void RemovePending(uint frameId)
        {
            PendingFrame pending;
            if (!_pending.TryGetValue(frameId, out pending))
            {
                return;
            }

            _pending.Remove(frameId);
            _droppedIncomplete++;

            if (pending.Buffer != null)
            {
                _pool.Return(pending.Buffer);
                pending.Buffer = null;
            }

            pending.Received = null;
            _pendingCache.Push(pending);
        }

        private static bool IsNewer(uint candidate, uint reference)
        {
            return unchecked((int)(candidate - reference)) > 0;
        }
    }
}
