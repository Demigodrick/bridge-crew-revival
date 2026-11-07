using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.VR;

namespace StbcRevival
{
    /// <summary>
    /// What a log needs to tell a slow or stuttering game apart from a bad connection, for every player:
    ///   - Once, and whenever it changes: the PC (GPU, CPU, memory, OS) and the headset (VR runtime, model,
    ///     refresh rate, render scale).
    ///   - Every 30 s: the frame rate (average, lowest), frames of 100 ms or more and the longest (while a
    ///     frame lasts no voice or game data moves; a host's long frames hold up the whole crew), frames too
    ///     late for the headset (it shows an old picture again: the game isn't keeping up), the headset's
    ///     GPU time where Unity reports it, memory in use and garbage collections (a common cause of hitches
    ///     in this Unity), and the game phase.
    ///   - Every 30 s while you've talked: your own voice as it leaves this game (PhotonNetwork.RaiseEvent,
    ///     event 125, as VoIpManager receives it): frames sent, and pauses mid-sentence (your game stalling or
    ///     your microphone delivering unevenly), with the microphone and its sample rates.
    /// </summary>
    internal static class PerfLog
    {
        private const float ReportSeconds = 30f, HitchSeconds = 0.1f;
        private const byte VoiceEvent = 125;

        private static float nextReport = ReportSeconds, nextPhaseCheck, longest, total;
        private static int frames, hitches, late, collections = -1;
        private static bool loadingSeen;
        private static string lastHardware, lastVr;

        // Your voice going out. RaiseEvent can be called off the main thread (the voice encoder has one), so
        // timed with a Stopwatch and kept under a lock.
        private static readonly object voiceLock = new object();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static long lastVoiceMs = -1;
        private static int voiceFrames, voicePauses;
        private static long voiceLongestPauseMs;
        private const long PauseMs = 150, BurstEndMs = 1000; // a gap this long mid-sentence is a pause; longer, the sentence ended

        private static PropertyInfo gpuTime; // UnityEngine.VR.VRStats.gpuTimeLastFrame, where this Unity has it

        internal static void Patch(Harmony harmony)
        {
            var raise = AccessTools.Method(typeof(NDIOGIGMPBH), "RaiseEvent");
            if (raise != null) harmony.Patch(raise, prefix: new HarmonyMethod(typeof(PerfLog), nameof(RaiseEvent_Prefix)));
            else Plugin.Log.LogWarning("Performance log: PhotonNetwork.RaiseEvent wasn't found; your own voice isn't logged");
            gpuTime = typeof(VRSettings).Assembly.GetType("UnityEngine.VR.VRStats")?.GetProperty("gpuTimeLastFrame", BindingFlags.Public | BindingFlags.Static);
        }

        private static void RaiseEvent_Prefix(byte __0)
        {
            if (__0 == VoiceEvent) VoiceFrameSent();
        }

        /// <summary>A voice frame of yours sent (also by DirectVoice, when one goes only directly to crewmates).</summary>
        internal static void VoiceFrameSent()
        {
            lock (voiceLock)
            {
                long now = clock.ElapsedMilliseconds;
                if (lastVoiceMs >= 0)
                {
                    long gap = now - lastVoiceMs;
                    if (gap >= PauseMs && gap < BurstEndMs)
                    {
                        voicePauses++;
                        if (gap > voiceLongestPauseMs) voiceLongestPauseMs = gap;
                    }
                }
                lastVoiceMs = now;
                voiceFrames++;
            }
        }

        internal static void Update()
        {
            float frame = Time.unscaledDeltaTime;
            frames++;
            total += frame;
            if (frame > longest) longest = frame;
            if (frame >= HitchSeconds) hitches++;
            float hz = VRSettings.enabled ? VRDevice.refreshRate : 0f;
            if (hz > 0f && frame > 1.5f / hz) late++;
            float now = Time.realtimeSinceStartup;
            if (now >= nextPhaseCheck)
            {
                nextPhaseCheck = now + 0.5f;
                if (GameState.Loading()) loadingSeen = true;
            }
            if (now < nextReport) return;
            nextReport = now + ReportSeconds;
            LogSetup();
            Report(hz);
            ReportVoice();
        }

