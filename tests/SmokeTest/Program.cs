using AudioRouter.Core;

namespace SmokeTest
{
    // Headless E2E: `--play` mode emits a sine on the default render device;
    // default mode routes the whole system into "CABLE Input" and asserts the
    // tone can be heard looping back on "CABLE Output".
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--play")
            {
                PlaySine(float.Parse(args[1]), int.Parse(args[2]));
                return 0;
            }
            return Run();
        }

        private static void PlaySine(float freq, int seconds)
        {
            using var player = new NAudio.Wave.WasapiOut(
                NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 80);
            var wave = new SineWave(freq);
            player.Init(wave);
            player.Play();
            Thread.Sleep(seconds * 1000);
            player.Stop();
        }

        private static int Run()
        {
            Console.WriteLine("[smoke] spawning tone generator (440 Hz, 6 s)...");
            var self = Environment.ProcessPath!;
            var psi = new System.Diagnostics.ProcessStartInfo(self, "--play 440 6")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var tone = System.Diagnostics.Process.Start(psi)!;

            try
            {
                Console.WriteLine($"[smoke] routing tone pid {tone.Id} → CABLE Input ...");
                using var player = new OutputPlayer("CABLE Input", false);
                using var mixer = new MultiSourceMixer(
                    new[] { (AudioSource)new AudioSource.Process((uint)tone.Id) }, player);

                var rms = ListenOnCableOutput(TimeSpan.FromSeconds(4));

                Console.WriteLine($"[smoke] mixer level: {mixer.Level:0.0000}");
                Console.WriteLine($"[smoke] measured RMS on CABLE Output: {rms:0.0000}");
                if (rms > 0.02)
                {
                    Console.WriteLine("[smoke] PASS — tone reached the cable");
                    return 0;
                }
                Console.WriteLine("[smoke] FAIL — cable silent");
                return 1;
            }
            finally
            {
                try { tone.Kill(); } catch { }
            }
        }

        private static double ListenOnCableOutput(TimeSpan window)
        {
            var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var cable = devices.EnumerateAudioEndPoints(
                    NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains("CABLE Output",
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("CABLE Output device not found");

            double sum = 0;
            long samples = 0;
            var done = new ManualResetEventSlim(false);

            using var cap = new NAudio.CoreAudioApi.WasapiCapture(cable);
            cap.DataAvailable += (_, e) =>
            {
                var buf = e.Buffer;
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                {
                    short s = BitConverter.ToInt16(buf, i);
                    sum += (double)(s * s);
                    samples++;
                }
            };
            cap.RecordingStopped += (_, _) => done.Set();
            cap.StartRecording();
            Thread.Sleep(window);
            cap.StopRecording();
            done.Wait(2000);

            return samples > 0 ? Math.Sqrt(sum / samples) / 32768.0 : 0.0;
        }

        private sealed class SineWave : NAudio.Wave.WaveProvider32
        {
            private readonly float _freq;
            private long _n;

            public SineWave(float freq)
            {
                _freq = freq;
                SetWaveFormat(48000, 2);
            }

            public override int Read(float[] buffer, int offset, int count)
            {
                for (int i = 0; i < count; i += 2)
                {
                    float v = 0.3f * MathF.Sin(2 * MathF.PI * _freq * _n / 48000f);
                    buffer[offset + i] = v;
                    buffer[offset + i + 1] = v;
                    _n++;
                }
                return count;
            }
        }
    }
}
