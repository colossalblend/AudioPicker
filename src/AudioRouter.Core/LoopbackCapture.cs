using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AudioRouter.Core.Interop;

namespace AudioRouter.Core
{
    /// <summary>A capture source: whole system (minus this process) or one
    /// process tree.</summary>
    public abstract record AudioSource
    {
        public sealed record System() : AudioSource;
        public sealed record Process(uint Pid, bool IncludeTree = true) : AudioSource;
    }

    /// <summary>Pushes interleaved stereo float32 frames (48 kHz).</summary>
    public interface IAudioSink
    {
        void Push(ReadOnlySpan<float> frames);
    }

    /// <summary>Background WASAPI loopback capture. One instance = one source.
    /// Owns its own MTA thread; audio arrives via IAudioSink.Push.</summary>
    public sealed class LoopbackCapture : IDisposable
    {
        private static readonly int TargetRate = 48000;

        private readonly AudioSource _source;
        private readonly IAudioSink _sink;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;
        public bool Dead { get; private set; }
        public event Action DeadChanged;

        public LoopbackCapture(AudioSource source, IAudioSink sink)
        {
            _source = source;
            _sink = sink;
            _thread = new Thread(Run) { IsBackground = true, Name = "loopback" };
            _thread.Start();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _thread.Join(2000);
            DeadChanged = null;
        }

        private readonly struct WaveFormat
        {
            public readonly int Rate;
            public readonly int Channels;
            public readonly int BytesPerFrame;
            public readonly bool IsFloat;

            public WaveFormat(int rate, int channels, int bytesPerFrame, bool isFloat)
            {
                Rate = rate;
                Channels = channels;
                BytesPerFrame = bytesPerFrame;
                IsFloat = isFloat;
            }

            public WaveFormat(Wasapi.WAVEFORMATEX w)
            {
                Rate = (int)w.nSamplesPerSec;
                Channels = w.nChannels;
                BytesPerFrame = w.nBlockAlign;
                IsFloat = w.wFormatTag == 3;
            }
        }

        /// <summary>Converts one raw packet to interleaved stereo 48k float.</summary>
        private static void PacketToStereo48k(ReadOnlySpan<byte> raw, WaveFormat fmt,
            List<float> outFrames)
        {
            outFrames.Clear();
            if (fmt.IsFloat)
            {
                var data = MemoryMarshal.Cast<byte, float>(raw);
                for (int i = 0; i < data.Length; i += fmt.Channels)
                {
                    float l = data[i];
                    float r = i + 1 < data.Length ? data[i + 1] : l;
                    outFrames.Add(l);
                    outFrames.Add(r);
                }
            }
            else
            {
                var data = MemoryMarshal.Cast<byte, short>(raw);
                for (int i = 0; i < data.Length; i += fmt.Channels)
                {
                    float l = i < data.Length ? data[i] / 32768f : 0f;
                    float r = i + 1 < data.Length ? data[i + 1] / 32768f : l;
                    outFrames.Add(l);
                    outFrames.Add(r);
                }
            }

            if (fmt.Rate != TargetRate && fmt.Rate > 0)
            {
                int srcFrames = outFrames.Count / 2;
                int dstFrames = (int)((long)srcFrames * TargetRate / fmt.Rate);
                if (dstFrames > 1 && dstFrames != srcFrames)
                {
                    var resampled = new List<float>(dstFrames * 2);
                    for (int i = 0; i < dstFrames; i++)
                    {
                        double pos = (double)i * (srcFrames - 1) / (dstFrames - 1);
                        int idx = (int)pos;
                        double frac = pos - idx;
                        int i0 = idx * 2;
                        int i1 = Math.Min(idx + 1, srcFrames - 1) * 2;
                        resampled.Add(outFrames[i0] + (outFrames[i1] - outFrames[i0]) * (float)frac);
                        resampled.Add(outFrames[i0 + 1] + (outFrames[i1 + 1] - outFrames[i0 + 1]) * (float)frac);
                    }
                    outFrames.Clear();
                    outFrames.AddRange(resampled);
                }
            }
        }

        private void Run()
        {
            ComThread.Initialize();
            try
            {
                RunCore();
            }
            catch (Exception)
            {
                Dead = true;
                DeadChanged?.Invoke();
            }
            finally
            {
                ComThread.Uninitialize();
            }
        }

