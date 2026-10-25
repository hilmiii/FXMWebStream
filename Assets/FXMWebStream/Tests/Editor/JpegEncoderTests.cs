using System;
using System.Diagnostics;
using FXM.WebStream;
using NUnit.Framework;
using UnityEngine;

namespace FXM.WebStream.Tests
{
    /// <summary>
    /// Verifies that the custom JPEG encoder output decodes with Unity's native
    /// ImageConversion.LoadImage, including row orientation and odd dimensions.
    ///
    /// Test convention:
    /// - Source rows 0..height/2-1 are BLUE and represent the BOTTOM of the image
    ///   (Unity texture layout: row 0 = bottom).
    /// - Source rows height/2..height-1 are RED and represent the TOP of the image.
    /// </summary>
    public sealed class JpegEncoderTests
    {
        private static byte[] CreateSplitImage(int width, int height)
        {
            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width + x) * 4;
                    bool topHalf = y >= height / 2;
                    rgba[index + 0] = topHalf ? (byte)230 : (byte)20;
                    rgba[index + 1] = topHalf ? (byte)40 : (byte)30;
                    rgba[index + 2] = topHalf ? (byte)40 : (byte)220;
                    rgba[index + 3] = 255;
                }
            }

            return rgba;
        }

        private static byte[] EncodeExact(JpegEncoder encoder, byte[] rgba, int width, int height, int quality, bool flipVertically)
        {
            byte[] buffer = null;
            int length = encoder.Encode(rgba, width, height, 4, quality, ref buffer, flipVertically);
            var exact = new byte[length];
            Buffer.BlockCopy(buffer, 0, exact, 0, length);
            return exact;
        }

        [Test]
        public void EncodedJpegLoadsInUnity()
        {
            const int width = 64;
            const int height = 48;

            byte[] rgba = CreateSplitImage(width, height);
            var encoder = new JpegEncoder();
            byte[] jpeg = EncodeExact(encoder, rgba, width, height, 90, false);

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                Assert.IsTrue(texture.LoadImage(jpeg, false), "LoadImage rejected the encoder output.");
                Assert.AreEqual(width, texture.width);
                Assert.AreEqual(height, texture.height);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void FlipVerticallyMatchesUnityTextureLayout()
        {
            const int width = 64;
            const int height = 48;

            byte[] rgba = CreateSplitImage(width, height);
            var encoder = new JpegEncoder();

            // flip = true (sender default): source row 0 (blue/bottom) must appear at the
            // BOTTOM of the image, so the top half is red.
            byte[] flipped = EncodeExact(encoder, rgba, width, height, 90, true);
            var flippedTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                Assert.IsTrue(flippedTexture.LoadImage(flipped, false));

                Color top = flippedTexture.GetPixel(width / 2, height - 2);
                Color bottom = flippedTexture.GetPixel(width / 2, 1);

                Assert.Greater(top.r, 0.6f, "With flip, the top row must be red.");
                Assert.Less(top.b, 0.4f, "With flip, the top row must not be blue.");
                Assert.Greater(bottom.b, 0.6f, "With flip, the bottom row must be blue.");
                Assert.Less(bottom.r, 0.4f, "With flip, the bottom row must not be red.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(flippedTexture);
            }

            // flip = false: source row order is used as-is, so the top half is blue.
            byte[] unflipped = EncodeExact(encoder, rgba, width, height, 90, false);
            var unflippedTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                Assert.IsTrue(unflippedTexture.LoadImage(unflipped, false));

                Color top = unflippedTexture.GetPixel(width / 2, height - 2);
                Color bottom = unflippedTexture.GetPixel(width / 2, 1);

                Assert.Greater(top.b, 0.6f, "Without flip, the top row must be blue.");
                Assert.Greater(bottom.r, 0.6f, "Without flip, the bottom row must be red.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(unflippedTexture);
            }
        }

        [Test]
        public void OddDimensionsArePreserved()
        {
            const int width = 37;
            const int height = 23;

            byte[] rgba = CreateSplitImage(width, height);
            var encoder = new JpegEncoder();
            byte[] jpeg = EncodeExact(encoder, rgba, width, height, 75, false);

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                Assert.IsTrue(texture.LoadImage(jpeg, false));
                Assert.AreEqual(width, texture.width);
                Assert.AreEqual(height, texture.height);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void AsyncEncoderProducesLoadableJpeg()
        {
            const int width = 320;
            const int height = 240;

            byte[] source = CreateSplitImage(width, height);
            var encoder = new AsyncJpegEncoder(1, 2, 4, true);

            try
            {
                for (int i = 0; i < 3; i++)
                {
                    var copy = new byte[source.Length];
                    Buffer.BlockCopy(source, 0, copy, 0, source.Length);
                    encoder.Submit(copy, width, height, 70, Stopwatch.GetTimestamp());
                }

                EncodedFrame frame = null;
                var timeout = Stopwatch.StartNew();
                while (timeout.ElapsedMilliseconds < 5000 && frame == null)
                {
                    encoder.TryDequeue(out frame);
                    if (frame == null)
                    {
                        System.Threading.Thread.Sleep(10);
                    }
                }

                Assert.IsNotNull(frame, "The encoder produced no frame within 5 seconds.");

                var jpeg = new byte[frame.Length];
                Buffer.BlockCopy(frame.Buffer, 0, jpeg, 0, frame.Length);
                encoder.ReleaseFrame(frame);

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                try
                {
                    Assert.IsTrue(texture.LoadImage(jpeg, false));
                    Assert.AreEqual(width, texture.width);
                    Assert.AreEqual(height, texture.height);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
            finally
            {
                encoder.Dispose();
            }
        }
    }
}
