// Revival voice capture: voice chat's own microphone recording (MicTap) -> noise suppression (RNNoise, or the
// energy detector without it) -> automatic gain -> speech gate -> Opus -> VoiceWire packets, queued for the
// sender (TryTakePacket). The main thread copies the samples out of the clip (Unity's API is main-thread only);
// a worker thread does everything else and owns RNNoise, the encoder and the loopback decoder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BepInEx;
using HarmonyLib;
using StbcRevival.Voice;
using UnityEngine;

namespace StbcRevival
{
    internal static class VoiceCapture
    {
        /// <summary>Have voice chat record the microphone at 48 kHz (GetInputSampleRate), so no resampling is
        /// needed. If the clip still comes out at another rate, the worker resamples it.</summary>
        internal const bool RaiseMicRate = true;

        private const int MaxQueuedPackets = 50;   // 1 s of speech: if nobody takes them, the oldest go
        private const float RetryAfterFailure = 30f;

        private static readonly object packetLock = new object();
        private static readonly Queue<byte[]> packets = new Queue<byte[]>();
        private static int droppedPackets;

        // Main thread.
        private static MicTap.Reader reader;
        private static Worker worker;
        private static DaikonForge.VoIP.AudioInputDeviceBase localInput;
        private static readonly List<float> tapped = new List<float>(4096);
        private static readonly System.Random random = new System.Random();
        private static float retryAt;
        private static uint stream;

        // Shared with the worker.
        private static volatile bool talking;
        private static volatile int lossPercent = -1; // the listeners' latest, -1 until one reports
        private static volatile float gainDb;
        private static volatile bool noiseSuppressed;
        private static int speechFrames; // Interlocked: frames the gate let through since the last Describe
        private static int loopbackWritten; // once per run
        private static volatile bool failureLogged;

        internal static bool Talking => talking;
        internal static uint Stream => stream;

        /// <summary>Main thread: the capture pipeline is running (DirectVoice's hello tells crewmates so).</summary>
        internal static bool Capturing => worker != null;

        internal static int PacketLossPercent { set { lossPercent = value; } }

        internal static void Patch(Harmony harmony)
        {
            var isTalking = AccessTools.PropertyGetter(typeof(DaikonForge.VoIP.AudioInputDeviceBase),
                                                       nameof(DaikonForge.VoIP.AudioInputDeviceBase.BBDPHCJHMEG)); // IsTalking
            if (isTalking == null) Plugin.Log.LogWarning("Revival voice: AudioInputDeviceBase.IsTalking wasn't found; the talking indicator stays the game's");
            else harmony.Patch(isTalking, postfix: new HarmonyMethod(typeof(VoiceCapture), nameof(IsTalking_Postfix)));

            if (RaiseMicRate)
            {
                var rate = AccessTools.Method(typeof(DaikonForge.VoIP.NJPHCGOEOPO), nameof(DaikonForge.VoIP.NJPHCGOEOPO.GetInputSampleRate));
                if (rate == null) Plugin.Log.LogWarning("Revival voice: GetInputSampleRate wasn't found; the microphone is resampled to 48 kHz");
                else harmony.Patch(rate, postfix: new HarmonyMethod(typeof(VoiceCapture), nameof(InputSampleRate_Postfix)));
            }
        }

        // The local microphone's IsTalking (VoIpManager.LocalPlayerIsTalking: the talking indicator) follows the
        // speech gate while Revival voice is capturing.
        private static void IsTalking_Postfix(DaikonForge.VoIP.AudioInputDeviceBase __instance, ref bool __result)
        {
            if (worker != null && localInput != null && ReferenceEquals(__instance, localInput)) __result = talking;
        }

        // The rate voice chat starts its microphone recording at (UnityMicrophoneInputDevice and
        // WwiseMicrophoneInputDevice clamp it to what the device supports, and resample to their codec's rate).
        // Only for a microphone that can record at 48 kHz: the devices keep the rate they asked for and hand it to
        // their codec's resampler, while Microphone.Start clamps the clip to the device's range, so asking a 44.1 kHz
        // or 16 kHz mic for 48 kHz would pitch-shift game voice (still what players without the mod hear).
        private static void InputSampleRate_Postfix(ref int __result)
        {
            if (Plugin.RevivalVoiceActive && Supports48k()) __result = VoiceFormat.SampleRate;
        }

