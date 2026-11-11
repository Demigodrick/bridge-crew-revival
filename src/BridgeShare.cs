using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StbcRevival
{
    /// <summary>
    /// Bridge layouts shared by the host: in a crew, everyone with the mod sees the host's layout of the bridge (the
    /// host's own BepInEx/bridges file), not their own. When a bridge (or briefing room) loads, a crewmate asks the
    /// host for its layout and waits for the answer (up to WaitSeconds): the layout, or "none" (the bridge as the
    /// game has it). No answer (a host without the mod, or with sharing off): your own file, as alone. The host also
    /// sends a layout to everyone again whenever it changes (saved from the designer while you play).
    ///
    /// Event 169: proto, kind (ask, layout, none), scene name, and for a layout its text, gzipped (Photon's strings
    /// stop at 32 KB). Only the host's answers count. Added models (.obj) aren't sent: a crewmate without the same
    /// file in their models folder doesn't see that part.
    /// </summary>
    internal static class BridgeShare
    {
        internal const byte Code = 169;
        private const byte Proto = 1;
        private const byte Ask = 0, Layout = 1, None = 2;
        private const float WaitSeconds = 3f;
        private const int MaxBytes = 512 * 1024;

        internal enum State { NotShared, Waiting, Has, None }

        private sealed class Entry
        {
            public int Root;          // the load of the scene this was asked for
            public float AskedAt;
            public bool Answered;
            public string Text;       // null: the host has none
            public int Version;
        }

        private static readonly Dictionary<string, Entry> hostLayouts = new Dictionary<string, Entry>();
        private static bool hooked;
        private static int roomMaster = -1;
        private static int versions;
        private static float nextCheck;

        private static bool On => Plugin.BridgeShareLayouts == null || Plugin.BridgeShareLayouts.Value;

        // ------------------------------------------------------------------------------------------- Photon
        private static Type Photon => Patches.PhotonNetworkType;
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;

        private static bool InRoom => Photon?.GetMethod("get_inRoom", Static)?.Invoke(null, null) is bool b && b;
        internal static bool IsHost => InRoom && Photon?.GetMethod("get_isMasterClient", Static)?.Invoke(null, null) is bool b && b;
        private static int MasterActor
        {
            get
            {
                var master = Photon?.GetMethod("get_masterClient", Static)?.Invoke(null, null);
                return master?.GetType().GetMethod("get_ID", BindingFlags.Public | BindingFlags.Instance)?.Invoke(master, null) is int id ? id : -1;
            }
        }

        private static void Send(object content, int[] to)
        {
            try { NDIOGIGMPBH.RaiseEvent(Code, content, true, new CGJCFMCOBMJ { TargetActors = to }); }
            catch (Exception e) { Plugin.Log.LogWarning("Bridge layout: sending: " + e.Message); }
        }

        // ------------------------------------------------------------------------------------------- every frame
        internal static void Update()
        {
            if (!hooked) { hooked = true; NDIOGIGMPBH.OnEventCall += OnEvent; }
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.5f;
            if (!On || !InRoom || IsHost) { if (hostLayouts.Count > 0 || roomMaster != -1) { hostLayouts.Clear(); roomMaster = -1; } return; }
            // A new host (the old one left): everything is asked again.
            int master = MasterActor;
            if (master != roomMaster) { hostLayouts.Clear(); roomMaster = master; }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded || !Shareable(s.name)) continue;
                var roots = s.GetRootGameObjects();
                int root = roots.Length > 0 ? roots[0].GetInstanceID() : 0;
                if (root == 0) continue;
                if (hostLayouts.TryGetValue(s.name, out var e) && e.Root == root) continue;
                hostLayouts[s.name] = new Entry { Root = root, AskedAt = Time.unscaledTime };
                Send(new object[] { Proto, Ask, s.name }, new[] { master });
            }
        }

        /// <summary>A plain scene name of a bridge or briefing room: letters, digits, spaces, _ and - only (no path).</summary>
        internal static bool SceneName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 100 || !Shareable(name)) return false;
            foreach (char c in name) if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == ' ')) return false;
            return true;
        }

        // The bridges and briefing rooms (the scenes the designer has).
        private static bool Shareable(string name) =>
            name.IndexOf("Bridge", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Briefing", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>What the host said about this scene's layout (crewmates only).</summary>
        internal static State HostLayout(string scene, out string text, out int version)
        {
            text = null; version = 0;
            if (!On || !InRoom || IsHost || !hostLayouts.TryGetValue(scene, out var e)) return State.NotShared;
            if (!e.Answered) return Time.unscaledTime - e.AskedAt < WaitSeconds ? State.Waiting : State.NotShared;
            text = e.Text;
            version = e.Version;
            return e.Text != null ? State.Has : State.None;
        }

        /// <summary>The host: this scene's layout, to everyone (it went on, or changed).</summary>
        internal static void Push(string scene, string text)
        {
            if (!On || !IsHost) return;
            var zip = Zip(text);
            if (zip.Length > MaxBytes) { Plugin.Log.LogWarning($"Bridge layout: {scene}'s layout is too big to share ({zip.Length / 1024} KB)"); return; }
            Send(new object[] { Proto, Layout, scene, zip }, null);
            Plugin.Log.LogInfo($"Bridge layout: shared {scene}'s layout with the crew ({zip.Length} bytes)");
        }

        private static void OnEvent(byte code, object content, int sender)
        {
            if (code != Code || !On) return;
            try
            {
                if (!(content is object[] m) || m.Length < 3 || !(m[0] is byte proto) || proto != Proto || !(m[1] is byte kind) || !(m[2] is string scene)) return;
                // A scene's name, nothing else: it names a file in the layouts folder.
                if (!SceneName(scene)) return;
                if (kind == Ask)
                {
                    if (!IsHost) return;
                    var text = BridgeLayout.OwnLayout(scene);
                    if (text == null) { Send(new object[] { Proto, None, scene }, new[] { sender }); return; }
                    var zip = Zip(text);
                    if (zip.Length > MaxBytes) { Send(new object[] { Proto, None, scene }, new[] { sender }); return; }
                    Send(new object[] { Proto, Layout, scene, zip }, new[] { sender });
                    return;
                }
                // Answers: only the host's.
                if (IsHost || sender != MasterActor) return;
                if (!hostLayouts.TryGetValue(scene, out var e)) hostLayouts[scene] = e = new Entry { Root = -1 };
                string got = null;
                if (kind == Layout)
                {
                    if (m.Length < 4 || !(m[3] is byte[] zip) || zip.Length > MaxBytes) return;
                    got = Unzip(zip);
                }
                else if (kind != None) return;
                if (e.Answered && e.Text == got) return;
                e.Answered = true;
                e.Text = got;
                e.Version = ++versions;
                Plugin.Log.LogInfo($"Bridge layout: the host's layout for {scene}: {(got == null ? "none (the bridge as the game has it)" : got.Length + " characters")}");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Bridge layout: receiving: " + ex.Message); }
        }

        private static byte[] Zip(string text)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new GZipStream(ms, CompressionMode.Compress))
                {
                    var b = Encoding.UTF8.GetBytes(text);
                    z.Write(b, 0, b.Length);
                }
                return ms.ToArray();
            }
        }

        private static string Unzip(byte[] zip)
        {
            using (var z = new GZipStream(new MemoryStream(zip), CompressionMode.Decompress))
            using (var outp = new MemoryStream())
            {
                var buf = new byte[8192];
                int n, total = 0;
                while ((n = z.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > 4 * MaxBytes) throw new InvalidDataException("unzips too big");
                    outp.Write(buf, 0, n);
                }
                return Encoding.UTF8.GetString(outp.ToArray());
            }
        }
    }
}
