using System;
using System.Runtime.InteropServices;

namespace StbcRevival
{
    /// <summary>
    /// A crash on CPUs with Intel's SHA extensions (SHA-NI: Intel 11th gen and later, AMD Ryzen, and others):
    /// the game's engine has a pre-release OpenSSL (1.1.0-dev) for HTTPS, whose SHA-1 routine for those CPUs
    /// is broken on Windows. It saves registers through rax, reuses rax in its loop (lea rax,[rsi+0x40]), then
    /// restores them, and the stack pointer, from there: an access violation in stbc.exe (offset 0x98b6a3) the
    /// first time anything hashes over HTTPS, as the game reaches the shuttle. OpenSSL picks that routine from
    /// its CPU capability flags (OPENSSL_ia32cap_P); here the "has SHA extensions" bit (word 2, bit 29) is
    /// cleared, so it uses its ordinary routine (the SHA-256 one checks the same bit, and has the same flaw).
    /// Only for this build of stbc.exe: the dispatcher's instructions are checked byte for byte before the flag
    /// is touched. Checked again each frame, in case OpenSSL sets its flags up after the mod loads.
    /// </summary>
    internal static class OpenSslShaFix
    {
        // sha1_block_data_order's dispatcher (RVAs in this stbc.exe, timestamp 0x5ab5449c):
        private const int LoadFlags = 0x98a364;   // mov r10d, [rip+disp32]: OPENSSL_ia32cap_P[2]
        private const int TestSha = 0x98a374;     // test r10d, 0x20000000
        private const int ShaBit = 0x20000000;

        private static readonly byte[] TestShaBytes = { 0x41, 0xF7, 0xC2, 0x00, 0x00, 0x00, 0x20 };

        private static IntPtr flags;              // &OPENSSL_ia32cap_P[2], or zero if not this build
        private static bool checkedBuild, cleared;

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        /// <summary>Plugin.Awake, and every frame: keep the SHA extensions bit off.</summary>
        internal static void Apply()
        {
            if (!checkedBuild)
            {
                checkedBuild = true;
                flags = FindFlags();
                if (flags != IntPtr.Zero)
                {
                    // The flags as found: word 0 is zero until OpenSSL has read the CPU (then it would set the bit
                    // later, at its first use; the per-frame check is there for that).
                    Plugin.Log.LogInfo($"OpenSSL: CPU flags {Marshal.ReadInt32(new IntPtr(flags.ToInt64() - 8)):X8} {Marshal.ReadInt32(new IntPtr(flags.ToInt64() - 4)):X8} {Marshal.ReadInt32(flags):X8}");
                    if ((Marshal.ReadInt32(flags) & ShaBit) == 0)
                        Plugin.Log.LogInfo("OpenSSL: this CPU has no SHA extensions (or they're already off); nothing to change");
                }
            }
            if (flags == IntPtr.Zero) return;
            int value = Marshal.ReadInt32(flags);
            if ((value & ShaBit) == 0) return;
            Marshal.WriteInt32(flags, value & ~ShaBit);
            if (!cleared) Plugin.Log.LogInfo("OpenSSL: this CPU has SHA extensions; the game's broken routine for them is switched off");
            cleared = true;
        }

        private static IntPtr FindFlags()
        {
            try
            {
                var exe = GetModuleHandleW(null);
                if (exe == IntPtr.Zero) return IntPtr.Zero;
                long baseAddress = exe.ToInt64();
                // 44 8B 15 <disp32>: mov r10d, [rip+disp32]
                if (Marshal.ReadByte(new IntPtr(baseAddress + LoadFlags)) != 0x44
                    || Marshal.ReadByte(new IntPtr(baseAddress + LoadFlags + 1)) != 0x8B
                    || Marshal.ReadByte(new IntPtr(baseAddress + LoadFlags + 2)) != 0x15)
                    return NotThisBuild();
                for (int i = 0; i < TestShaBytes.Length; i++)
                    if (Marshal.ReadByte(new IntPtr(baseAddress + TestSha + i)) != TestShaBytes[i]) return NotThisBuild();
                int disp = Marshal.ReadInt32(new IntPtr(baseAddress + LoadFlags + 3));
                return new IntPtr(baseAddress + LoadFlags + 7 + disp);
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo("OpenSSL: couldn't check the game's SHA routine: " + e.Message);
                return IntPtr.Zero;
            }
        }

        private static IntPtr NotThisBuild()
        {
            Plugin.Log.LogInfo("OpenSSL: not the stbc.exe build this fix knows; left alone");
            return IntPtr.Zero;
        }
    }
}
