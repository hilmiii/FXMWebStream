using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace FXM.WebStream
{
    /// <summary>One encoded JPEG frame, ready to send through a transport.</summary>
    public sealed class EncodedFrame
    {
        /// <summary>Encoder-owned pooled buffer. Do not keep it after ReleaseFrame().</summary>
        public byte[] Buffer;

        /// <summary>Valid byte count inside <see cref="Buffer"/>.</summary>
        public int Length;

        /// <summary>Capture timestamp (Stopwatch ticks) used to measure latency.</summary>
        public long Timestamp;

        /// <summary>Encoder time spent on this frame (milliseconds).</summary>
        public double EncodeMilliseconds;
    }

    /// <summary>
    /// Worker-thread JPEG encoder. All heavy work (color conversion, DCT, Huffman) runs
    /// off the main thread, so game frame rate is not affected.
    ///
    /// Backpressure policy:
    /// - The job queue is bounded (default 2). When full, the OLDEST frame is dropped so
    ///   latency does not grow.
    /// - The result queue is bounded (default 4). When full, the oldest result is dropped
    ///   and its buffer is returned to the pool.
    /// - All buffers use <see cref="FrameBufferPool"/>, so steady state allocates nothing.
    /// </summary>
    public sealed class AsyncJpegEncoder : IDisposable
    {
        private sealed class EncodeJob
        {
            public byte[] Source;
            public int Width;
            public int Height;
            public int Quality;
            public long Timestamp;
        }

        private readonly int _workerCount;
        private readonly int _maxPendingJobs;
        private readonly int _maxPendingResults;
        private readonly bool _flipVertically;

        private readonly FrameBufferPool _sourcePool;
        private readonly FrameBufferPool _outputPool;
        private readonly Queue<EncodeJob> _jobs = new Queue<EncodeJob>();
        private readonly Queue<EncodeJob> _jobCache = new Queue<EncodeJob>();
        private readonly Queue<EncodedFrame> _results = new Queue<EncodedFrame>();
        private readonly Queue<EncodedFrame> _resultCache = new Queue<EncodedFrame>();
        private readonly object _jobLock = new object();
        private readonly object _resultLock = new object();
        private readonly List<Thread> _workers = new List<Thread>();

        private volatile bool _stop;
        private volatile bool _started;
        private long _droppedJobs;
        private long _droppedResults;

        public AsyncJpegEncoder(int workerCount = 1, int maxPendingJobs = 2, int maxPendingResults = 4, bool flipVertically = true)
        {
            _workerCount = Math.Max(1, workerCount);
            _maxPendingJobs = Math.Max(1, maxPendingJobs);
            _maxPendingResults = Math.Max(1, maxPendingResults);
            _flipVertically = flipVertically;

            // Small pool: capture input (large) plus JPEG output (small) per worker.
            _sourcePool = new FrameBufferPool(2 + _workerCount);
            _outputPool = new FrameBufferPool(4 + _workerCount);
        }

        public bool IsRunning
        {
            get { return _started && !_stop; }
        }

        /// <summary>Number of jobs still waiting to encode (0..maxPendingJobs).</summary>
        public int PendingJobs
        {
            get
            {
                lock (_jobLock)
                {
                    return _jobs.Count;
                }
            }
        }

        /// <summary>Total frames dropped because the encoder was overloaded.</summary>
        public long DroppedFrames
        {
            get { return Interlocked.Read(ref _droppedJobs) + Interlocked.Read(ref _droppedResults); }
        }

        /// <summary>
        /// Submit an RGBA frame to the encoder. Ownership of <paramref name="rgba"/> moves to
        /// the encoder (the buffer returns to the pool after use), so callers must not use it again.
        /// </summary>
        public void Submit(byte[] rgba, int width, int height, int quality, long timestamp)
        {
            if (rgba == null)
            {
                return;
            }

            if (_stop)
            {
                // The encoder is disposed: do not retain the caller's buffer.
                _sourcePool.Return(rgba);
                return;
            }

            EnsureStarted();

            EncodeJob droppedJob = null;
            EncodeJob job;

            lock (_jobLock)
            {
                if (_jobs.Count >= _maxPendingJobs)
                {
                    // Drop the oldest frame: skipping a frame is better than adding latency.
                    droppedJob = _jobs.Dequeue();
                    Interlocked.Increment(ref _droppedJobs);
                }

                job = _jobCache.Count > 0 ? _jobCache.Dequeue() : new EncodeJob();
                job.Source = rgba;
                job.Width = width;
                job.Height = height;
                job.Quality = quality;
                job.Timestamp = timestamp;
                _jobs.Enqueue(job);
                Monitor.Pulse(_jobLock);
            }

            if (droppedJob != null)
            {
                _sourcePool.Return(droppedJob.Source);
                droppedJob.Source = null;
                lock (_jobLock)
                {
                    _jobCache.Enqueue(droppedJob);
                }
            }
        }

        /// <summary>Dequeue an encoded frame. Call from the main thread every Update.</summary>
        public bool TryDequeue(out EncodedFrame frame)
        {
            lock (_resultLock)
            {
                if (_results.Count > 0)
                {
                    frame = _results.Dequeue();
                    return true;
                }
            }

            frame = null;
            return false;
        }

        /// <summary>Return an encoded frame buffer after sending it.</summary>
        public void ReleaseFrame(EncodedFrame frame)
        {
            if (frame == null)
            {
                return;
            }

            _outputPool.Return(frame.Buffer);
            frame.Buffer = null;
            frame.Length = 0;

            lock (_resultLock)
            {
                if (_resultCache.Count < _maxPendingResults + 4)
                {
                    _resultCache.Enqueue(frame);
                }
            }
        }

        private void EnsureStarted()
        {
            if (_started)
            {
                return;
            }

            _started = true;
            for (int i = 0; i < _workerCount; i++)
            {
                var thread = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "FXM.JpegEncoder." + i
                };
                thread.Start();
                _workers.Add(thread);
            }
        }

        private void WorkerLoop()
        {
            // Encoder and scratch buffer per thread; not shared, so no locking is needed.
            var encoder = new JpegEncoder();

            while (true)
            {
                EncodeJob job;

                lock (_jobLock)
                {
                    while (_jobs.Count == 0 && !_stop)
                    {
                        Monitor.Wait(_jobLock);
                    }

                    if (_stop && _jobs.Count == 0)
                    {
                        return;
                    }

                    job = _jobs.Dequeue();
                }

                byte[] output = _outputPool.Rent(4096);
                int length = 0;
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    length = encoder.Encode(job.Source, job.Width, job.Height, 4, job.Quality, ref output, _flipVertically);
                }
                catch (Exception exception)
                {
                    // Do not stop the worker because one frame failed.
                    Debug.WriteLine("[FXM.WebStream] Encode failed: " + exception.Message);
                    length = 0;
                }

                stopwatch.Stop();
                _sourcePool.Return(job.Source);
                job.Source = null;

                lock (_jobLock)
                {
                    if (_jobCache.Count < _maxPendingJobs + 2)
                    {
                        _jobCache.Enqueue(job);
                    }
                }

                if (length <= 0)
                {
                    _outputPool.Return(output);
                    continue;
                }

                EncodedFrame completed = null;
                lock (_resultLock)
                {
                    if (_results.Count >= _maxPendingResults)
                    {
                        completed = _results.Dequeue();
                        Interlocked.Increment(ref _droppedResults);
                    }
                }

                if (completed != null)
                {
                    _outputPool.Return(completed.Buffer);
                    completed.Buffer = null;
                    lock (_resultLock)
                    {
                        _resultCache.Enqueue(completed);
                    }
                }

                EncodedFrame result;
                lock (_resultLock)
                {
                    result = _resultCache.Count > 0 ? _resultCache.Dequeue() : new EncodedFrame();
                }

                result.Buffer = output;
                result.Length = length;
                result.Timestamp = job.Timestamp;
                result.EncodeMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

                lock (_resultLock)
                {
                    _results.Enqueue(result);
                }
            }
        }

        /// <summary>Stop workers and release buffers. Call from the main thread when disabling the component.</summary>
        public void Dispose()
        {
            _stop = true;

            lock (_jobLock)
            {
                Monitor.PulseAll(_jobLock);
            }

            bool allStopped = true;
            for (int i = 0; i < _workers.Count; i++)
            {
                // Give the in-flight encode a chance to finish (up to 500 ms per thread).
                if (!_workers[i].Join(500))
                {
                    allStopped = false;
                }
            }

            _workers.Clear();

            if (!allStopped)
            {
                // A worker still owns the buffer; let GC clean it up instead of racing.
                return;
            }

            lock (_jobLock)
            {
                while (_jobs.Count > 0)
                {
                    var job = _jobs.Dequeue();
                    _sourcePool.Return(job.Source);
                    job.Source = null;
                }
            }

            lock (_resultLock)
            {
                while (_results.Count > 0)
                {
                    var result = _results.Dequeue();
                    _outputPool.Return(result.Buffer);
                    result.Buffer = null;
                }
            }

            _sourcePool.Clear();
            _outputPool.Clear();
        }
    }
}
