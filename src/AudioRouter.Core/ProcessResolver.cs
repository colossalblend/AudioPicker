using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AudioRouter.Core.Interop;

namespace AudioRouter.Core
{
    public sealed record RunningProcess(uint Pid, string ExeName, string WindowTitle);

    public static class ProcessResolver
    {
        /// <summary>All processes grouped by exe name (one entry per name,
        /// preferring the instance that owns a window), audio-first sorting
        /// is applied by the caller.</summary>
        public static List<RunningProcess> EnumerateCandidates(
            IEnumerable<string> skipNames, string search)
        {
            var skip = new HashSet<string>(skipNames, StringComparer.OrdinalIgnoreCase);
            var byName = new Dictionary<string, RunningProcess>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var name = p.ProcessName;
                    if (skip.Contains(name)) continue;
                    var title = GetWindowTitle(p);
                    var key = name;
                    if (!byName.TryGetValue(key, out var cur) ||
                        (title is not null && cur.WindowTitle is null))
                        byName[key] = new RunningProcess((uint)p.Id, name, title);
                }
                catch { /* access denied etc. */ }
            }
            var items = byName.Values.ToList();
            if (!string.IsNullOrEmpty(search))
                items = items.Where(p =>
                    p.ExeName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (p.WindowTitle?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();
            return items.OrderBy(p => p.ExeName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Best pid to capture for an exe name: 1) the pid currently
        /// holding a loud audio session, 2) the family tree root, 3) any pid.
        /// Browsers spawn dozens of helpers and audio lives in one specific
        /// child; capturing the tree root covers it.</summary>
        public static uint? ResolvePid(string exeName)
        {
            var family = Process.GetProcesses()
                .Where(p => string.Equals(p.ProcessName, exeName, StringComparison.OrdinalIgnoreCase))
                .Select(p => (uint)p.Id)
                .ToHashSet();
            if (family.Count == 0) return null;

            float bestPeak = 0f;
            uint bestPid = 0;
            foreach (var (pid, peak) in Wasapi.GetSessionPeaks())
            {
                if (family.Contains(pid) && peak > bestPeak)
                {
                    bestPeak = peak;
                    bestPid = pid;
                }
            }
            if (bestPeak > 0f) return bestPid;

            var ppid = new Dictionary<uint, uint>();
            foreach (var p in Process.GetProcesses())
            {
                try { ppid[(uint)p.Id] = (uint)GetParentPid(p); }
                catch { }
            }
            var roots = family.Where(pid =>
                    !ppid.TryGetValue(pid, out var par) || !family.Contains(par))
                .ToList();
            if (roots.Count > 0) return roots[0];
            return family.First();
        }

        private static int GetParentPid(Process p) => (int)Native.GetParentProcessId(p.Id);

        private static class Native
        {
            public static uint GetParentProcessId(int pid)
            {
                // NT query: simplest portable way is Toolhelp32 snapshot
                var h = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
                if (h == IntPtr.Zero) return 0;
                try
                {
                    var pe = new PROCESSENTRY32 { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESSENTRY32>() };
                    if (!Process32First(h, ref pe)) return 0;
                    do
                    {
                        if (pe.th32ProcessID == (uint)pid)
                            return pe.th32ParentProcessID;
                    } while (Process32Next(h, ref pe));
                }
                finally { CloseHandle(h); }
                return 0;
            }

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool Process32First(IntPtr h, ref PROCESSENTRY32 lp);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool Process32Next(IntPtr h, ref PROCESSENTRY32 lp);

            [System.Runtime.InteropServices.DllImport("kernel32.dll")]
            private static extern bool CloseHandle(IntPtr h);

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            private struct PROCESSENTRY32
            {
                public uint dwSize;
                public uint cntUsage;
                public uint th32ProcessID;
                public IntPtr th32DefaultHeapID;
                public uint th32ModuleID;
                public uint cntThreads;
                public uint th32ParentProcessID;
                public int pcPriClassBase;
                public uint dwFlags;
                [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
                public string szExeFile;
            }
        }

        private static string GetWindowTitle(Process p)
        {
            if (p.MainWindowHandle == IntPtr.Zero) return null;
            var t = p.MainWindowTitle;
            return string.IsNullOrEmpty(t) ? null : t.Length > 45 ? t[..45] : t;
        }
    }
}