        private static readonly Dictionary<string, bool> supports48k = new Dictionary<string, bool>(); // by device name

        private static bool Supports48k()
        {
            try
            {
                var input = MicTap.ChatInput();
                if (input == null || !MicTap.ChatDevice(input, out var device)) return false;
                var key = device ?? "";
                if (supports48k.TryGetValue(key, out bool ok)) return ok;
                Microphone.GetDeviceCaps(device, out int min, out int max);
                ok = max == 0 || (min <= VoiceFormat.SampleRate && VoiceFormat.SampleRate <= max);
                supports48k[key] = ok;
                Plugin.Log.LogInfo($"Revival voice: microphone '{(key.Length == 0 ? "default" : key)}' records at {min}-{max} Hz; "
                                   + (ok ? "voice chat records it at 48 kHz" : "voice chat keeps its rate (resampled to 48 kHz)"));
                return ok;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Revival voice: microphone rates unknown, voice chat keeps its rate: " + e.Message);
                return false;
            }
        }

        /// <summary>Main thread, every frame.</summary>
        internal static void Update()
        {
            if (!Plugin.RevivalVoiceActive)
            {
                if (worker != null) Stop("Revival voice is off");
                lock (packetLock) packets.Clear();
                localInput = null;
                return;
            }
            localInput = MicTap.ChatInput();
            if (!MicTap.TryFind(out var device, out var clip))
            {
                if (worker != null) Stop("voice chat isn't recording the microphone");
                return;
            }
            if (worker != null && (worker.Failed || !ReferenceEquals(reader.Clip, clip) || reader.Device != device))
            {
                if (worker.Failed) retryAt = Time.realtimeSinceStartup + RetryAfterFailure;
                Stop(worker.Failed ? "it failed" : "voice chat's recording changed");
            }
            if (worker == null)
            {
                if (Time.realtimeSinceStartup < retryAt) return;
                Start(device, clip);
            }
            tapped.Clear();
            if (reader.Read(tapped) > 0) worker.Feed(tapped);
        }

        private static void Start(string device, AudioClip clip)
        {
            reader = new MicTap.Reader(device, clip);
            uint next;
            do next = (uint)random.Next() ^ ((uint)random.Next() << 16); while (next == 0 || next == stream);
            stream = next;
            bool loopback = Plugin.VoiceDebugLoopback.Value && Interlocked.CompareExchange(ref loopbackWritten, 0, 0) == 0;
            worker = new Worker(stream, reader.Frequency, Plugin.VoiceNoiseSuppression.Value, Plugin.VoiceAutoGain.Value,
                                Mathf.Clamp(Plugin.VoiceBitrate.Value, 24, 96),
                                loopback ? Path.Combine(Paths.BepInExRootPath, "voice-loopback.wav") : null);
            Plugin.Log.LogInfo($"Revival voice: capturing '{(string.IsNullOrEmpty(device) ? "default" : device)}' at {reader.Frequency} Hz"
                               + $"{(reader.Frequency != VoiceFormat.SampleRate ? " (resampled to 48 kHz)" : "")}, stream {stream:x8}");
        }

        // Stops and joins the worker (it disposes RNNoise, the encoder and the decoder itself, on its way out).
        private static void Stop(string why)
        {
            var w = worker;
            worker = null;
            reader = null;
            if (w == null) return;
            if (!w.Stop()) Plugin.Log.LogWarning("Revival voice: the capture worker didn't stop in time; it finishes on its own");
            talking = false;
            Plugin.Log.LogInfo($"Revival voice: capture stopped ({why})");
        }

        /// <summary>The next packet to send (VoiceWire voice packets, in order), from any thread.</summary>
        internal static bool TryTakePacket(out byte[] wire, out int length)
        {
            lock (packetLock)
            {
                if (packets.Count == 0)
                {
                    wire = null;
                    length = 0;
                    return false;
                }
                wire = packets.Dequeue();
                length = wire.Length;
                return true;
            }
        }

