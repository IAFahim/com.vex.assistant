using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Vex.Codex.Editor;

namespace Vex.Assistant.Editor
{
    /// <summary>
    /// Vex skills config, surfaced as a Project Settings page under <b>AI ▸ Vex Skills</b> — right next to Unity's own
    /// AI Assistant pages, so the whole AI tooling feels like one seamless extension (no separate top-menu window).
    /// What Unity's native Manage Skills can't give us: a one-click <b>Allow All</b> (so the skill-router / chat agent
    /// can draw from the whole auto-loaded catalog) and a <b>per-skill usage indicator</b> (how often each skill was
    /// selected/used by the discovery loop + its avg helpfulness rating, read from flue's <c>.vex-usage.json</c>).
    ///
    /// Allow-state is the SAME EditorPrefs (AIAssistant.SkillAllowed.&lt;key&gt;) Unity's window + <see cref="VexEnabledSkills"/>
    /// read, so toggles here reach the chat bridge immediately.
    /// </summary>
    static class VexSkillsSettings
    {
        [SettingsProvider]
        public static SettingsProvider Create()
        {
            var panel = new Panel();
            return new SettingsProvider("Project/AI/Vex Skills", SettingsScope.Project)
            {
                label = "Vex Skills",
                activateHandler = (_, __) => panel.Refresh(),
                guiHandler = panel.OnGui,
                keywords = new HashSet<string>(new[] { "vex", "skill", "allow", "usage", "flue", "ai", "rating" }),
            };
        }

        // A quick headless action kept on the menu for convenience.
        [MenuItem("Vex/Allow All Skills")]
        static void AllowAllMenu()
        {
            var n = VexEnabledSkills.SetAll(true);
            Debug.Log($"[Vex] Allowed all {n} scanned skill(s). (If 0, open Project Settings ▸ AI ▸ Skills / Rescan first to populate the list.)");
        }

        sealed class Panel
        {
            struct SkillStat { public int Selected; public int Used; public double AvgRating; public int RatingCount; public string Suggestion; }

            readonly List<string> m_Keys = new();
            Dictionary<string, SkillStat> m_Usage = new();
            string m_Filter = "";
            string m_UsagePath = "";
            bool m_UsageFound;

            public void Refresh()
            {
                m_Keys.Clear();
                m_Keys.AddRange(VexEnabledSkills.AllSkillKeys().OrderBy(VexEnabledSkills.NameOf, StringComparer.OrdinalIgnoreCase));
                m_Usage = LoadUsage(out m_UsagePath, out m_UsageFound);
            }

            public void OnGui(string searchContext)
            {
                EditorGUILayout.Space(4);
                DrawToolbar();

                if (m_Keys.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "No scanned skills yet. Open the native AI ▸ Skills page (or Rescan there) once to populate the " +
                        "catalog, then come back — the list is shared via EditorPrefs.",
                        MessageType.Info);
                    return;
                }

                DrawHeader();
                foreach (var key in m_Keys)
                {
                    var name = VexEnabledSkills.NameOf(key);
                    if (!string.IsNullOrEmpty(m_Filter) && name.IndexOf(m_Filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    DrawRow(key, name);
                }
                DrawFooter();
            }

            void DrawToolbar()
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Allow All", GUILayout.Width(80))) { VexEnabledSkills.SetAll(true); Refresh(); }
                    if (GUILayout.Button("Deny All", GUILayout.Width(80))) { VexEnabledSkills.SetAll(false); Refresh(); }
                    if (GUILayout.Button("Refresh", GUILayout.Width(70))) Refresh();
                    GUILayout.Space(8);
                    GUILayout.Label("Filter", GUILayout.Width(36));
                    m_Filter = GUILayout.TextField(m_Filter ?? "", GUILayout.MinWidth(120), GUILayout.MaxWidth(220));
                    GUILayout.FlexibleSpace();
                    var allowed = m_Keys.Count(VexEnabledSkills.IsAllowed);
                    GUILayout.Label($"{allowed}/{m_Keys.Count} allowed", EditorStyles.miniLabel);
                }
            }

            static void DrawHeader()
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label("Allow", GUILayout.Width(44));
                    GUILayout.Label("Skill", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label("selected", EditorStyles.miniLabel, GUILayout.Width(60));
                    GUILayout.Label("used", EditorStyles.miniLabel, GUILayout.Width(40));
                    GUILayout.Label("rating", EditorStyles.miniLabel, GUILayout.Width(56));
                }
            }

            void DrawRow(string key, string name)
            {
                m_Usage.TryGetValue(name, out var stat);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var was = VexEnabledSkills.IsAllowed(key);
                    var now = EditorGUILayout.Toggle(was, GUILayout.Width(44));
                    if (now != was) VexEnabledSkills.SetAllowed(key, now);

                    var tip = string.IsNullOrEmpty(stat.Suggestion) ? key : key + "\n\n💡 " + stat.Suggestion;
                    GUILayout.Label(new GUIContent(string.IsNullOrEmpty(stat.Suggestion) ? name : name + " 💡", tip), GUILayout.MinWidth(140));
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(stat.Selected > 0 ? stat.Selected.ToString() : "·", Mini(stat.Selected > 0), GUILayout.Width(60));
                    GUILayout.Label(stat.Used > 0 ? stat.Used.ToString() : "·", Mini(stat.Used > 0), GUILayout.Width(40));
                    GUILayout.Label(stat.RatingCount > 0 ? $"★{stat.AvgRating:0.0}" : "·", Mini(stat.RatingCount > 0), GUILayout.Width(56));
                }
            }

            static GUIStyle Mini(bool active)
            {
                var s = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
                if (!active) s.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
                return s;
            }

            void DrawFooter()
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label(m_UsageFound ? $"usage: {m_UsagePath}"
                        : "usage file not found yet — runs through the chat→forge loop create it (.vex-usage.json).",
                        EditorStyles.miniLabel);
                }
            }

            static Dictionary<string, SkillStat> LoadUsage(out string path, out bool found)
            {
                var result = new Dictionary<string, SkillStat>();
                path = ""; found = false;
                try
                {
                    var dir = CodexSettings.Load()?.FlueDir;
                    if (string.IsNullOrEmpty(dir)) return result;
                    path = Path.Combine(dir, ".vex-usage.json");
                    if (!File.Exists(path)) return result;
                    found = true;
                    var root = JObject.Parse(File.ReadAllText(path));
                    if (root["skills"] is not JObject skills) return result;
                    foreach (var p in skills.Properties())
                    {
                        if (p.Value is not JObject s) continue;
                        var rc = s.Value<int?>("ratingCount") ?? 0;
                        var rsum = s.Value<double?>("ratingSum") ?? 0;
                        result[p.Name] = new SkillStat
                        {
                            Selected = s.Value<int?>("selected") ?? 0,
                            Used = s.Value<int?>("used") ?? 0,
                            RatingCount = rc,
                            AvgRating = rc > 0 ? rsum / rc : 0,
                            Suggestion = s.Value<string>("lastSuggestion") ?? "",
                        };
                    }
                }
                catch { /* corrupt/locked → show no usage */ }
                return result;
            }
        }
    }
}
