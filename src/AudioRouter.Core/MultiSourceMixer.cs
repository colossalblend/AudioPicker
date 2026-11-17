using System;
using System.Collections.Generic;
using System.Threading;

namespace AudioRouter.Core
{
    /// <summary>Mixes N loopback captures into one stereo 48k float stream.
    /// Every source capture writes into its own lane buffer; the mixer thread
    /// wakes every 20 ms, takes the max frame count across lanes, sums the
    /// lanes sample-by-sample and pushes one chunk downstream. All clients
    /// ride the same WASAPI clock so lanes stay sample-aligned.</summary>
    public sealed class MultiSourceMixer : IDisposable
    {
        private sealed class Lane
        {
            public readonly LoopbackCapture Capture;
            public readonly object Lock = new();
            public float[] Pending = Array.Empty<float>();
            public int PendingCount;

            public Lane(AudioSource source, MultiSourceMixer owner)
            {
                Capture = new LoopbackCapture(source, new Proxy(this));
            }

            private sealed class Proxy : IAudioSink
            {
                private readonly Lane _lane;
                public Proxy(Lane lane) => _lane = lane;

                public void Push(ReadOnlySpan<float> frames)
                {
                    lock (_lane.Lock)
                    {
                        int need = _lane.PendingCount + frames.Length;
                        if (_lane.Pending.Length < need)
                        {
                            int cap = Math.Max(need, _lane.Pending.Length * 2);
                            var grown = new float[cap];
                            Array.Copy(_lane.Pending, grown, _lane.PendingCount);
                            _lane.Pending = grown;
                        }
                        frames.CopyTo(_lane.Pending.AsSpan(_lane.PendingCount));
                        _lane.PendingCount += frames.Length;
                        // hard cap ~2 s per lane: if the mixer stalled, drop oldest
                        const int capFrames = 48000 * 2 * 2;
                        if (_lane.PendingCount > capFrames)
                        {
                            int drop = _lane.PendingCount - capFrames;
                            Array.Copy(_lane.Pending, drop, _lane.Pending, 0, _lane.PendingCount - drop);
                            _lane.PendingCount = capFrames;
                        }
                    }
                }
            }
        }

        private readonly List<Lane> _lanes = new();
        private readonly IAudioSink _downstream;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _mixerThread;
        private float _gain = 1f;
        public float Gain
        {
            get => Volatile.Read(ref _gain);
            set => Volatile.Write(ref _gain, value);
        }
        public float Level { get; private set; }
        public bool AnyDead { get; private set; }
        public event Action Died;

        public IReadOnlyList<LoopbackCapture> Captures => _lanes.ConvertAll(l => l.Capture);

        public MultiSourceMixer(IEnumerable<AudioSource> sources, IAudioSink downstream)
        {
            _downstream = downstream;
            foreach (var s in sources)
            {
                var lane = new Lane(s, this);
                lane.Capture.DeadChanged += () =>
                {
                    AnyDead = true;
                    Died?.Invoke();
                };
                _lanes.Add(lane);
            }
            _mixerThread = new Thread(MixLoop) { IsBackground = true, Name = "mixer" };
            _mixerThread.Start();
        }

        private void MixLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                int maxFrames = 0;
                for (int i = 0; i < _lanes.Count; i++)
                {
                    var lane = _lanes[i];
                    lock (lane.Lock)
                        maxFrames = Math.Max(maxFrames, lane.PendingCount);
                }

                if (maxFrames == 0)
                {
                    _cts.Token.WaitHandle.WaitOne(20);
                    continue;
                }

                var mix = new float[maxFrames];
                float peak = 0f;
                float gain = Gain;
                for (int i = 0; i < _lanes.Count; i++)
                {
                    var lane = _lanes[i];
                    lock (lane.Lock)
                    {
                        int take = Math.Min(lane.PendingCount, maxFrames);
                        if (take == 0) continue;
                        var src = lane.Pending.AsSpan(0, take);
                        for (int j = 0; j < take; j++)
                        {
                            float v = src[j] * gain;
                            mix[j] += v;
                            float a = Math.Abs(v);
                            if (a > peak) peak = a;
                        }
                        int rest = lane.PendingCount - take;
                        Array.Copy(lane.Pending, take, lane.Pending, 0, rest);
                        lane.PendingCount = rest;
                    }
                }
                Level = peak;

                _downstream.Push(mix.AsSpan(0, maxFrames));
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _mixerThread.Join(1000);
            foreach (var lane in _lanes) lane.Capture.Dispose();
        }
    }
}
