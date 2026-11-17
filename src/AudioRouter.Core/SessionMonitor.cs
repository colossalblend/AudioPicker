using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AudioRouter.Core.Interop;

namespace AudioRouter.Core
{
    public sealed record SessionInfo(uint Pid, string Name, float Peak);

    /// <summary>Enumerates audio sessions and their current peaks. Aggregates
    /// by process name so it lines up with the deduped source list.</summary>
    public static class SessionMonitor
    {
        public static List<SessionInfo> ReadPeaks()
        {
            var peaks = Wasapi.GetSessionPeaks();
            var byName = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pid, peak) in peaks)
            {
                if (peak <= 0f) continue;
                string name;
                try { name = Process.GetProcessById((int)pid).ProcessName; }
                catch { continue; }
                if (!byName.TryGetValue(name, out var cur) || peak > cur)
                    byName[name] = peak;
            }
            return byName
                .Select(kv => new SessionInfo(0, kv.Key, kv.Value))
                .OrderByDescending(s => s.Peak)
                .ToList();
        }
    }
}
