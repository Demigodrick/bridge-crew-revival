using System;
using System.Collections.Generic;
using System.Reflection;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace StbcRevival
{
    /// <summary>
    /// Setting a Photon player's or room's custom property, safely: not before Photon is connected (PUN throws
    /// a NullReferenceException then, which used to switch off the whole feature for the session), and a
    /// failure is logged once per key instead of thrown. Returns whether it was sent.
    /// </summary>
    internal static class PhotonProps
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly HashSet<string> warned = new HashSet<string>();

        internal static bool Set(object owner, string key, object value)
        {
            if (owner == null) return false;
            var photon = Patches.PhotonNetworkType;
            if (!(photon?.GetMethod("get_connected", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is bool connected) || !connected)
                return false;
            try
            {
                owner.GetType().GetMethod("SetCustomProperties", All)?.Invoke(owner, new object[] { new PhotonHashtable { [key] = value }, null, false });
                return true;
            }
            catch (Exception e)
            {
                if (warned.Add(key)) Plugin.Log.LogWarning($"Couldn't set the {key} property (will retry): {(e.InnerException ?? e).Message}");
                return false;
            }
        }
    }
}