        private void RunCore()
        {
            Wasapi.IAudioClient client;
            WaveFormat fmt;
            if (_source is AudioSource.Process p)
            {
                client = Wasapi.ActivateProcessLoopbackClient(p.Pid, p.IncludeTree);
                fmt = InitClient(client, Wasapi.WAVEFORMATEX.Float32());
            }
            else
            {
                // system: exclude our own tree so the self-monitor never feeds back
                uint selfPid = (uint)Environment.ProcessId;
                try
                {
                    client = Wasapi.ActivateProcessLoopbackClient(selfPid, includeTree: false);
                    fmt = InitClient(client, Wasapi.WAVEFORMATEX.Float32());
                }
                catch
                {
                    RunNaudioSystemFallback();
                    return;
                }
            }

            int hr = client.GetService(in Wasapi.IID_IAudioCaptureClient, out object svc);
            if (hr != 0) throw Marshal.GetExceptionForHR(hr);
            var capture = (Wasapi.IAudioCaptureClient)svc;
            if (client.Start() != 0) throw new InvalidOperationException("Start failed");

            var buffer = new List<float>(8192);
            var raw = new byte[64 * 1024];
            while (!_cts.IsCancellationRequested)
            {
                hr = capture.GetNextPacketSize(out uint packet);
                if (hr != 0) throw Marshal.GetExceptionForHR(hr);
                if (packet == 0)
                {
                    Thread.Sleep(4);
                    continue;
                }

                hr = capture.GetBuffer(out IntPtr data, out uint frames, out int flags,
                    out _, out _);
                if (hr != 0) throw Marshal.GetExceptionForHR(hr);

                if (frames > 0 && (flags & Wasapi.AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                {
                    int bytes = (int)frames * fmt.BytesPerFrame;
                    if (raw.Length < bytes) raw = new byte[bytes];
                    Marshal.Copy(data, raw, 0, bytes);
                    PacketToStereo48k(raw.AsSpan(0, bytes), fmt, buffer);
                }
                else
                {
                    for (uint i = 0; i < frames; i++) { buffer.Add(0f); buffer.Add(0f); }
                }
                capture.ReleaseBuffer(frames);

                if (buffer.Count >= 512)
                {
                    _sink.Push(buffer.ToArray());
                    buffer.Clear();
                }
            }
            client.Stop();
        }

        /// <summary>Rare fallback: raw process-loopback activation failed, so
        /// capture the default render device's loopback through NAudio instead.
        /// Runs until cancellation; pushes through the same sink.</summary>
        private void RunNaudioSystemFallback()
        {
            using var cap = new NAudio.Wave.WasapiLoopbackCapture();
            var nf = cap.WaveFormat;
            var fmt = new WaveFormat(nf.SampleRate, nf.Channels,
                nf.Channels * nf.BitsPerSample / 8,
                nf.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat);
            var buffer = new List<float>(8192);
            cap.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                PacketToStereo48k(e.Buffer.AsSpan(0, e.BytesRecorded), fmt, buffer);
                if (buffer.Count >= 512)
                {
                    _sink.Push(buffer.ToArray());
                    buffer.Clear();
                }
            };
            cap.StartRecording();
            try
            {
                while (!_cts.IsCancellationRequested)
                    _cts.Token.WaitHandle.WaitOne(100);
            }
            finally
            {
                cap.StopRecording();
            }
        }

        private static WaveFormat InitClient(Wasapi.IAudioClient client, Wasapi.WAVEFORMATEX fmt)        {
            int hr = client.Initialize(0 /* shared */, Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK,
                2_000_000, 0, ref fmt, IntPtr.Zero);
            if (hr == 0)
                return new WaveFormat(fmt);

            // shared mode rejected our format — use the endpoint mix format
            hr = client.GetMixFormat(out IntPtr mixPtr);
            if (hr != 0) throw Marshal.GetExceptionForHR(hr);
            try
            {
                var mix = Marshal.PtrToStructure<Wasapi.WAVEFORMATEX>(mixPtr);
                hr = client.Initialize(0, Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK,
                    2_000_000, 0, ref mix, IntPtr.Zero);
                if (hr != 0) throw Marshal.GetExceptionForHR(hr);
                return new WaveFormat(mix);
            }
            finally
            {
                Marshal.FreeHGlobal(mixPtr);
            }
        }

        private static class ComThread
        {
            [DllImport("ole32.dll")]
            private static extern int CoInitializeEx(IntPtr reserved, int coInit);
            [DllImport("ole32.dll")]
            private static extern void CoUninitialize();

            public static void Initialize()
            {
                int hr = CoInitializeEx(IntPtr.Zero, 0x0 /* COINIT_MULTITHREADED */);
                if (hr != 0 && hr != 1 /* S_FALSE */) throw Marshal.GetExceptionForHR(hr);
            }

            public static void Uninitialize() => CoUninitialize();
        }
    }
}

