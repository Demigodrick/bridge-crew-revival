using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace StbcRevival
{
    /// <summary>
    /// The mod's own texts in German, when the game's menu language is German (anything else: English, as written).
    /// The game's texts are English strings used as keys (SVR.Localization.GetLocalizedText returns the key itself
    /// when it knows no translation), and every text field and the directive screen look their text up that way on
    /// each player's own PC. So one postfix there translates the mod's texts for every player in their own
    /// language, even for a mission text the host made. Texts that go straight to a window or a popup are passed
    /// through T on the way.
    /// The table (TranslationDe) maps an English text to its German one; a text with numbers or names in it is a
    /// pattern, "Wave {0}: destroy all {1} hostiles", and what stands for {0}, {1} is looked up itself too.
    /// </summary>
    internal static partial class Translation
    {
        private sealed class Pattern
        {
            public Regex Regex;
            public string German;
            public int Literal; // letters of fixed text: the longer, the more specific
        }

        private static readonly Regex Placeholder = new Regex(@"\{\d+\}");
        private static readonly Dictionary<string, string> exact = new Dictionary<string, string>();
        private static readonly List<Pattern> patterns = new List<Pattern>();
        private static readonly Dictionary<string, string> cache = new Dictionary<string, string>(); // text -> translation or null
        private static readonly MethodInfo menuLanguage = AccessTools.Method(typeof(SVR.Localization), "get_MenuLanguageID");
        private static bool loaded, germanNow, languageRead;
        private static int languageReadAt;

        // The menu language, looked at again every second (it can be changed in the options; this is called a lot).
        internal static bool German
        {
            get
            {
                int now = Environment.TickCount;
                if (!languageRead || unchecked(now - languageReadAt) > 1000)
                {
                    languageRead = true;
                    languageReadAt = now;
                    try { germanNow = menuLanguage != null && menuLanguage.Invoke(null, null)?.ToString() == "German"; }
                    catch { germanNow = false; }
                }
                return germanNow;
            }
        }

        /// <summary>The text in the game's language: German when the menu is, otherwise as it is.</summary>
        internal static string T(string english) => German && !string.IsNullOrEmpty(english) ? Translate(english) ?? english : english;

        internal static void Patch(Harmony harmony)
        {
            var lookup = AccessTools.Method(typeof(SVR.Localization), "GetLocalizedText", new[] { typeof(string), typeof(int) });
            if (lookup == null)
            {
                Plugin.Log.LogWarning("Translation: the game's text lookup wasn't found; the mod's texts stay English");
                return;
            }
            harmony.Patch(lookup, postfix: new HarmonyMethod(typeof(Translation), nameof(Lookup_Postfix)));
        }

        // Unknown to the game: the key comes back as it is.
        private static void Lookup_Postfix(string __0, ref string __result)
        {
            if (__result == null || !string.Equals(__result, __0, StringComparison.Ordinal) || !German) return;
            try
            {
                var german = Translate(__result);
                if (german != null) __result = german;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Translation: " + e.Message);
            }
        }

        /// <summary>The German text, or null if the table has none.</summary>
        internal static string Translate(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            Load();
            string hit;
            lock (cache)
                if (cache.TryGetValue(text, out hit)) return hit;
            hit = Find(text);
            lock (cache)
            {
                if (cache.Count > 4000) cache.Clear();
                cache[text] = hit;
            }
            if (hit != null && Plugin.Diagnostics.Value) Plugin.Log.LogInfo($"[diag] translated '{text}' -> '{hit}'");
            return hit;
        }

        private static string Find(string text)
        {
            if (exact.TryGetValue(text, out var german)) return german;
            foreach (var pattern in patterns)
            {
                var match = pattern.Regex.Match(text);
                if (!match.Success) continue;
                var args = new object[match.Groups.Count - 1];
                for (int i = 1; i < match.Groups.Count; i++)
                {
                    var part = match.Groups[i].Value;
                    args[i - 1] = Translate(part) ?? part;
                }
                try { return string.Format(pattern.German, args); }
                catch (FormatException) { return null; }
            }
            return null;
        }

        private static void Load()
        {
            lock (cache)
            {
                if (loaded) return;
                loaded = true;
                LoadGerman();
                patterns.Sort((a, b) => b.Literal.CompareTo(a.Literal));
            }
        }

        // A text, or a pattern with {0}, {1}... in the order they come in the English text.
        private static void Add(string english, string german)
        {
            if (!Placeholder.IsMatch(english))
            {
                exact[english] = german;
                return;
            }
            var regex = new StringBuilder("^");
            int literal = 0, last = 0;
            foreach (Match slot in Placeholder.Matches(english))
            {
                var fixedText = english.Substring(last, slot.Index - last);
                regex.Append(Regex.Escape(fixedText)).Append("(.+?)");
                literal += fixedText.Length;
                last = slot.Index + slot.Length;
            }
            var tail = english.Substring(last);
            regex.Append(Regex.Escape(tail)).Append('$');
            literal += tail.Length;
            patterns.Add(new Pattern { Regex = new Regex(regex.ToString(), RegexOptions.Singleline), German = german, Literal = literal });
        }
    }
}
