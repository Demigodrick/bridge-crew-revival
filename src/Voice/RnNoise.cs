// Revival voice noise suppression: RNNoise (tools/rnnoise/build.sh) loaded from BepInEx/native/rnnoise.dll by
// full path, called through function pointers (the game's Unity runtime only finds [DllImport] libraries in
// its own folders), as Vosk is in VoiceCommands.
using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;

namespace StbcRevival
{
    // Single-thread ownership: create, Process and Dispose from one thread only (no locking).
    internal sealed class RnNoise : IDisposable
    {
        internal const int FrameSamples = 480; // 10 ms at 48 kHz

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr CreateFn(IntPtr model);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate float ProcessFn(IntPtr state, float[] output, float[] input);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DestroyFn(IntPtr state);

        static bool logged;

        readonly CreateFn create;
        readonly ProcessFn process;
        readonly DestroyFn destroy;
        readonly float[] input = new float[FrameSamples];  // scratch for the native call (int16-scaled)
        readonly float[] output = new float[FrameSamples];
        IntPtr state;

        RnNoise(CreateFn create, ProcessFn process, DestroyFn destroy)
        {
            this.create = create; this.process = process; this.destroy = destroy;
            state = create(IntPtr.Zero); // NULL model: the built-in one
            if (state == IntPtr.Zero) throw new InvalidOperationException("rnnoise_create returned NULL");
        }

        // null and a reason if the DLL, a symbol or the state can't be had. Logs the outcome once.
        internal static RnNoise TryCreate(out string why)
        {
            RnNoise result = null;
            why = null;
            try
            {
                var path = Path.Combine(Path.Combine(Paths.BepInExRootPath, "native"), "rnnoise.dll");
                if (!File.Exists(path)) throw new FileNotFoundException("BepInEx/native/rnnoise.dll is missing");
                var module = LoadLibraryW(path);
                if (module == IntPtr.Zero) throw new Exception("couldn't load rnnoise.dll (error " + Marshal.GetLastWin32Error() + ")");
                result = new RnNoise(Fn<CreateFn>(module, "rnnoise_create"),
                                     Fn<ProcessFn>(module, "rnnoise_process_frame"),
                                     Fn<DestroyFn>(module, "rnnoise_destroy"));
            }
            catch (Exception e)
            {
                why = e.Message;
            }
            if (!logged)
            {
                logged = true;
                Plugin.Log.LogInfo(result != null ? "Revival voice: noise suppression ready"
                                                  : "Revival voice: noise suppression off: " + why);
            }
            return result;
        }

        static T Fn<T>(IntPtr module, string name) where T : class
        {
            var p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException("rnnoise.dll has no " + name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        // Denoises frame[offset .. offset+480) in place (float PCM -1..1); returns the speech probability 0..1.
        internal float Process(float[] frame, int offset)
        {
            if (state == IntPtr.Zero) throw new ObjectDisposedException("RnNoise");
            if (frame == null || offset < 0 || offset + FrameSamples > frame.Length) throw new ArgumentException("frame too short");
            for (int i = 0; i < FrameSamples; i++) input[i] = frame[offset + i] * 32768f;
            float vad = process(state, output, input);
            for (int i = 0; i < FrameSamples; i++) frame[offset + i] = output[i] / 32768f;
            GC.KeepAlive(input); GC.KeepAlive(output); // pinned only for the call; keep them alive across it
            return vad;
        }

        public void Dispose()
        {
            if (state == IntPtr.Zero) return;
            destroy(state);
            state = IntPtr.Zero;
            GC.KeepAlive(this);
        }
    }
}
