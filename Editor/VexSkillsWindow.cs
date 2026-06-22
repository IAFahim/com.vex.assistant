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
    internal static class VexSkillsSettings
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
                keywords = new HashSet<string>(new[] { "vex", "skill", "allow", "usage", "flue", "ai", "rating" })
            };
        }

        [MenuItem("Vex/Allow All Skills")]
        private static void AllowAllMenu()
        {
            var n = VexEnabledSkills.SetAll(true);
            Debug.Log(
                $"[Vex] Allowed all {n} scanned skill(s). (If 0, open Project Settings ▸ AI ▸ Skills / Rescan first to populate the list.)");
        }

        private sealed class Panel
        {
            private readonly List<string> m_Keys = new();
            private string m_Filter = "";
            private Dictionary<string, SkillStat> m_Usage = new();
            private bool m_UsageFound;
            private string m_UsagePath = "";

            public void Refresh()
            {
                m_Keys.Clear();
                m_Keys.AddRange(VexEnabledSkills.AllSkillKeys()
                    .OrderBy(VexEnabledSkills.NameOf, StringComparer.OrdinalIgnoreCase));
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
                    if (!string.IsNullOrEmpty(m_Filter) &&
                        name.IndexOf(m_Filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    DrawRow(key, name);
                }

                DrawFooter();
            }

            private void DrawToolbar()
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Allow All", GUILayout.Width(80)))
                    {
                        VexEnabledSkills.SetAll(true);
                        Refresh();
                    }

                    if (GUILayout.Button("Deny All", GUILayout.Width(80)))
                    {
                        VexEnabledSkills.SetAll(false);
                        Refresh();
                    }

                    if (GUILayout.Button("Refresh", GUILayout.Width(70))) Refresh();
                    GUILayout.Space(8);
                    GUILayout.Label("Filter", GUILayout.Width(36));
                    m_Filter = GUILayout.TextField(m_Filter ?? "", GUILayout.MinWidth(120), GUILayout.MaxWidth(220));
                    GUILayout.FlexibleSpace();
                    var allowed = m_Keys.Count(VexEnabledSkills.IsAllowed);
                    GUILayout.Label($"{allowed}/{m_Keys.Count} allowed", EditorStyles.miniLabel);
                }
            }

            private static void DrawHeader()
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

            private void DrawRow(string key, string name)
            {
                m_Usage.TryGetValue(name, out var stat);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var was = VexEnabledSkills.IsAllowed(key);
                    var now = EditorGUILayout.Toggle(was, GUILayout.Width(44));
                    if (now != was) VexEnabledSkills.SetAllowed(key, now);

                    var tip = string.IsNullOrEmpty(stat.Suggestion) ? key : key + "\n\n💡 " + stat.Suggestion;
                    GUILayout.Label(new GUIContent(string.IsNullOrEmpty(stat.Suggestion) ? name : name + " 💡", tip),
                        GUILayout.MinWidth(140));
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(stat.Selected > 0 ? stat.Selected.ToString() : "·", Mini(stat.Selected > 0),
                        GUILayout.Width(60));
                    GUILayout.Label(stat.Used > 0 ? stat.Used.ToString() : "·", Mini(stat.Used > 0),
                        GUILayout.Width(40));
                    GUILayout.Label(stat.RatingCount > 0 ? $"★{stat.AvgRating:0.0}" : "·", Mini(stat.RatingCount > 0),
                        GUILayout.Width(56));
                }
            }

            private static GUIStyle Mini(bool active)
            {
                var s = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
                if (!active) s.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
                return s;
            }

            private void DrawFooter()
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label(m_UsageFound
                            ? $"usage: {m_UsagePath}"
                            : "usage file not found yet — runs through the chat→forge loop create it (.vex-usage.json).",
                        EditorStyles.miniLabel);
                }
            }

            private static Dictionary<string, SkillStat> LoadUsage(out string path, out bool found)
            {
                var result = new Dictionary<string, SkillStat>();
                path = "";
                found = false;
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
                        if (p.Value is JObject s)
                            result[p.Name] = ParseStat(s);
                }
                catch
                {
                }

                return result;
            }

            private static SkillStat ParseStat(JObject s)
            {
                var rc = s.Value<int?>("ratingCount") ?? 0;
                var rsum = s.Value<double?>("ratingSum") ?? 0;
                return new SkillStat
                {
                    Selected = s.Value<int?>("selected") ?? 0,
                    Used = s.Value<int?>("used") ?? 0,
                    RatingCount = rc,
                    AvgRating = rc > 0 ? rsum / rc : 0,
                    Suggestion = s.Value<string>("lastSuggestion") ?? ""
                };
            }

            private struct SkillStat
            {
                public int Selected;
                public int Used;
                public double AvgRating;
                public int RatingCount;
                public string Suggestion;
            }
        }
    }
}