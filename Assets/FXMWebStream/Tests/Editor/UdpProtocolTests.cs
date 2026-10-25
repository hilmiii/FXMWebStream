using System;
using System.Collections.Generic;
using FXM.WebStream;
using NUnit.Framework;

namespace FXM.WebStream.Tests
{
    public sealed class UdpProtocolTests
    {
        [Test]
        public void LargePayloadIsSplitAndReassembledOutOfOrder()
        {
            const int mtu = 1200;
            const int chunkPayload = mtu - UdpFrameProtocol.HeaderSize;
            const int payloadSize = 100 * 1024;

            var payload = new byte[payloadSize];
            new System.Random(1234).NextBytes(payload);

            int chunkCount = UdpFrameChunker.GetChunkCount(payloadSize, chunkPayload);
            Assert.Greater(chunkCount, 1);

            var datagram = new byte[mtu];
            var chunks = new List<byte[]>(chunkCount);
            for (int i = 0; i < chunkCount; i++)
            {
                int length = UdpFrameChunker.WriteChunk(payload, payloadSize, 7, i, chunkCount, chunkPayload, datagram);
                var chunk = new byte[length];
                Buffer.BlockCopy(datagram, 0, chunk, 0, length);
                chunks.Add(chunk);
            }

            // Reversed order plus duplicates: reassembly must still be correct.
            var pool = new FrameBufferPool(4);
            var assembler = new UdpFrameAssembler(pool, 4, 2f);
            bool completed = false;
            AssembledFrame result = default(AssembledFrame);

            for (int i = chunks.Count - 1; i >= 0; i--)
            {
                if (assembler.TryAddPacket(chunks[i], chunks[i].Length, out AssembledFrame frame))
                {
                    completed = true;
                    result = frame;
                }

                if (i % 10 == 0)
                {
                    assembler.TryAddPacket(chunks[i], chunks[i].Length, out AssembledFrame duplicate);
                }
            }

            Assert.IsTrue(completed, "The frame never completed.");
            Assert.AreEqual(payloadSize, result.Length);
            Assert.AreEqual(0, assembler.PendingFrames);
            Assert.AreEqual(0, assembler.MalformedPackets);

            for (int i = 0; i < payloadSize; i++)
            {
                if (payload[i] != result.Buffer[i])
                {
                    Assert.Fail("Payload differs at byte " + i);
                }
            }

            pool.Return(result.Buffer);
        }

        [Test]
        public void PacketWithWrongMagicIsRejected()
        {
            var packet = new byte[64];
            var pool = new FrameBufferPool(2);
            var assembler = new UdpFrameAssembler(pool, 2, 1f);

            Assert.IsFalse(assembler.TryAddPacket(packet, packet.Length, out AssembledFrame frame));
            Assert.AreEqual(1, assembler.MalformedPackets);
        }

        [Test]
        public void IncompleteFrameExpires()
        {
            const int mtu = 1200;
            const int chunkPayload = mtu - UdpFrameProtocol.HeaderSize;
            var payload = new byte[5000];
            new System.Random(5).NextBytes(payload);
            int chunkCount = UdpFrameChunker.GetChunkCount(payload.Length, chunkPayload);

            var datagram = new byte[mtu];
            var pool = new FrameBufferPool(2);
            var assembler = new UdpFrameAssembler(pool, 2, 0.05f);

            // Only the last chunk: a half-finished frame.
            int length = UdpFrameChunker.WriteChunk(payload, payload.Length, 1, chunkCount - 1, chunkCount, chunkPayload, datagram);
            Assert.IsFalse(assembler.TryAddPacket(datagram, length, out AssembledFrame ignored));
            Assert.AreEqual(1, assembler.PendingFrames);

            System.Threading.Thread.Sleep(150);
            assembler.PruneExpired();

            Assert.AreEqual(0, assembler.PendingFrames);
            Assert.AreEqual(1, assembler.DroppedIncomplete);
        }
    }
}
