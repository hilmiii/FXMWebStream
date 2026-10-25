using FXM.WebStream;
using NUnit.Framework;

namespace FXM.WebStream.Tests
{
    public sealed class StreamAudioProtocolTests
    {
        [Test]
        public void WritesAndReadsPcmPacket()
        {
            var packet = new byte[StreamAudioProtocol.HeaderSize + 8];
            for (int i = 0; i < 8; i++)
            {
                packet[StreamAudioProtocol.HeaderSize + i] = (byte)(i + 1);
            }

            StreamAudioProtocol.WriteHeader(packet, 0, 42u, 1500u, 24000, 1, 8);

            StreamAudioFrame frame;
            Assert.IsTrue(StreamAudioProtocol.TryRead(packet, 0, packet.Length, out frame));
            Assert.AreEqual(42u, frame.Sequence);
            Assert.AreEqual(1500u, frame.TimestampMs);
            Assert.AreEqual(24000, frame.SampleRate);
            Assert.AreEqual(1, frame.Channels);
            Assert.AreEqual(8, frame.Pcm.Count);
            Assert.AreEqual(1, packet[frame.Pcm.Offset]);
            Assert.AreEqual(8, packet[frame.Pcm.Offset + 7]);
        }

        [Test]
        public void StereoPayloadMustContainWholeFrames()
        {
            var packet = new byte[StreamAudioProtocol.HeaderSize + 3];
            StreamAudioProtocol.WriteHeader(packet, 0, 1u, 0u, 48000, 2, 3);

            StreamAudioFrame frame;
            Assert.IsFalse(StreamAudioProtocol.TryRead(packet, 0, packet.Length, out frame));
        }

        [Test]
        public void JpegIsNotTreatedAsAudio()
        {
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0 };
            StreamAudioFrame frame;
            Assert.IsFalse(StreamAudioProtocol.TryRead(jpeg, 0, jpeg.Length, out frame));
        }

        [Test]
        public void TruncatedPacketIsRejected()
        {
            var packet = new byte[StreamAudioProtocol.HeaderSize + 4];
            StreamAudioProtocol.WriteHeader(packet, 0, 1u, 0u, 24000, 1, 4);

            StreamAudioFrame frame;
            Assert.IsFalse(StreamAudioProtocol.TryRead(packet, 0, StreamAudioProtocol.HeaderSize - 1, out frame));
        }

        [Test]
        public void InvalidFormatIsRejected()
        {
            var packet = new byte[StreamAudioProtocol.HeaderSize + 4];
            StreamAudioProtocol.WriteHeader(packet, 0, 1u, 0u, 24000, 3, 4);

            StreamAudioFrame frame;
            Assert.IsFalse(StreamAudioProtocol.TryRead(packet, 0, packet.Length, out frame));
        }

        [Test]
        public void ReportsBandwidth()
        {
            Assert.AreEqual(48000, StreamAudioProtocol.BytesPerSecond(24000, 1));
            Assert.AreEqual(192000, StreamAudioProtocol.BytesPerSecond(48000, 2));
        }
    }
}
