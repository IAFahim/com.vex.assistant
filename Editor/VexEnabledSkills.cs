using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Vex.Assistant.Editor
{
    /// <summary>
    /// The bridge that lets the Assistant's <b>Manage Skills</b> Allow/Deny opt-in actually reach the flue chat agent.
    ///
    /// The flue chat runs on its OWN agent (flue-pipeline's <c>buildChatAgent</c>), which bypasses Unity's skill
    /// registry entirely — so without this, toggling a skill to "Allow" in Manage Skills changed nothing about what
    /// the chat could do. Here we read the user's allowed skills and hand their NAMES to the chat workflow; flue
    /// resolves the ones it ships (by folder name == SKILL.md <c>name</c>) and ignores the rest.
    ///
    /// Source of truth = the PERSISTED EditorPrefs the settings UI writes, NOT the live <c>SkillsRegistry</c>. The
    /// registry's filtered snapshot is only fully populated once a scan has run (e.g. the Manage Skills window was
    /// opened); reading it from a fresh chat turn returned just the internal skills. The persisted allow-state is
    /// always correct regardless of scan timing — it is literally what the user toggled.
    /// </summary>
    internal static class VexEnabledSkills
    {
        // Mirror of Unity.AI.Assistant.Editor.AssistantEditorPreferences (internal): EditorPrefs key layout.
        //   k_SettingsPrefix      = "AIAssistant."
        //   CurrentSkillKeys      = "AIAssistant.CurrentSkillKeys" — '\n'-joined composite keys "name:normalizedPath"
        //   SkillAllowed.<key>    = bool, default false (skills are deny-by-default)
        // Skill names are alphanumeric + hyphens (no ':'), so the name is the substring before the first ':'.
        const string k_CurrentSkillKeysKey = "AIAssistant.CurrentSkillKeys";
        const string k_SkillAllowedPrefix = "AIAssistant.SkillAllowed.";

        /// <summary>
        /// Names of every user-allowed skill (== SKILL.md <c>name</c> == flue skill folder, so flue resolves them
        /// directly). Main-thread only (reads EditorPrefs). Never throws — a read hiccup yields an empty list.
        /// </summary>
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
            catch { /* never let a prefs hiccup break a chat turn */ }
            return names.Distinct().ToList();
        }

        /// <summary>
        /// Every scanned skill's composite key ("name:normalizedPath"). Populated by Unity's skill scan (e.g. opening
        /// Manage Skills or Rescan), so this is empty until a scan has run. Main-thread only.
        /// </summary>
        public static List<string> AllSkillKeys()
        {
            try
            {
                var raw = EditorPrefs.GetString(k_CurrentSkillKeysKey, "");
                return string.IsNullOrEmpty(raw)
                    ? new List<string>()
                    : raw.Split('\n').Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList();
            }
            catch { return new List<string>(); }
        }

        /// <summary>The skill name (before the first ':') of a composite key.</summary>
        public static string NameOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            var colon = key.IndexOf(':');
            return colon > 0 ? key.Substring(0, colon) : key;
        }

        public static bool IsAllowed(string key) => EditorPrefs.GetBool(k_SkillAllowedPrefix + key, false);

        public static void SetAllowed(string key, bool allowed) => EditorPrefs.SetBool(k_SkillAllowedPrefix + key, allowed);

        /// <summary>Flip every scanned skill to allowed/denied. Returns how many keys were affected.</summary>
        public static int SetAll(bool allowed)
        {
            var keys = AllSkillKeys();
            foreach (var k in keys)
                SetAllowed(k, allowed);
            return keys.Count;
        }
    }
}
