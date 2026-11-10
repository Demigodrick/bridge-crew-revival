// The microphone recording the game's voice chat makes, shared with voice commands and Revival voice: one
// recording per microphone, so nothing here calls Microphone.Start or Microphone.End (commit c903fa8).
// Main thread only (Unity's Microphone and AudioClip.GetData).
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace StbcRevival
{
    internal static class MicTap
    {
        internal static DaikonForge.VoIP.AudioInputDeviceBase ChatInput() =>
            VoIpManager.IEFBKOMBFGO == null ? null : VoIpManager.IEFBKOMBFGO.GetLocalMicrophoneInputDevice();

        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // By input class: the field holding its device name (null if it has none this knows), and its AudioSource.
        private static readonly Dictionary<Type, FieldInfo> deviceFields = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo> sourceFields = new Dictionary<Type, FieldInfo>();
        private static string lastFailure;

        // Looked up quietly (AccessTools.Field warns about the one a class doesn't have).
        private static FieldInfo Find(Type type, string name)
        {
            for (; type != null; type = type.BaseType)
                if (type.GetField(name, Fields) is FieldInfo f)
                    return f;
            return null;
        }

        // The microphone voice chat records from (by input class). False if the class isn't one this knows.
        // Its input is either DaikonForge's UnityMicrophoneInputDevice (device in FJNHDGFAOLB) or the game's
        // WwiseMicrophoneInputDevice (AudioInputDeviceBase's GMNCBFBICKK), by voice template.
        internal static bool ChatDevice(DaikonForge.VoIP.AudioInputDeviceBase input, out string device)
        {
            device = null;
            var type = input.GetType();
            if (!deviceFields.TryGetValue(type, out var field))
            {
                field = null;
                foreach (var name in new[] { "FJNHDGFAOLB", "GMNCBFBICKK" })
                {
                    var f = Find(type, name);
                    if (f != null && f.FieldType == typeof(string)) { field = f; break; }
                }
                deviceFields[type] = field;
                if (field == null) Plugin.Log.LogWarning($"Mic tap: voice chat's microphone ({type.Name}) isn't one this knows");
            }
            if (field == null) return false;
            device = field.GetValue(input) as string;
            return true;
        }

        // The voice chat's own recording of your microphone (a looping clip on its AudioSource, EALMEBPADKI), if
        // it's recording; false (device and clip null) when it isn't (muted, mic off, not in a session).
        internal static bool TryFind(out string device, out AudioClip clip)
        {
            device = null;
            clip = null;
            try
            {
                var input = ChatInput();
                if (input == null || !ChatDevice(input, out var name)) return false;
                var type = input.GetType();
                if (!sourceFields.TryGetValue(type, out var field)) sourceFields[type] = field = Find(type, "EALMEBPADKI");
                var source = field?.GetValue(input) as AudioSource;
                if (source == null || source.clip == null || !Microphone.IsRecording(name)) return false;
                device = name;
                clip = source.clip;
                return true;
            }
            catch (Exception e)
            {
                if (e.Message != lastFailure)
                {
                    lastFailure = e.Message;
                    Plugin.Log.LogWarning("Mic tap: voice chat's recording: " + e.Message);
                }
                return false;
            }
        }

        /// <summary>
        /// Follows a looping recording from where it is now: each Read hands over the mono samples recorded since
        /// the last one, at the clip's own rate. Reads whole 10 ms pieces into one reused buffer, so nothing is
        /// allocated per frame; what's left over comes with the next Read.
        /// </summary>
        internal sealed class Reader
        {
            internal readonly string Device;
            internal readonly AudioClip Clip;
            internal readonly int Frequency;
            private readonly int length, channels, piece;
            private readonly float[] buffer;
            private int position;

            internal Reader(string device, AudioClip clip)
            {
                Device = device;
                Clip = clip;
                Frequency = clip.frequency;
                length = clip.samples;
                channels = Math.Max(1, clip.channels);
                piece = Math.Max(1, Math.Min(Frequency / 100, length / 2));
                buffer = new float[piece * channels];
                position = Microphone.GetPosition(device);
            }

            internal int Read(List<float> into)
            {
                int end = Microphone.GetPosition(Device);
                if (end < 0 || end >= length) return 0;
                int available = (end - position + length) % length, added = 0;
                while (available >= piece)
                {
                    if (position + piece <= length)
                    {
                        Clip.GetData(buffer, position);
                        Mono(buffer, piece, into);
                    }
                    else
                    {
                        // Across the end of the loop (only when the clip isn't a whole number of pieces).
                        int first = length - position;
                        var a = new float[first * channels];
                        var b = new float[(piece - first) * channels];
                        Clip.GetData(a, position);
                        Clip.GetData(b, 0);
                        Mono(a, first, into);
                        Mono(b, piece - first, into);
                    }
                    position = (position + piece) % length;
                    available -= piece;
                    added += piece;
                }
                return added;
            }

            private void Mono(float[] data, int frames, List<float> into)
            {
                if (channels == 1)
                {
                    for (int i = 0; i < frames; i++) into.Add(data[i]);
                    return;
                }
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++) sum += data[i * channels + c];
                    into.Add(sum / channels);
                }
            }
        }
    }
}
