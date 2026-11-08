#nullable disable
// Revival voice Opus wrapper over libopus (opus.dll in the game, libopus.so in the tests). Float PCM, one 20 ms frame per call.
using System;
using System.Runtime.InteropServices;

namespace StbcRevival.Voice
{
    public interface IFrameDecoder
    {
        int Decode(byte[] packet, int length, float[] pcm, bool fec); // samples written (FrameSamples), <0 error
        int Conceal(float[] pcm);                                     // PLC, samples written
    }

    static class OpusNative
    {
        const string Lib = "opus";
        public const int ApplicationVoip = 2048;
        public const int SetBitrate = 4002, SetVbr = 4006, SetComplexity = 4010, SetInbandFec = 4012,
                         SetPacketLossPerc = 4014, GetPacketLossPerc = 4015, SetDtx = 4016, SetSignal = 4024;
        public const int SignalVoice = 3001;

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr opus_encoder_create(int fs, int channels, int application, out int error);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void opus_encoder_destroy(IntPtr st);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_encode_float(IntPtr st, float[] pcm, int frameSize, byte[] data, int maxBytes);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_encoder_ctl(IntPtr st, int request, int value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_encoder_ctl(IntPtr st, int request, out int value);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr opus_decoder_create(int fs, int channels, out int error);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void opus_decoder_destroy(IntPtr st);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_decode_float(IntPtr st, byte[] data, int len, float[] pcm, int frameSize, int decodeFec);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int opus_decode_float(IntPtr st, IntPtr data, int len, float[] pcm, int frameSize, int decodeFec);
    }

    // Single-thread ownership: use and Dispose from one thread only (no locking).
    public sealed class OpusEncoder : IDisposable
    {
        IntPtr st;
        readonly int frameSamples;
        float[] scratch;

        public OpusEncoder(int sampleRate, int bitrateKbps)
        {
            int err;
            st = OpusNative.opus_encoder_create(sampleRate, 1, OpusNative.ApplicationVoip, out err);
            if (st == IntPtr.Zero || err != 0) { st = IntPtr.Zero; throw new InvalidOperationException("opus_encoder_create failed: " + err); }
            frameSamples = sampleRate / 50;
            OpusNative.opus_encoder_ctl(st, OpusNative.SetSignal, OpusNative.SignalVoice);
            OpusNative.opus_encoder_ctl(st, OpusNative.SetBitrate, bitrateKbps * 1000);
            OpusNative.opus_encoder_ctl(st, OpusNative.SetVbr, 1);
            OpusNative.opus_encoder_ctl(st, OpusNative.SetDtx, 0);
            OpusNative.opus_encoder_ctl(st, OpusNative.SetInbandFec, 1);
            OpusNative.opus_encoder_ctl(st, OpusNative.SetComplexity, 8);
            PacketLossPercent = 10;
            GC.KeepAlive(this);
        }

        ~OpusEncoder() { Free(); }
        public void Dispose() { Free(); GC.SuppressFinalize(this); }
        void Free()
        {
            IntPtr p = st; st = IntPtr.Zero;
            if (p != IntPtr.Zero) OpusNative.opus_encoder_destroy(p);
        }

        // Clamped 5..30.
        public int PacketLossPercent
        {
            set
            {
                if (st == IntPtr.Zero) throw new ObjectDisposedException("OpusEncoder");
                int v = value < 5 ? 5 : value > 30 ? 30 : value;
                OpusNative.opus_encoder_ctl(st, OpusNative.SetPacketLossPerc, v);
                GC.KeepAlive(this);
            }
        }

        // What libopus holds now (for tests and diagnostics).
        public int PacketLossPercentRead
        {
            get
            {
                if (st == IntPtr.Zero) throw new ObjectDisposedException("OpusEncoder");
                int v;
                OpusNative.opus_encoder_ctl(st, OpusNative.GetPacketLossPerc, out v);
                GC.KeepAlive(this);
                return v;
            }
        }

        // Encodes one frame starting at pcm[offset]; returns bytes written, <0 on error.
        public int Encode(float[] pcm, int offset, byte[] output)
        {
            if (st == IntPtr.Zero) throw new ObjectDisposedException("OpusEncoder");
            float[] src = pcm;
            if (offset != 0)
            {
                if (scratch == null) scratch = new float[frameSamples];
                Array.Copy(pcm, offset, scratch, 0, frameSamples);
                src = scratch;
            }
            int n = OpusNative.opus_encode_float(st, src, frameSamples, output, output.Length);
            GC.KeepAlive(this); // the finalizer must not destroy the handle mid-call
            return n;
        }
    }

    // Single-thread ownership: use and Dispose from one thread only (no locking).
    public sealed class OpusDecoder : IFrameDecoder, IDisposable
    {
        IntPtr st;
        readonly int frameSamples;

        public OpusDecoder(int sampleRate)
        {
            int err;
            st = OpusNative.opus_decoder_create(sampleRate, 1, out err);
            if (st == IntPtr.Zero || err != 0) { st = IntPtr.Zero; throw new InvalidOperationException("opus_decoder_create failed: " + err); }
            frameSamples = sampleRate / 50;
        }

        ~OpusDecoder() { Free(); }
        public void Dispose() { Free(); GC.SuppressFinalize(this); }
        void Free()
        {
            IntPtr p = st; st = IntPtr.Zero;
            if (p != IntPtr.Zero) OpusNative.opus_decoder_destroy(p);
        }

        // With fec, reconstructs the frame BEFORE this packet from its redundancy.
        public int Decode(byte[] packet, int length, float[] pcm, bool fec)
        {
            if (st == IntPtr.Zero) throw new ObjectDisposedException("OpusDecoder");
            int n = OpusNative.opus_decode_float(st, packet, length, pcm, frameSamples, fec ? 1 : 0);
            GC.KeepAlive(this);
            return n;
        }

        public int Conceal(float[] pcm)
        {
            if (st == IntPtr.Zero) throw new ObjectDisposedException("OpusDecoder");
            int n = OpusNative.opus_decode_float(st, IntPtr.Zero, 0, pcm, frameSamples, 0);
            GC.KeepAlive(this);
            return n;
        }
    }
}