        private static void Enqueue(byte[] wire)
        {
            lock (packetLock)
            {
                while (packets.Count >= MaxQueuedPackets)
                {
                    packets.Dequeue();
                    droppedPackets++;
                }
                packets.Enqueue(wire);
            }
        }

        /// <summary>For the 30 s report: speech sent since the last call, the gain, noise suppression.</summary>
        internal static string Describe()
        {
            float speech = Interlocked.Exchange(ref speechFrames, 0) * VoiceFormat.FrameMs / 1000f;
            int dropped;
            lock (packetLock) { dropped = droppedPackets; droppedPackets = 0; }
            bool on = worker != null;
            return $"capture {(on ? "on" : "off")}, {speech:0.0} s of speech sent, gain {gainDb:+0.0;-0.0;0.0} dB, "
                   + $"noise suppression {(noiseSuppressed ? "on" : "off")}"
                   + (dropped > 0 ? $", {dropped} packets not taken" : "");
        }

        // ---- The worker ---------------------------------------------------------------------------------------

        private sealed class Worker
        {
            private const int Ring = 4;              // the last encoded frames: up to 3 of pre-roll and the current one
            private const int MaxOpus = 1275;        // the largest Opus frame
            private const int LoopbackSamples = 10 * VoiceFormat.SampleRate;

            private readonly Thread thread;
            private readonly AutoResetEvent wake = new AutoResetEvent(false);
            private volatile bool stopping;
            internal volatile bool Failed;

            // From the main thread: samples at the clip's rate, waiting for the worker (at most 1 s; beyond that
            // the oldest are skipped, and the frame counter still moves on by their length).
            private readonly object inputLock = new object();
            private float[] pending;
            private int pendingCount, skipped;

            private readonly uint stream;
            private readonly int rate;
            private readonly bool suppress, autoGain;
            private readonly int bitrate;
            private readonly string loopbackPath;

            internal Worker(uint stream, int rate, bool suppress, bool autoGain, int bitrate, string loopbackPath)
            {
                this.stream = stream;
                this.rate = rate;
                this.suppress = suppress;
                this.autoGain = autoGain;
                this.bitrate = bitrate;
                this.loopbackPath = loopbackPath;
                pending = new float[Math.Max(rate / 10, 1024)];
                thread = new Thread(Run) { IsBackground = true, Name = "Revival voice capture" };
                thread.Start();
            }

            internal void Feed(List<float> samples)
            {
                lock (inputLock)
                {
                    int limit = Math.Max(rate, 1024);
                    int count = samples.Count;
                    if (pendingCount + count > limit)
                    {
                        int drop = Math.Min(pendingCount, pendingCount + count - limit);
                        Array.Copy(pending, drop, pending, 0, pendingCount - drop);
                        pendingCount -= drop;
                        skipped += drop;
                        if (count > limit) { skipped += count - limit; count = limit; }
                    }
                    if (pending.Length < pendingCount + count) Array.Resize(ref pending, Math.Min(limit, Math.Max(pending.Length * 2, pendingCount + count)));
                    int from = samples.Count - count;
                    for (int i = 0; i < count; i++) pending[pendingCount + i] = samples[from + i];
                    pendingCount += count;
                }
                wake.Set();
            }

            // True if the worker finished within the wait.
            internal bool Stop()
            {
                stopping = true;
                wake.Set();
                return thread.Join(2000);
            }

            // ---- Worker thread only below ----

            private RnNoise noise;
            private OpusEncoder encoder;
            private Resampler resampler;
            private Agc agc;
            private readonly SpeechGate gate = new SpeechGate();
            private float[] input = new float[0], resampled = new float[0];
            private readonly float[] frame = new float[VoiceFormat.FrameSamples];
            private int frameFill;
            private uint seq;          // every 20 ms of microphone time, sent or not
            private long encoded;      // frames in the ring so far
            private readonly byte[][] ring = new byte[Ring][];
            private readonly int[] ringLength = new int[Ring];
            private int lossApplied = -1;
            private bool encodeWarned;

            private OpusDecoder loopback;
            private VoicePacketIn loopbackPacket;
            private float[] loopbackPcm;
            private int loopbackFill;
            private readonly float[] decoded = new float[VoiceFormat.FrameSamples];

