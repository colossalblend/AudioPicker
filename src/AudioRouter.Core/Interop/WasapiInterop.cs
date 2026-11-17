using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace AudioRouter.Core.Interop
{
    internal static class Wasapi
    {
        public const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        public const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
        public const int PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1;
        public const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
        public const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";

        public const int eRender = 0;
        public const int eConsole = 0;
        public const int DEVICE_STATE_ACTIVE = 1;
        public const int CLSCTX_ALL = 0x17;
        public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        public static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A7B7F");

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;

            public static WAVEFORMATEX Float32(int rate = 48000, int channels = 2) => new()
            {
                wFormatTag = 3, // IEEE float
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)rate,
                wBitsPerSample = 32,
                nBlockAlign = (ushort)(channels * 4),
                nAvgBytesPerSec = (uint)(rate * channels * 4),
                cbSize = 0
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
        {
            public uint TargetProcessId;
            public int ProcessLoopbackMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AUDIOCLIENT_ACTIVATION_PARAMS
        {
            public int ActivationType;
            public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PropVariantBlob
        {
            public uint cbSize;
            public IntPtr pBlobData;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        internal struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public PropVariantBlob blob;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        internal class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B62"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDevice
        {
            [PreserveSig] int Activate(in Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration,
                long periodicity, ref WAVEFORMATEX format, IntPtr audioSessionGuid);
            [PreserveSig] int GetBufferSize(out uint numBufferFrames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, ref WAVEFORMATEX format, out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr mixFormat); // WAVEFORMATEX* (LocalAlloc)
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint framesToRead, out int flags,
                out long devicePosition, out long qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint framesRead);
            [PreserveSig] int GetNextPacketSize(out uint framesInNextPacket);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A7B7F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionManager2
        {
            // IAudioSessionManager
            [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr volume);
            // IAudioSessionManager2
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
            [PreserveSig] int RegisterSessionNotification(IntPtr notification);
            [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
            [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string appId, IntPtr duck);
            [PreserveSig] int UnregisterDuckNotification(IntPtr duck);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int sessionCount);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
        }

        [ComImport, Guid("F4B95A3C-9C07-49B0-A433-C21D30A8FBF8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionControl
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr ctx);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr ctx);
            [PreserveSig] int GetGroupingParam(out Guid param);
            [PreserveSig] int SetGroupingParam(ref Guid param, IntPtr ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        }

        [ComImport, Guid("bfb79588-b44a-4d6d-9b6a-f426cfe7f5ca"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr ctx);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr ctx);
            [PreserveSig] int GetGroupingParam(out Guid param);
            [PreserveSig] int SetGroupingParam(ref Guid param, IntPtr ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference(bool optOut);
        }

        [ComImport, Guid("C02216F6-5C67-43B8-8D52-94B0366C310C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
            [PreserveSig] int GetMeteringChannelCount(out int channels);
            [PreserveSig] int GetChannelsPeakValues(int count, [In, Out] float[] peaks);
            [PreserveSig] int QueryHardwareSupport(out int support);
        }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IActivateAudioInterfaceAsyncOperation
        {
            [PreserveSig] int GetActivateResult(out int activateResult, out IntPtr activatedInterface);
        }

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IActivateAudioInterfaceCompletionHandler
        {
            [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
        }

        [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAgileObject
        {
        }

        [DllImport("mmdevapi.dll", PreserveSig = true)]
        private static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            in Guid riid,
            IntPtr activationParams, // PROPVARIANT*
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        private sealed class CompletionHandler : StandardOleMarshalObject,
            IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEventSlim Completed = new(false);

            public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
            {
                Completed.Set();
                return 0;
            }
        }

        /// <summary>Activates a loopback IAudioClient scoped to a process tree.
        /// Must be called from an MTA thread; the completion handler is agile
        /// (StandardOleMarshalObject aggregates the FTM), otherwise the API
        /// returns E_ILLEGAL_METHOD_CALL.</summary>
        public static IAudioClient ActivateProcessLoopbackClient(uint targetPid, bool includeTree)
        {
            var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
            {
                ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
                ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
                {
                    TargetProcessId = targetPid,
                    ProcessLoopbackMode = includeTree
                        ? PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
                        : PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
                }
            };

            IntPtr paramsMem = Marshal.AllocHGlobal(Marshal.SizeOf(activationParams));
            IntPtr pvMem = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.StructureToPtr(activationParams, paramsMem, false);
                var pv = new PROPVARIANT
                {
                    vt = 65, // VT_BLOB
                    blob = new PropVariantBlob
                    {
                        cbSize = (uint)Marshal.SizeOf(activationParams),
                        pBlobData = paramsMem
                    }
                };
                Marshal.StructureToPtr(pv, pvMem, false);

                var handler = new CompletionHandler();
                int hr = ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                    in IID_IAudioClient, pvMem, handler, out var operation);
                if (hr != 0)
                    throw Marshal.GetExceptionForHR(hr);

                if (!handler.Completed.Wait(5000))
                    throw new TimeoutException("process loopback activation timed out");

                hr = operation.GetActivateResult(out int result, out IntPtr unk);
                if (hr != 0)
                    throw Marshal.GetExceptionForHR(hr);
                if (result != 0)
                    throw Marshal.GetExceptionForHR(result);

                try
                {
                    return (IAudioClient)Marshal.GetObjectForIUnknown(unk);
                }
                finally
                {
                    Marshal.Release(unk);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(paramsMem);
                Marshal.FreeHGlobal(pvMem);
            }
        }

        public static IMMDeviceEnumerator GetDeviceEnumerator()
        {
            return (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        }

        public static IMMDevice GetDefaultRenderDevice()
        {
            int hr = GetDeviceEnumerator().GetDefaultAudioEndpoint(eRender, eConsole, out var dev);
            if (hr != 0) throw Marshal.GetExceptionForHR(hr);
            return dev;
        }

        public static IAudioClient ActivateDeviceClient(IMMDevice device)
        {
            int hr = device.Activate(in IID_IAudioClient, CLSCTX_ALL, IntPtr.Zero, out object unk);
            if (hr != 0) throw Marshal.GetExceptionForHR(hr);
            return (IAudioClient)unk;
        }

        /// <summary>Every audio session on every active render endpoint:
        /// pid + current peak (0..1). Aggregation by name happens upstream.</summary>
        public static List<(uint Pid, float Peak)> GetSessionPeaks()
        {
            var result = new List<(uint, float)>();
            int hr = GetDeviceEnumerator().EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE,
                out var devices);
            if (hr != 0) return result;
            try
            {
                hr = devices.GetCount(out int count);
                if (hr != 0) return result;
                for (int i = 0; i < count; i++)
                {
                    IMMDevice dev;
                    try
                    {
                        if (devices.Item(i, out dev) != 0) continue;
                    }
                    catch { continue; }
                    IAudioSessionEnumerator se = null;
                    try
                    {
                        if (dev.Activate(in IID_IAudioSessionManager2, CLSCTX_ALL, IntPtr.Zero,
                                out object o) != 0) continue;
                        if (o is not IAudioSessionManager2 mgr) continue;
                        if (mgr.GetSessionEnumerator(out se) != 0) continue;
                        if (se.GetCount(out int n) != 0) continue;
                        for (int s = 0; s < n; s++)
                        {
                            if (se.GetSession(s, out var ctrl) != 0) continue;
                            try
                            {
                                if (ctrl is not IAudioSessionControl2 c2) continue;
                                if (c2.GetProcessId(out uint pid) != 0) continue;
                                float peak = 0f;
                                if (ctrl is IAudioMeterInformation meter)
                                {
                                    try { meter.GetPeakValue(out peak); } catch { }
                                }
                                result.Add((pid, peak));
                            }
                            finally { Marshal.ReleaseComObject(ctrl); }
                        }
                    }
                    catch { }
                    finally
                    {
                        if (se != null) Marshal.ReleaseComObject(se);
                        Marshal.ReleaseComObject(dev);
                    }
                }
            }
            finally { Marshal.ReleaseComObject(devices); }
            return result;
        }
    }
}

