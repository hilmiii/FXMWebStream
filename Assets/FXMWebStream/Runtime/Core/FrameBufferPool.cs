using System;
using System.Collections.Generic;

namespace FXM.WebStream
{
    /// <summary>
    /// Simple thread-safe byte[] pool for the streaming pipeline. It avoids large
    /// per-frame allocations (a 1080p RGBA frame is 8 MB) that cause GC spikes.
    /// Every Rent() must be paired with Return().
    /// </summary>
    public sealed class FrameBufferPool
    {
        private readonly List<byte[]> _buffers = new List<byte[]>(8);
        private readonly int _maxRetained;
        private readonly object _lock = new object();

        public FrameBufferPool(int maxRetained = 8)
        {
            _maxRetained = Math.Max(1, maxRetained);
        }

        /// <summary>Number of buffers currently held by the pool.</summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _buffers.Count;
                }
            }
        }

        /// <summary>
        /// Rent a buffer of at least <paramref name="minimumSize"/> bytes. Picks the
        /// smallest sufficient buffer (best fit) to avoid wasting memory.
        /// </summary>
        public byte[] Rent(int minimumSize)
        {
            if (minimumSize < 0)
            {
                minimumSize = 0;
            }

            lock (_lock)
            {
                int bestIndex = -1;
                for (int i = 0; i < _buffers.Count; i++)
                {
                    int length = _buffers[i].Length;
                    if (length >= minimumSize && (bestIndex < 0 || length < _buffers[bestIndex].Length))
                    {
                        bestIndex = i;
                    }
                }

                if (bestIndex >= 0)
                {
                    byte[] buffer = _buffers[bestIndex];
                    _buffers.RemoveAt(bestIndex);
                    return buffer;
                }
            }

            return new byte[Math.Max(minimumSize, 256)];
        }

        /// <summary>Return a buffer to the pool. Null is ignored; contents are not cleared.</summary>
        public void Return(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0)
            {
                return;
            }

            lock (_lock)
            {
                if (_buffers.Count < _maxRetained)
                {
                    _buffers.Add(buffer);
                }
            }
        }

        /// <summary>Clear the pool (buffers become eligible for GC).</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _buffers.Clear();
            }
        }
    }
}
