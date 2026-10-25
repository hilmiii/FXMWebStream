using System;

namespace FXM.WebStream
{
    /// <summary>Playback state of a stream (owned by the sender, mirrored to receivers).</summary>
    public enum StreamPlaybackState : byte
    {
        /// <summary>No new frames are being sent.</summary>
        Stopped = 0,

        /// <summary>Frames are sent at the target FPS.</summary>
        Playing = 1,

        /// <summary>Streaming is paused; the last frame remains visible on the receiver.</summary>
        Paused = 2
    }

    /// <summary>Playback commands a receiver can send to a sender.</summary>
    public enum StreamCommandType : byte
    {
        None = 0,
        Play = 1,
        Pause = 2,
        Stop = 3,
        Restart = 4,
        Seek = 5,
        SetQuality = 6,
        SetTargetFps = 7
    }

    /// <summary>Control message kind.</summary>
    public enum StreamMessageKind : byte
    {
        /// <summary>Receiver -> sender: request an action.</summary>
        Command = 1,

        /// <summary>Sender -> receiver: publish the current state and position.</summary>
        State = 2
    }

    /// <summary>
    /// One fixed-size control message (28 bytes). It travels on the same channel as video
    /// but uses a different magic ("FXC1"), so it can be separated from JPEG chunks. It is
    /// small and is never split like a video frame.
    /// </summary>
    public struct StreamControlMessage
    {
        public StreamMessageKind Kind;

        /// <summary>Set when Kind = Command.</summary>
        public StreamCommandType Command;

        /// <summary>Set when Kind = State.</summary>
        public StreamPlaybackState State;

        /// <summary>
        /// Kind = State: source sender id.
        /// Kind = Command: target sender id (0 = every listening sender).
        /// </summary>
        public uint SourceId;

        /// <summary>Monotonic sequence number used to discard out-of-order messages.</summary>
        public uint Sequence;

        /// <summary>Extra value: JPEG quality, target FPS, or seek position in seconds.</summary>
        public int Value;

        /// <summary>Playback position in milliseconds (for video).</summary>
        public int PositionMs;

        /// <summary>Source duration in milliseconds (0 for a live camera or unknown duration).</summary>
        public int DurationMs;

        public static StreamControlMessage CommandMessage(StreamCommandType command, uint targetSourceId, int value = 0)
        {
            return new StreamControlMessage
            {
                Kind = StreamMessageKind.Command,
                Command = command,
                State = StreamPlaybackState.Stopped,
                SourceId = targetSourceId,
                Value = value
            };
        }

        public static StreamControlMessage StateMessage(uint sourceId, StreamPlaybackState state,
            int positionMs = 0, int durationMs = 0)
        {
            return new StreamControlMessage
            {
                Kind = StreamMessageKind.State,
                Command = StreamCommandType.None,
                State = state,
                SourceId = sourceId,
                PositionMs = positionMs,
                DurationMs = durationMs
            };
        }
    }

    /// <summary>
    /// Control wire format (little-endian):
    /// <code>
    /// offset 0  : magic 'F','X','C','1'
    /// offset 4  : version (1)
    /// offset 5  : kind (1 = command, 2 = state)
    /// offset 6  : command (see StreamCommandType)
    /// offset 7  : state (see StreamPlaybackState)
    /// offset 8  : sourceId (uint)
    /// offset 12 : sequence (uint)
    /// offset 16 : value (int)
    /// offset 20 : positionMs (int)
    /// offset 24 : durationMs (int, 0 = live / unknown)
    /// </code>
    /// There is no variable payload, so a message always fits in one UDP datagram.
    /// </summary>
    public static class StreamControlProtocol
    {
        public const int HeaderSize = 28;
        public const byte Version = 1;
        public const int MaxPositionMs = 24 * 60 * 60 * 1000;

        private const byte Magic0 = (byte)'F';
        private const byte Magic1 = (byte)'X';
        private const byte Magic2 = (byte)'C';
        private const byte Magic3 = (byte)'1';

        /// <summary>
        /// Allocation-free quick check. Transports use it to separate control messages
        /// from video frames before reassembly or decode queueing.
        /// </summary>
        public static bool IsControlPacket(byte[] buffer, int offset, int count)
        {
            if (buffer == null || count < HeaderSize || offset < 0 || offset + HeaderSize > buffer.Length)
            {
                return false;
            }

            return buffer[offset] == Magic0 &&
                   buffer[offset + 1] == Magic1 &&
                   buffer[offset + 2] == Magic2 &&
                   buffer[offset + 3] == Magic3 &&
                   buffer[offset + 4] == Version;
        }

