using System;
using UnityEngine;

namespace FXM.WebStream.WalkingSim
{
    /// <summary>One player transform update sent over the relay's player channel.</summary>
    public struct WalkingSimState
    {
        public uint PlayerId;
        public uint Sequence;
        public Vector3 Position;
        public float Yaw;
        public bool Moving;
    }

    /// <summary>
    /// Small gameplay packet. It is deliberately independent from
    /// <see cref="StreamControlProtocol"/> and never travels through the video frame queue:
    /// the relay forwards it only between peers that joined with role=player.
    ///
    /// Layout (32 bytes, little endian):
    ///   "FXG1" | version | flags | reserved(2) | player id | sequence | x | y | z | yaw
    /// </summary>
    public static class WalkingSimProtocol
    {
        public const byte Version = 1;
        public const int PacketSize = 32;
        public const byte FlagMoving = 1;

        private static readonly byte Magic0 = (byte)'F';
        private static readonly byte Magic1 = (byte)'X';
        private static readonly byte Magic2 = (byte)'G';
        private static readonly byte Magic3 = (byte)'1';

        public static bool IsPacket(byte[] buffer, int offset, int length)
        {
            return buffer != null &&
                   length >= PacketSize &&
                   offset >= 0 &&
                   offset + PacketSize <= buffer.Length &&
                   buffer[offset] == Magic0 &&
                   buffer[offset + 1] == Magic1 &&
                   buffer[offset + 2] == Magic2 &&
                   buffer[offset + 3] == Magic3;
        }

        public static int Write(byte[] buffer, int offset, in WalkingSimState state)
        {
            if (buffer == null || offset < 0 || offset + PacketSize > buffer.Length)
            {
                return 0;
            }

            buffer[offset] = Magic0;
            buffer[offset + 1] = Magic1;
            buffer[offset + 2] = Magic2;
            buffer[offset + 3] = Magic3;
            buffer[offset + 4] = Version;
            buffer[offset + 5] = state.Moving ? FlagMoving : (byte)0;
            buffer[offset + 6] = 0;
            buffer[offset + 7] = 0;

            WriteUInt32(buffer, offset + 8, state.PlayerId);
            WriteUInt32(buffer, offset + 12, state.Sequence);
            WriteFloat(buffer, offset + 16, state.Position.x);
            WriteFloat(buffer, offset + 20, state.Position.y);
            WriteFloat(buffer, offset + 24, state.Position.z);
            WriteFloat(buffer, offset + 28, state.Yaw);
            return PacketSize;
        }

        public static bool TryRead(byte[] buffer, int offset, int length, out WalkingSimState state)
        {
            state = default(WalkingSimState);

            if (!IsPacket(buffer, offset, length) ||
                buffer[offset + 4] != Version)
            {
                return false;
            }

            state.PlayerId = ReadUInt32(buffer, offset + 8);
            state.Sequence = ReadUInt32(buffer, offset + 12);
            state.Position = new Vector3(
                ReadFloat(buffer, offset + 16),
                ReadFloat(buffer, offset + 20),
                ReadFloat(buffer, offset + 24));
            state.Yaw = ReadFloat(buffer, offset + 28);
            state.Moving = (buffer[offset + 5] & FlagMoving) != 0;
            return true;
        }

        public static bool TryRead(ArraySegment<byte> data, out WalkingSimState state)
        {
            if (data.Array == null)
            {
                state = default(WalkingSimState);
                return false;
            }

            return TryRead(data.Array, data.Offset, data.Count, out state);
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] |
                          (buffer[offset + 1] << 8) |
                          (buffer[offset + 2] << 16) |
                          (buffer[offset + 3] << 24));
        }

        private static void WriteFloat(byte[] buffer, int offset, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            buffer[offset] = bytes[0];
            buffer[offset + 1] = bytes[1];
            buffer[offset + 2] = bytes[2];
            buffer[offset + 3] = bytes[3];
        }

        private static float ReadFloat(byte[] buffer, int offset)
        {
            if (BitConverter.IsLittleEndian)
            {
                return BitConverter.ToSingle(buffer, offset);
            }

            var bytes = new byte[4];
            bytes[0] = buffer[offset + 3];
            bytes[1] = buffer[offset + 2];
            bytes[2] = buffer[offset + 1];
            bytes[3] = buffer[offset];
            return BitConverter.ToSingle(bytes, 0);
        }
    }
}
