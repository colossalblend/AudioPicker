using AudioRouter.Core;

namespace SmokeTest
{
    // Replicates the GUI StartStream path exactly: resolve pid by exe name,
    // OutputPlayer on CABLE Input, MultiSourceMixer, level check.
    internal static class StartPathTest
    {
        internal static int Run()
        {
            Console.WriteLine("[path] spawning tone generator (440 Hz, 6 s)...");
            var self = Environment.ProcessPath!;
            var psi = new System.Diagnostics.ProcessStartInfo(self, "--play 440 6")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var tone = System.Diagnostics.Process.Start(psi)!;
            Thread.Sleep(700);

            try
            {
                Console.WriteLine("[path] ResolvePid(\"SmokeTest\") ...");
                var resolved = ProcessResolver.ResolvePid("SmokeTest");
                Console.WriteLine($"[path] resolved pid: {resolved?.ToString() ?? "null"} (tone={tone.Id})");
                if (resolved is null) { Console.WriteLine("[path] FAIL: resolve returned null"); return 1; }

                var sources = new List<AudioSource> { new AudioSource.Process(resolved.Value) };
                var player = new OutputPlayer("CABLE Input (VB-Audio Virtual Cable)", false);
                var mixer = new MultiSourceMixer(sources, player) { Gain = 1f };

                Thread.Sleep(2500);
                Console.WriteLine($"[path] mixer level: {mixer.Level:0.0000}");
                Console.WriteLine($"[path] any dead: {mixer.AnyDead}");

                // measure on cable output
                double rms = 0;
                var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var cable = devices.EnumerateAudioEndPoints(
                        NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active)
                    .First(d => d.FriendlyName.Contains("CABLE Output"));
                double sum = 0; long samples = 0;
                using var cap = new NAudio.CoreAudioApi.WasapiCapture(cable);
                cap.DataAvailable += (_, e) =>
                {
                    for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                    {
                        short s = BitConverter.ToInt16(e.Buffer, i);
                        sum += (double)(s * s); samples++;
                    }
                };
                cap.StartRecording();
                Thread.Sleep(2000);
                cap.StopRecording();
                rms = samples > 0 ? Math.Sqrt(sum / samples) / 32768.0 : 0.0;
                Console.WriteLine($"[path] cable RMS: {rms:0.0000}");

                mixer.Dispose(); player.Dispose();

                if (rms > 0.02) { Console.WriteLine("[path] PASS"); return 0; }
                Console.WriteLine("[path] FAIL"); return 1;
            }
            finally
            {
                try { tone.Kill(); } catch { }
            }
        }
    }
}