        /// <summary>Write a message into a buffer (at least HeaderSize bytes). Returns the bytes written.</summary>
        public static int Write(byte[] destination, int offset, StreamControlMessage message)
        {
            if (destination == null || offset < 0 || offset + HeaderSize > destination.Length)
            {
                throw new ArgumentException("Control buffer is too small.", "destination");
            }

            destination[offset + 0] = Magic0;
            destination[offset + 1] = Magic1;
            destination[offset + 2] = Magic2;
            destination[offset + 3] = Magic3;
            destination[offset + 4] = Version;
            destination[offset + 5] = (byte)message.Kind;
            destination[offset + 6] = (byte)message.Command;
            destination[offset + 7] = (byte)message.State;

            WriteUInt32(destination, offset + 8, message.SourceId);
            WriteUInt32(destination, offset + 12, message.Sequence);
            WriteInt32(destination, offset + 16, message.Value);
            WriteInt32(destination, offset + 20, message.PositionMs);
            WriteInt32(destination, offset + 24, message.DurationMs);

            return HeaderSize;
        }

        /// <summary>Allocate a new buffer containing one control message (caller convenience).</summary>
        public static byte[] Create(StreamControlMessage message)
        {
            var buffer = new byte[HeaderSize];
            Write(buffer, 0, message);
            return buffer;
        }

        public static bool TryRead(byte[] buffer, int offset, int count, out StreamControlMessage message)
        {
            message = default(StreamControlMessage);

            if (!IsControlPacket(buffer, offset, count))
            {
                return false;
            }

            message.Kind = (StreamMessageKind)buffer[offset + 5];
            message.Command = (StreamCommandType)buffer[offset + 6];
            message.State = (StreamPlaybackState)buffer[offset + 7];
            message.SourceId = ReadUInt32(buffer, offset + 8);
            message.Sequence = ReadUInt32(buffer, offset + 12);
            message.Value = ReadInt32(buffer, offset + 16);
            message.PositionMs = ReadInt32(buffer, offset + 20);
            message.DurationMs = ReadInt32(buffer, offset + 24);

            if (message.Kind != StreamMessageKind.Command && message.Kind != StreamMessageKind.State)
            {
                return false;
            }

            if (message.PositionMs < 0)
            {
                message.PositionMs = 0;
            }
            else if (message.PositionMs > MaxPositionMs)
            {
                message.PositionMs = MaxPositionMs;
            }

            if (message.DurationMs < 0)
            {
                message.DurationMs = 0;
            }
            else if (message.DurationMs > MaxPositionMs)
            {
                message.DurationMs = MaxPositionMs;
            }

            return true;
        }

        /// <summary>
        /// Monotonic sequence increment. Messages older than the last accepted sequence
        /// are considered stale and ignored (UDP can reorder packets).
        /// </summary>
        public static uint NextSequence(ref uint counter)
        {
            counter = unchecked(counter + 1u);
            if (counter == 0)
            {
                counter = 1;
            }

            return counter;
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            WriteUInt32(buffer, offset, unchecked((uint)value));
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] |
                          (buffer[offset + 1] << 8) |
                          (buffer[offset + 2] << 16) |
                          (buffer[offset + 3] << 24));
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return unchecked((int)ReadUInt32(buffer, offset));
        }
    }

    /// <summary>Unique id generator for each sender/camera instance.</summary>
    public static class StreamIdentity
    {
        private static int _counter;
        private static readonly uint SessionPrefix = CreatePrefix();

        /// <summary>32-bit id that is never 0 (0 means "all sources").</summary>
        public static uint Next()
        {
            uint id = unchecked(SessionPrefix + (uint)System.Threading.Interlocked.Increment(ref _counter));
            return id == 0 ? 1u : id;
        }

        private static uint CreatePrefix()
        {
            // Time plus a process hash is enough to distinguish multiple senders on the
            // same machine and across machines within a demo session.
            long ticks = DateTime.UtcNow.Ticks;
            int process = 0;
            try
            {
                process = System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch
            {
                // Some platforms (for example WebGL) do not allow process access.
            }

            uint hash = unchecked((uint)ticks ^ (uint)(ticks >> 32) ^ (uint)process * 2654435761u);
            return hash == 0 ? 0x9E3779B9u : hash;
        }
    }
}
