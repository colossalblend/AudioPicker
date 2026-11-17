using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioRouter.Core
{
    /// <summary>Plays the mixed stream into one output device, with an optional
    /// self-monitor player on the default device. Feeds both from one
    /// BufferedWaveProvider clone; WASAPI shared mode does any resampling.</summary>
    public sealed class OutputPlayer : IAudioSink, IDisposable
    {
        private const int Rate = 48000;
        private static readonly WaveFormat Format =
            WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

        private readonly BufferedWaveProvider _provider;
        private WasapiOut _out;
        private WasapiOut _monitorOut;
        private readonly BufferedWaveProvider _monitorProvider;
        private readonly MMDevice _device;
        private readonly MMDevice _defaultDevice;

        public OutputPlayer(string outputDeviceName, bool monitor)
        {
            var enumerator = new MMDeviceEnumerator();
            _device = FindDevice(enumerator, outputDeviceName);
            _defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);

            _provider = new BufferedWaveProvider(Format)
            {
                BufferDuration = TimeSpan.FromSeconds(4),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            _monitorProvider = new BufferedWaveProvider(Format)
            {
                BufferDuration = TimeSpan.FromSeconds(4),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            _out = new WasapiOut(_device, AudioClientShareMode.Shared, true, 80);
            _out.Init(_provider);
            _out.Play();

            if (monitor)
                EnableMonitor();
        }

        private static MMDevice FindDevice(MMDeviceEnumerator en, string name)
        {
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                if (name != null && d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return d;
            // auto: VB-Cable input
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                if (d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                    return d;
            return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        }

        public void EnableMonitor()
        {
            if (_monitorOut != null) return;
            _monitorOut = new WasapiOut(_defaultDevice, AudioClientShareMode.Shared, true, 80);
            _monitorOut.Init(_monitorProvider);
            _monitorOut.Play();
        }

        public void DisableMonitor()
        {
            _monitorOut?.Stop();
            _monitorOut?.Dispose();
            _monitorOut = null;
        }

        public bool MonitorEnabled => _monitorOut != null;

        public void Push(ReadOnlySpan<float> frames)
        {
            var bytes = new byte[frames.Length * sizeof(float)];
            if (frames.Length > 0 && System.Runtime.InteropServices.MemoryMarshal.TryGetArray(
                    frames.ToArray().AsMemory(), out ArraySegment<float> segment))
            {
                Buffer.BlockCopy(segment.Array, segment.Offset * sizeof(float), bytes, 0, bytes.Length);
            }
            else
            {
                for (int i = 0; i < frames.Length; i++)
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), frames[i]);
            }
            _provider.AddSamples(bytes, 0, bytes.Length);
            if (_monitorOut != null)
                _monitorProvider.AddSamples(bytes, 0, bytes.Length);
        }

        public void Dispose()
        {
            DisableMonitor();
            _out?.Stop();
            _out?.Dispose();
            _out = null;
        }
    }
}
