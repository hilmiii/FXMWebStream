using FXM.WebStream;
using NUnit.Framework;

namespace FXM.WebStream.Tests
{
    public sealed class StreamControlTests
    {
        [Test]
        public void StateMessageRoundTrips()
        {
            var message = StreamControlMessage.StateMessage(1234, StreamPlaybackState.Paused, 4321, 90000);
            message.Sequence = 9;

            var buffer = new byte[StreamControlProtocol.HeaderSize];
            int written = StreamControlProtocol.Write(buffer, 0, message);

            Assert.AreEqual(StreamControlProtocol.HeaderSize, written);
            Assert.IsTrue(StreamControlProtocol.IsControlPacket(buffer, 0, buffer.Length));

            StreamControlMessage parsed;
            Assert.IsTrue(StreamControlProtocol.TryRead(buffer, 0, buffer.Length, out parsed));
            Assert.AreEqual(StreamMessageKind.State, parsed.Kind);
            Assert.AreEqual(StreamPlaybackState.Paused, parsed.State);
            Assert.AreEqual(1234u, parsed.SourceId);
            Assert.AreEqual(9u, parsed.Sequence);
            Assert.AreEqual(4321, parsed.PositionMs);
            Assert.AreEqual(90000, parsed.DurationMs);
        }

        [Test]
        public void CommandMessageRoundTrips()
        {
            var message = StreamControlMessage.CommandMessage(StreamCommandType.Seek, 0, 15000);
            var buffer = StreamControlProtocol.Create(message);

            StreamControlMessage parsed;
            Assert.IsTrue(StreamControlProtocol.TryRead(buffer, 0, buffer.Length, out parsed));
            Assert.AreEqual(StreamMessageKind.Command, parsed.Kind);
            Assert.AreEqual(StreamCommandType.Seek, parsed.Command);
            Assert.AreEqual(0u, parsed.SourceId);
            Assert.AreEqual(15000, parsed.Value);
        }

        [Test]
        public void GarbageIsRejected()
        {
            var garbage = new byte[64];
            StreamControlMessage parsed;
            Assert.IsFalse(StreamControlProtocol.TryRead(garbage, 0, garbage.Length, out parsed));

            var shortBuffer = new byte[StreamControlProtocol.HeaderSize - 1];
            Assert.IsFalse(StreamControlProtocol.TryRead(shortBuffer, 0, shortBuffer.Length, out parsed));
        }

        [Test]
        public void SequenceNumbersNeverReturnZero()
        {
            uint sequence = 0;
            Assert.AreEqual(1u, StreamControlProtocol.NextSequence(ref sequence));

            sequence = uint.MaxValue;
            Assert.AreEqual(1u, StreamControlProtocol.NextSequence(ref sequence));
        }
    }
}
