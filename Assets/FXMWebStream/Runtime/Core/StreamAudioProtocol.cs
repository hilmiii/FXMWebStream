using System;

namespace FXM.WebStream
{
    /// <summary>One PCM16 audio packet decoded from a transport frame.</summary>
    public struct StreamAudioFrame
    {
        /// <summary>Monotonically increasing packet number from the sender.</summary>
        public uint Sequence;

        /// <summary>Sender audio clock in milliseconds (from the first packet).</summary>
        public uint TimestampMs;

        /// <summary>Samples per second, for example 24000.</summary>
        public int SampleRate;

        /// <summary>1 = mono, 2 = stereo. Samples are interleaved.</summary>
        public int Channels;

        /// <summary>Little-endian signed 16-bit PCM, interleaved.</summary>
        public ArraySegment<byte> Pcm;

        public float DurationSeconds
        {
            get
            {
                int frameBytes = Math.Max(1, Channels * 2);
                int sampleFrames = Pcm.Count / frameBytes;
                return SampleRate > 0 ? sampleFrames / (float)SampleRate : 0f;
            }
        }
    }

    /// <summary>
    /// Wire format for streamed audio:
    /// <code>
    /// offset 0  : magic 'F','X','A','1'
    /// offset 4  : version (1)
    /// offset 5  : flags (reserved, 0)
    /// offset 6  : headerSize (ushort = 20)
    /// offset 8  : sequence (uint)
    /// offset 12 : timestampMs (uint)
    /// offset 16 : sampleRate (ushort)
    /// offset 18 : channels (byte, 1 or 2)
    /// offset 19 : bitsPerSample (byte = 16)
    /// offset 20 : interleaved PCM16 payload
    /// </code>
    /// Audio uses the normal frame path, so it works over UDP, WebSocket, and the relay
    /// without changing any transport. Receivers separate it from JPEG by the magic bytes.
    /// </summary>
    public static class StreamAudioProtocol
    {
        public const int HeaderSize = 20;
        public const byte Version = 1;
        public const int BitsPerSample = 16;
        public const int MinSampleRate = 8000;
        public const int MaxSampleRate = 48000;
        public const int MaxChannels = 2;
        public const int MaxPayloadBytes = 32 * 1024;

        private const byte Magic0 = (byte)'F';
        private const byte Magic1 = (byte)'X';
        private const byte Magic2 = (byte)'A';
        private const byte Magic3 = (byte)'1';

        public static void WriteHeader(byte[] destination, int offset, uint sequence, uint timestampMs,
            int sampleRate, int channels, int payloadBytes)
        {
            if (destination == null)
            {
                throw new ArgumentNullException("destination");
            }

            if (destination.Length - offset < HeaderSize)
            {
                throw new ArgumentException("Destination buffer is too small for an audio header.", "destination");
            }

            destination[offset + 0] = Magic0;
            destination[offset + 1] = Magic1;
            destination[offset + 2] = Magic2;
            destination[offset + 3] = Magic3;
            destination[offset + 4] = Version;
            destination[offset + 5] = 0;
            WriteUInt16(destination, offset + 6, HeaderSize);
            WriteUInt32(destination, offset + 8, sequence);
            WriteUInt32(destination, offset + 12, timestampMs);
            WriteUInt16(destination, offset + 16, sampleRate);
            destination[offset + 18] = (byte)channels;
            destination[offset + 19] = BitsPerSample;
        }

        public static bool IsAudioPacket(byte[] packet, int offset, int count)
        {
            return packet != null && count >= HeaderSize && offset >= 0 &&
                   offset + HeaderSize <= packet.Length &&
                   packet[offset + 0] == Magic0 && packet[offset + 1] == Magic1 &&
                   packet[offset + 2] == Magic2 && packet[offset + 3] == Magic3;
        }

        public static bool TryRead(byte[] packet, int offset, int count, out StreamAudioFrame frame)
        {
            frame = default(StreamAudioFrame);

            if (!IsAudioPacket(packet, offset, count))
            {
                return false;
            }

            if (packet[offset + 4] != Version)
            {
                return false;
            }

            int headerSize = ReadUInt16(packet, offset + 6);
            if (headerSize != HeaderSize)
            {
                return false;
            }

            int sampleRate = ReadUInt16(packet, offset + 16);
            int channels = packet[offset + 18];
            int bits = packet[offset + 19];

            if (sampleRate < MinSampleRate || sampleRate > MaxSampleRate)
            {
                return false;
            }

            if (channels < 1 || channels > MaxChannels)
            {
                return false;
            }

            if (bits != BitsPerSample)
            {
                return false;
            }

            int payloadBytes = count - HeaderSize;
            if (payloadBytes <= 0 || payloadBytes > MaxPayloadBytes)
            {
                return false;
            }

            if (payloadBytes % (channels * 2) != 0)
            {
                return false;
            }

            frame.Sequence = ReadUInt32(packet, offset + 8);
            frame.TimestampMs = ReadUInt32(packet, offset + 12);
            frame.SampleRate = sampleRate;
            frame.Channels = channels;
            frame.Pcm = new ArraySegment<byte>(packet, offset + HeaderSize, payloadBytes);
            return true;
        }

        public static int BytesPerSecond(int sampleRate, int channels)
        {
            return Math.Max(0, sampleRate) * Math.Max(1, channels) * (BitsPerSample / 8);
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
            return (uint)(buffer[offset] | (buffer[offset + 1] << 8) |
                          (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
        }
    }
}
