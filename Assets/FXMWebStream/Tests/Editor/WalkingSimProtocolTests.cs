using FXM.WebStream.WalkingSim;
using NUnit.Framework;
using UnityEngine;

namespace FXM.WebStream.Tests
{
    public sealed class WalkingSimProtocolTests
    {
        [Test]
        public void RoundTripsPlayerState()
        {
            var original = new WalkingSimState
            {
                PlayerId = 0xDEADBEEF,
                Sequence = 42,
                Position = new Vector3(-61.17f, 16.9f, -76.66f),
                Yaw = 123.5f,
                Moving = true
            };

            var buffer = new byte[WalkingSimProtocol.PacketSize];
            int written = WalkingSimProtocol.Write(buffer, 0, original);

            Assert.AreEqual(WalkingSimProtocol.PacketSize, written);

            WalkingSimState decoded;
            Assert.IsTrue(WalkingSimProtocol.TryRead(buffer, 0, written, out decoded));
            Assert.AreEqual(original.PlayerId, decoded.PlayerId);
            Assert.AreEqual(original.Sequence, decoded.Sequence);
            Assert.AreEqual(original.Position.x, decoded.Position.x, 0.0001f);
            Assert.AreEqual(original.Position.y, decoded.Position.y, 0.0001f);
            Assert.AreEqual(original.Position.z, decoded.Position.z, 0.0001f);
            Assert.AreEqual(original.Yaw, decoded.Yaw, 0.0001f);
            Assert.IsTrue(decoded.Moving);
        }

        [Test]
        public void RejectsForeignOrShortPackets()
        {
            var buffer = new byte[WalkingSimProtocol.PacketSize];
            var state = new WalkingSimState { PlayerId = 7, Moving = false };
            WalkingSimProtocol.Write(buffer, 0, state);

            WalkingSimState decoded;
            Assert.IsFalse(WalkingSimProtocol.TryRead(buffer, 0, WalkingSimProtocol.PacketSize - 1, out decoded));

            buffer[0] = (byte)'X';
            Assert.IsFalse(WalkingSimProtocol.TryRead(buffer, 0, WalkingSimProtocol.PacketSize, out decoded));

            var other = new byte[WalkingSimProtocol.PacketSize + 8];
            WalkingSimProtocol.Write(other, 8, state);
            Assert.IsTrue(WalkingSimProtocol.TryRead(other, 8, WalkingSimProtocol.PacketSize, out decoded));
            Assert.AreEqual(7u, decoded.PlayerId);
        }

        [Test]
        public void MissingMovingFlagIsIdle()
        {
            var state = new WalkingSimState
            {
                PlayerId = 1,
                Sequence = 1,
                Position = Vector3.one,
                Yaw = 0f,
                Moving = false
            };

            var buffer = new byte[WalkingSimProtocol.PacketSize];
            WalkingSimProtocol.Write(buffer, 0, state);

            WalkingSimState decoded;
            Assert.IsTrue(WalkingSimProtocol.TryRead(buffer, 0, buffer.Length, out decoded));
            Assert.IsFalse(decoded.Moving);
        }
    }
}