        private static void LogSetup()
        {
            string hardware = $"{SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsMemorySize} MB, {SystemInfo.graphicsDeviceVersion}), "
                              + $"{SystemInfo.processorType} ×{SystemInfo.processorCount}, {SystemInfo.systemMemorySize} MB RAM, {SystemInfo.operatingSystem}";
            if (hardware != lastHardware)
            {
                lastHardware = hardware;
                Plugin.Log.LogInfo("PC: " + hardware);
            }
            string vr;
            try
            {
                vr = VRSettings.enabled
                    ? $"{VRSettings.loadedDeviceName}, {VRDevice.model}, {VRDevice.refreshRate:0} Hz, render scale {VRSettings.renderScale:0.##}, "
                      + $"eye texture {VRSettings.eyeTextureWidth}×{VRSettings.eyeTextureHeight}"
                    : $"off (desktop, {Screen.width}×{Screen.height})";
            }
            catch (Exception e)
            {
                vr = "unknown: " + e.Message;
            }
            if (vr != lastVr)
            {
                lastVr = vr;
                Plugin.Log.LogInfo("VR: " + vr);
            }
        }

        private static void Report(float hz)
        {
            int gc = GC.CollectionCount(0);
            int collected = collections < 0 ? 0 : gc - collections;
            collections = gc;
            float fps = total > 0f ? frames / total : 0f;
            string headset = hz > 0f ? $"; {late} frames too late for the headset's {hz:0} Hz" : "";
            string gpu = "";
            if (hz > 0f && gpuTime != null)
            {
                try { if (gpuTime.GetValue(null, null) is float ms && ms > 0f) gpu = $" (GPU {ms * 1000f:0.0} ms last frame)"; }
                catch { gpuTime = null; }
            }
            string phase;
            try { phase = GameState.Phase() ?? "no phase"; }
            catch { phase = "unknown phase"; }
            Plugin.Log.LogInfo($"Performance: last {ReportSeconds:0} s in {phase}{(loadingSeen ? " (loading part of it)" : "")}, "
                               + $"{fps:0} fps average, lowest {(longest > 0f ? 1f / longest : 0f):0}; {hitches} frames of {HitchSeconds * 1000:0}+ ms, "
                               + $"longest {longest * 1000f:0} ms{headset}{gpu}; memory {GC.GetTotalMemory(false) / (1024 * 1024)} MB, {collected} collections");
            frames = hitches = late = 0;
            total = longest = 0f;
            loadingSeen = false;
        }

        private static void ReportVoice()
        {
            int sent, pauses;
            long longestPause;
            lock (voiceLock)
            {
                sent = voiceFrames;
                pauses = voicePauses;
                longestPause = voiceLongestPauseMs;
                voiceFrames = voicePauses = 0;
                voiceLongestPauseMs = 0;
            }
            if (sent == 0) return;
            Plugin.Log.LogInfo($"Your voice: last {ReportSeconds:0} s, {sent} frames sent, {pauses} pauses of {PauseMs}+ ms mid-sentence"
                               + (pauses > 0 ? $" (longest {longestPause} ms)" : "") + "; " + MicrophoneInfo());
        }

        private static object micInput;
        private static string micInfo;

        // Voice chat's microphone, looked up again only when voice chat's input changes.
        private static string MicrophoneInfo()
        {
            var input = VoiceCommands.ChatInput();
            if (input == null) return "microphone unknown";
            if (!ReferenceEquals(input, micInput))
            {
                micInput = input;
                micInfo = Describe(input);
            }
            return micInfo;
        }

        private static string Describe(DaikonForge.VoIP.AudioInputDeviceBase input)
        {
            try
            {
                if (!VoiceCommands.ChatDevice(input, out string device)) return $"microphone via {input.GetType().Name}";
                UnityEngine.Microphone.GetDeviceCaps(device, out int min, out int max);
                string rates = min == 0 && max == 0 ? "any rate" : min == max ? $"{min} Hz" : $"{min}-{max} Hz";
                return $"microphone '{(string.IsNullOrEmpty(device) ? "default" : device)}' ({rates}, via {input.GetType().Name})";
            }
            catch (Exception e)
            {
                return "microphone unknown: " + e.Message;
            }
        }
    }
}
