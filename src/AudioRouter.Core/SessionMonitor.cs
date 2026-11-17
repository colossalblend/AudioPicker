using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;

namespace AudioRouter.Core
{
    public sealed record SessionInfo(uint Pid, string Name, float Peak);

    /// <summary>Enumerates audio sessions and their current peaks. Aggregates
    /// by process name so it lines up with the deduped source list. Built on
    /// NAudio to avoid RCW conflicts with the MMDeviceEnumerator singleton.</summary>
    public static class SessionMonitor
    {
        public static List<SessionInfo> ReadPeaks()
        {
            var byName = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(
                DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var mgr = dev.AudioSessionManager;
                    int count = mgr.Sessions.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var s = mgr.Sessions[i];
                        try
                        {
                            uint pid = s.GetProcessID;
                            if (pid == 0) continue;
                            float peak = 0f;
                            try { peak = s.AudioMeterInformation.MasterPeakValue; }
                            catch { }
                            string name;
                            try { name = Process.GetProcessById((int)pid).ProcessName; }
                            catch { continue; }
                            if (!byName.TryGetValue(name, out var cur) || peak > cur)
                                byName[name] = peak;
                        }
                        finally
                        {
                            try { s.Dispose(); } catch { }
                        }
                    }
                }
                catch { }
                finally
                {
                    try { dev.Dispose(); } catch { }
                }
            }
            return byName
                .Select(kv => new SessionInfo(0, kv.Key, kv.Value))
                .OrderByDescending(s => s.Peak)
                .ToList();
        }
    }
}
