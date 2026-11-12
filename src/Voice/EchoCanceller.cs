// Revival voice echo cancellation: speexdsp (tools/speexdsp/build.sh) loaded from BepInEx/native/speexdsp.dll by
// full path and called through function pointers, as RNNoise is. Speex's echo canceller (MDF) learns how each
// crewmate's voice gets from this computer's speakers back into its microphone, one channel each, and subtracts it.
// In a simulated room (three crewmates taking turns, echo 70-160 ms late) it took out about 21 dB once settled
// (a minute or so of them talking), against 10 dB with their voices mixed into one channel, and left the user's
// own voice as it was. Speex's residual echo suppressor took 2 dB more but bent the user's voice while they talked
// over a crewmate, so it isn't used; RNNoise comes next anyway.
using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using StbcRevival.Voice;

namespace StbcRevival
{
    // Single-thread ownership: create, Process and Dispose from one thread only (the capture worker).
    internal sealed class EchoCanceller : IDisposable
    {
        internal const int FrameSamples = 480;       // 10 ms at 48 kHz
        internal const int TailMs = 320;             // the longest speaker-to-microphone delay learnt, lead included

        const int EchoSetSamplingRate = 24;          // SPEEX_ECHO_SET_SAMPLING_RATE

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr EchoInitFn(int frameSize, int filterLength, int mics, int speakers);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void EchoCancelFn(IntPtr st, short[] rec, short[] play, short[] output);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CtlIntFn(IntPtr st, int request, ref int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DestroyFn(IntPtr st);

        sealed class Api
        {
            public EchoInitFn EchoInit;
            public EchoCancelFn EchoCancel;
            public CtlIntFn EchoCtl;
            public DestroyFn EchoDestroy;
        }

        static Api api;
        static string loadError;
        static bool logged;

        readonly Api fn;
        readonly int channels;
        readonly short[] mic = new short[FrameSamples], output = new short[FrameSamples];
        IntPtr echo;

        // How much the canceller takes out while crewmates are heard, for the 30 s report (lock stats: read from
        // the main thread).
        readonly object stats = new object();
        double inEnergy, outEnergy;

        EchoCanceller(Api fn, int channels)
        {
            this.fn = fn;
            this.channels = channels;
            int filter = TailMs * EchoReference.SamplesPerMs;
            echo = fn.EchoInit(FrameSamples, filter, 1, channels);
            if (echo == IntPtr.Zero) throw new InvalidOperationException("speex_echo_state_init_mc returned NULL");
            int rate = VoiceFormat.SampleRate;
            fn.EchoCtl(echo, EchoSetSamplingRate, ref rate);
        }

        internal int Channels => channels;

        // null and a reason if the DLL, a symbol or the state can't be had. Logs the outcome once.
        internal static EchoCanceller TryCreate(int channels, out string why)
        {
            EchoCanceller result = null;
            why = null;
            try
            {
                if (api == null && loadError == null) Load();
                if (api == null) throw new Exception(loadError);
                result = new EchoCanceller(api, channels);
            }
            catch (Exception e)
            {
                why = e.Message;
            }
            if (!logged)
            {
                logged = true;
                Plugin.Log.LogInfo(result != null ? "Revival voice: echo cancellation ready"
                                                  : "Revival voice: echo cancellation off: " + why);
            }
            return result;
        }

        static void Load()
        {
            try
            {
                var path = Path.Combine(Path.Combine(Paths.BepInExRootPath, "native"), "speexdsp.dll");
                if (!File.Exists(path)) throw new FileNotFoundException("BepInEx/native/speexdsp.dll is missing");
                var module = LoadLibraryW(path);
                if (module == IntPtr.Zero) throw new Exception("couldn't load speexdsp.dll (error " + Marshal.GetLastWin32Error() + ")");
                api = new Api
                {
                    EchoInit = Fn<EchoInitFn>(module, "speex_echo_state_init_mc"),
                    EchoCancel = Fn<EchoCancelFn>(module, "speex_echo_cancellation"),
                    EchoCtl = Fn<CtlIntFn>(module, "speex_echo_ctl"),
                    EchoDestroy = Fn<DestroyFn>(module, "speex_echo_state_destroy"),
                };
            }
            catch (Exception e)
            {
                loadError = e.Message; // not retried: the DLL won't appear mid-game
            }
        }

        static T Fn<T>(IntPtr module, string name) where T : class
        {
            var p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException("speexdsp.dll has no " + name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        /// <summary>Takes the echo out of frame[offset .. offset+480) in place (float PCM -1..1). reference: what
        /// played meanwhile, 480 x Channels interleaved 16-bit (EchoReference.Read); active: anything was in it.</summary>
        internal void Process(float[] frame, int offset, short[] reference, bool active)
        {
            if (echo == IntPtr.Zero) throw new ObjectDisposedException("EchoCanceller");
            if (frame == null || offset < 0 || offset + FrameSamples > frame.Length) throw new ArgumentException("frame too short");
            if (reference == null || reference.Length < FrameSamples * channels) throw new ArgumentException("reference too short");
            double before = 0, after = 0;
            for (int i = 0; i < FrameSamples; i++)
            {
                float s = frame[offset + i] * 32767f;
                mic[i] = (short)(s > 32767f ? 32767 : s < -32768f ? -32768 : (int)s);
                before += (double)mic[i] * mic[i];
            }
            fn.EchoCancel(echo, mic, reference, output);
            for (int i = 0; i < FrameSamples; i++)
            {
                frame[offset + i] = output[i] / 32768f;
                after += (double)output[i] * output[i];
            }
            if (active)
                lock (stats)
                {
                    inEnergy += before;
                    outEnergy += after;
                }
            GC.KeepAlive(mic); GC.KeepAlive(output); GC.KeepAlive(reference);
        }

        /// <summary>How much quieter the microphone came out while crewmates' voices were playing, since the last
        /// call (dB; NaN if none played). Echo taken out, but the user talking over them counts too. Any thread.</summary>
        internal double TakeReductionDb()
        {
            lock (stats)
            {
                double r = inEnergy > 0 && outEnergy > 0 ? 10 * Math.Log10(inEnergy / outEnergy) : double.NaN;
                inEnergy = outEnergy = 0;
                return r;
            }
        }

        public void Dispose()
        {
            if (echo != IntPtr.Zero) { fn.EchoDestroy(echo); echo = IntPtr.Zero; }
            GC.KeepAlive(this);
        }
    }
}
