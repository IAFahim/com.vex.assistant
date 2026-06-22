using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Vex.Assistant.Editor
{
    internal static class VexEnabledSkills
    {
        private const string k_CurrentSkillKeysKey = "AIAssistant.CurrentSkillKeys";
        private const string k_SkillAllowedPrefix = "AIAssistant.SkillAllowed.";

        public static List<string> AllowedNames()
        {
            var names = new List<string>();
            try
            {
                var raw = EditorPrefs.GetString(k_CurrentSkillKeysKey, "");
                if (string.IsNullOrEmpty(raw))
                    return names;

                foreach (var key in raw.Split('\n'))
                {
                    if (string.IsNullOrEmpty(key))
                        continue;
                    if (!EditorPrefs.GetBool(k_SkillAllowedPrefix + key, false))
                        continue;
                    var colon = key.IndexOf(':');
                    var name = colon > 0 ? key.Substring(0, colon) : key;
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
            }
            catch
            {
            }

            return names.Distinct().ToList();
        }

        public static List<string> AllSkillKeys()
        {
            try
            {
                var raw = EditorPrefs.GetString(k_CurrentSkillKeysKey, "");
                return string.IsNullOrEmpty(raw)
                    ? new List<string>()
                    : raw.Split('\n').Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        public static string NameOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            var colon = key.IndexOf(':');
            return colon > 0 ? key.Substring(0, colon) : key;
        }

        public static bool IsAllowed(string key)
        {
            return EditorPrefs.GetBool(k_SkillAllowedPrefix + key, false);
        }

        public static void SetAllowed(string key, bool allowed)
        {
            EditorPrefs.SetBool(k_SkillAllowedPrefix + key, allowed);
        }

        public static int SetAll(bool allowed)
        {
            var keys = AllSkillKeys();
            foreach (var k in keys)
                SetAllowed(k, allowed);
            return keys.Count;
        }
    }
}