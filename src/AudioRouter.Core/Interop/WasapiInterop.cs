using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AudioRouter.Core.Interop
{
    /// <summary>Raw WASAPI process-loopback activation. Only what NAudio does
    /// NOT provide: ActivateAudioInterfaceAsync with a target process tree.
    /// Everything device/session related lives in NAudio (MMDeviceEnumerator
    /// is a per-process singleton — mixing RCW types for it breaks QI).</summary>
    internal static class Wasapi
    {
        public const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        public const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
        public const int PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1;
        public const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
        public const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";

        public const int CLSCTX_ALL = 0x17;
        public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

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
    }
}