            private void Run()
            {
                try
                {
                    if (suppress)
                    {
                        noise = RnNoise.TryCreate(out _); // logs once why not
                    }
                    noiseSuppressed = noise != null;
                    encoder = new OpusEncoder(VoiceFormat.SampleRate, bitrate);
                    if (autoGain) agc = new Agc();
                    gainDb = 0f;
                    if (rate != VoiceFormat.SampleRate) resampler = new Resampler(rate, VoiceFormat.SampleRate);
                    for (int i = 0; i < Ring; i++) ring[i] = new byte[MaxOpus];
                    if (loopbackPath != null)
                    {
                        loopback = new OpusDecoder(VoiceFormat.SampleRate);
                        loopbackPacket = new VoicePacketIn();
                        loopbackPcm = new float[LoopbackSamples];
                    }
                    while (!stopping)
                    {
                        wake.WaitOne(100);
                        if (stopping) break;
                        int count, skip;
                        lock (inputLock)
                        {
                            count = pendingCount;
                            skip = skipped;
                            skipped = 0;
                            if (input.Length < count) input = new float[Math.Max(count, input.Length * 2)];
                            Array.Copy(pending, input, count);
                            pendingCount = 0;
                        }
                        if (skip > 0) Skip(skip);
                        if (count > 0) Process(input, count);
                    }
                }
                catch (Exception e)
                {
                    Failed = true;
                    if (failureLogged) Plugin.Log.LogWarning("Revival voice: capture failed again: " + e.Message);
                    else Plugin.Log.LogError("Revival voice: capture failed (retried every 30 s): " + e);
                    failureLogged = true;
                }
                finally
                {
                    talking = false;
                    WriteLoopback(loopbackFill >= LoopbackSamples); // not full yet: the next capture records again
                    if (loopback != null) loopback.Dispose();
                    if (encoder != null) encoder.Dispose();
                    if (noise != null) noise.Dispose();
                }
            }

            // Samples the worker fell too far behind on: they're lost, but the time they cover still counts.
            private void Skip(int samples)
            {
                long at48 = (long)samples * VoiceFormat.SampleRate / rate;
                seq += (uint)(at48 / VoiceFormat.FrameSamples);
                frameFill = 0;
                encoded = 0; // the ring's frames aren't contiguous with what follows: no pre-roll or prev from them
                Plugin.Log.LogWarning($"Revival voice: capture fell behind; skipped {at48 * 1000 / VoiceFormat.SampleRate} ms of microphone");
            }

            private void Process(float[] samples, int count)
            {
                float[] source = samples;
                int n = count;
                if (resampler != null)
                {
                    int room = (int)((long)count * VoiceFormat.SampleRate / rate) + 64;
                    if (resampled.Length < room) resampled = new float[room];
                    n = resampler.Process(samples, count, resampled);
                    source = resampled;
                }
                for (int i = 0; i < n;)
                {
                    int take = Math.Min(n - i, VoiceFormat.FrameSamples - frameFill);
                    Array.Copy(source, i, frame, frameFill, take);
                    frameFill += take;
                    i += take;
                    if (frameFill == VoiceFormat.FrameSamples)
                    {
                        frameFill = 0;
                        Frame();
                    }
                }
            }

            // One 20 ms frame: two 10 ms halves through RNNoise (or the energy detector), gain, encode, gate.
            private void Frame()
            {
                const int half = VoiceFormat.NoiseFrameSamples;
                float first, second;
                if (noise != null)
                {
                    first = noise.Process(frame, 0);
                    second = noise.Process(frame, half);
                }
                else
                {
                    first = EnergyVad.Probability(frame, 0, half);
                    second = EnergyVad.Probability(frame, half, half);
                }
                float speech = Math.Max(first, second);
                if (agc != null)
                {
                    agc.Process(frame, 0, VoiceFormat.FrameSamples, gate.Open || speech >= 0.6f);
                    gainDb = agc.GainDb;
                }

                int loss = lossPercent;
                if (loss >= 0 && loss != lossApplied)
                {
                    encoder.PacketLossPercent = loss; // clamped 5..30 by the encoder
                    lossApplied = loss;
                }

                uint frameSeq = seq++;
                int slot = (int)(encoded % Ring);
                int length = encoder.Encode(frame, 0, ring[slot]);
                if (length < 0)
                {
                    if (!encodeWarned) Plugin.Log.LogWarning("Revival voice: Opus couldn't encode a frame (error " + length + ")");
                    encodeWarned = true;
                    length = 0;
                }
                ringLength[slot] = length;
                encoded++;

                int send = gate.Step(speech, out bool burstStart);
                bool wasTalking = talking;
                talking = gate.Open;
                if (send <= 0)
                {
                    // Save the loopback so far at the end of each sentence, so quitting the game doesn't lose it.
                    if (wasTalking && loopback != null) WriteLoopback(false);
                    return;
                }
                Interlocked.Increment(ref speechFrames);
                send = (int)Math.Min(send, Math.Min(encoded, Ring));
                for (int k = send - 1; k >= 0; k--)
                {
                    long index = encoded - 1 - k;
                    int at = (int)(index % Ring);
                    if (ringLength[at] == 0) continue; // an encode failure: nothing to send for it
                    byte[] prev = null;
                    int prevLength = 0;
                    if (k + 1 < Ring && index > 0) // the frame before it is still in the ring
                    {
                        int before = (int)((index - 1) % Ring);
                        prev = ring[before];
                        prevLength = ringLength[before];
                        if (prevLength == 0) prev = null;
                    }
                    var wire = new byte[13 + ringLength[at] + 2 + prevLength];
                    VoiceWire.WriteVoice(wire, stream, frameSeq - (uint)k, burstStart && k == send - 1,
                                         ring[at], ringLength[at], prev, prevLength);
                    Enqueue(wire);
                    if (loopback != null) Loop(wire);
                }
            }

            // DebugLoopback: decode what was just sent, as a listener would, into the first 10 s.
            private void Loop(byte[] wire)
            {
                if (!VoiceWire.TryReadVoice(wire, wire.Length, loopbackPacket)) return;
                int n = loopback.Decode(loopbackPacket.Opus, loopbackPacket.OpusLength, decoded, false);
                if (n <= 0) return;
                n = Math.Min(n, LoopbackSamples - loopbackFill);
                Array.Copy(decoded, 0, loopbackPcm, loopbackFill, n);
                loopbackFill += n;
                if (loopbackFill >= LoopbackSamples) WriteLoopback(true);
            }

            // Writes what's been collected so far. With final, it's the last write this run: the decoder is
            // freed and later captures don't record again.
            private void WriteLoopback(bool final = true)
            {
                if (loopback == null || loopbackFill == 0) return;
                if (Interlocked.CompareExchange(ref loopbackWritten, 0, 0) != 0) return;
                if (final) Interlocked.Exchange(ref loopbackWritten, 1);
                try
                {
                    using (var file = new BinaryWriter(File.Create(loopbackPath)))
                    {
                        int bytes = loopbackFill * 2;
                        file.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                        file.Write(36 + bytes);
                        file.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                        file.Write(16);
                        file.Write((short)1);                        // PCM
                        file.Write((short)1);                        // mono
                        file.Write(VoiceFormat.SampleRate);
                        file.Write(VoiceFormat.SampleRate * 2);      // bytes per second
                        file.Write((short)2);                        // block align
                        file.Write((short)16);                       // bits per sample
                        file.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                        file.Write(bytes);
                        for (int i = 0; i < loopbackFill; i++)
                        {
                            float s = loopbackPcm[i] * 32767f;
                            file.Write((short)(s > 32767f ? 32767 : s < -32768f ? -32768 : (int)s));
                        }
                    }
                    Plugin.Log.LogInfo($"Revival voice: loopback ({loopbackFill / (float)VoiceFormat.SampleRate:0.0} s of what was sent) written to {loopbackPath}"
                                       + (final ? "" : " (more is added as you talk, up to 10 s)"));
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Revival voice: couldn't write the loopback recording: " + e.Message);
                }
                if (!final) return;
                loopback.Dispose();
                loopback = null;
            }
        }
    }
}
