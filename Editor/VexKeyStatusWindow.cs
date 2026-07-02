using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Vex.Codex.Editor;

namespace Vex.Assistant.Editor
{
    internal static class VexKeysSettings
    {
        [SettingsProvider]
        public static SettingsProvider Create()
        {
            var panel = new Panel();
            return new SettingsProvider("Project/AI/Vex Keys & Models", SettingsScope.Project)
            {
                label = "Vex Keys & Models",
                activateHandler = (_, __) => panel.Activate(),
                guiHandler = panel.OnGui,
                keywords = new HashSet<string>(new[]
                    { "vex", "key", "vault", "model", "flue", "glm", "minimax", "ai", "api" })
            };
        }

        private sealed class Panel
        {
            private static readonly string[] k_Kinds = { "glm", "minimax" };
            private static readonly string[] k_Plans = { "", "lite", "pro", "max" };
            private static readonly string[] k_PlanLabels = { "(not set)", "lite", "pro", "max" };
            private static readonly string[] k_ChatModels = { "default", "glm", "glm:glm-5.2" };
            private static readonly string[] k_ChatLabels = { "MiniMax (default)", "GLM (auto + fallback)", "GLM 5.2" };
            private bool m_Dirty;

            private List<VexKeyVault.Key> m_Keys;
            private string m_LockError;
            private VexKeyVault.Policy m_Policy;
            private string m_PwInput = "";
            private string m_Status = "Click Refresh to load usage.";

            public void Activate()
            {
                m_PwInput = VexKeyVault.MasterPassword;
                Reload();
            }

            private void Reload()
            {
                m_Policy = VexKeyVault.LoadPolicy();
                m_Keys = VexKeyVault.LoadKeys(out m_LockError);
                m_Dirty = false;
            }

            public void OnGui(string searchContext)
            {
                EditorGUILayout.Space(4);

                DrawMasterPassword();

                EditorGUILayout.Space(6);
                if (m_Keys == null)
                {
                    EditorGUILayout.HelpBox(m_LockError ?? "Locked.", MessageType.Warning);
                    return;
                }

                DrawKeysSection();
                DrawSaveBar();
                DrawRoutingPolicy();
                DrawUsageSection();
            }

            private void DrawMasterPassword()
            {
                EditorGUILayout.LabelField("Master password (machine-local, never committed)", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    m_PwInput = EditorGUILayout.PasswordField("Master password", m_PwInput);
                    var locked = m_Keys == null;
                    if (GUILayout.Button(locked ? "Unlock" : "Apply", GUILayout.Width(70)))
                    {
                        var wasUnlocked = m_Keys != null;
                        VexKeyVault.MasterPassword = m_PwInput;
                        if (wasUnlocked)
                            // The new password is already persisted to EditorPrefs by the setter above; re-encrypt
                            // the on-disk blob NOW so it stays decryptable with that password. Just marking dirty
                            // would strand the vault (old-password blob + new-password prefs) if the user quits first.
                            SaveKeys();
                        else Reload();
                        GUI.FocusControl(null);
                    }
                }

                EditorGUILayout.HelpBox(
                    "Keys are AES-256 encrypted with this password and stored in ProjectSettings/VexKeyVault.json (safe " +
                    "to commit & sync). The password lives only in EditorPrefs — enter the same one on each machine.",
                    MessageType.Info);
            }

            private void DrawKeysSection()
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"Keys ({m_Keys.Count})", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("+ Add GLM", GUILayout.Width(90))) AddKey("glm");
                    if (GUILayout.Button("+ Add MiniMax", GUILayout.Width(110))) AddKey("minimax");
                }

                var removeAt = -1;
                for (var i = 0; i < m_Keys.Count; i++)
                    if (DrawKeyCard(m_Keys[i]))
                        removeAt = i;
                if (removeAt >= 0)
                {
                    m_Keys.RemoveAt(removeAt);
                    m_Dirty = true;
                }
            }

            private bool DrawKeyCard(VexKeyVault.Key k)
            {
                var remove = false;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUI.BeginChangeCheck();
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        k.label = EditorGUILayout.TextField(k.label, GUILayout.MinWidth(80));
                        k.kind = k_Kinds[
                            EditorGUILayout.Popup(Array.IndexOf(k_Kinds, k.kind) is var ki && ki >= 0 ? ki : 0, k_Kinds,
                                GUILayout.Width(80))];
                        if (GUILayout.Button("✕", GUILayout.Width(22))) remove = true;
                    }

                    k.apiKey = EditorGUILayout.PasswordField("API key", k.apiKey);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        k.model = EditorGUILayout.TextField("Model", k.model);
                        EditorGUILayout.LabelField("Priority", GUILayout.Width(52));
                        k.priority = EditorGUILayout.IntField(k.priority, GUILayout.Width(44));
                    }

                    if (k.kind == "glm")
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            k.peakGuard =
                                EditorGUILayout.ToggleLeft("Pause at peak", k.peakGuard, GUILayout.Width(120));
                            k.scope = EditorGUILayout.TextField("Scope", k.scope);
                        }

                    if (EditorGUI.EndChangeCheck()) m_Dirty = true;
                }

                return remove;
            }

            private void DrawSaveBar()
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!m_Dirty || !VexKeyVault.HasMasterPassword))
                    {
                        if (GUILayout.Button("Save keys (encrypt)")) SaveKeys();
                    }

                    if (GUILayout.Button("Revert")) Reload();
                }

                if (m_Dirty)
                    EditorGUILayout.HelpBox("Unsaved changes — Save to encrypt & write the vault.", MessageType.None);
            }

            private void DrawRoutingPolicy()
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Routing policy", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();
                m_Policy.defaultChatModel =
                    Popup(new GUIContent("Chat window uses", "Model the Assistant-on-flue window opens on."),
                        m_Policy.defaultChatModel, k_ChatModels, k_ChatLabels);
                m_Policy.peakGuard =
                    EditorGUILayout.Toggle(new GUIContent("Pause GLM at peak", "Skip GLM 14:00–18:00 UTC+8 → MiniMax."),
                        m_Policy.peakGuard);
                m_Policy.plan = Popup("Plan (for est.)", m_Policy.plan, k_Plans, k_PlanLabels);
                if (EditorGUI.EndChangeCheck()) VexKeyVault.SavePolicy(m_Policy);
            }

            private void DrawUsageSection()
            {
                EditorGUILayout.Space(8);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Usage", EditorStyles.boldLabel);
                    if (GUILayout.Button("Refresh", GUILayout.Width(70))) Refresh();
                    if (GUILayout.Button("Open Assistant on flue", GUILayout.Width(160)))
                        AssistantWindowTool.OpenOnFlueMenu();
                }

                EditorGUILayout.SelectableLabel(m_Status, EditorStyles.wordWrappedLabel, GUILayout.MinHeight(120));
            }

            private void AddKey(string kind)
            {
                m_Keys.Add(new VexKeyVault.Key
                {
                    id = $"{kind}-{m_Keys.Count + 1}",
                    kind = kind,
                    label = kind == "minimax" ? "MiniMax" : $"GLM {m_Keys.Count + 1}",
                    model = kind == "minimax" ? "MiniMax-M2.7" : "glm-4.7",
                    priority = (m_Keys.Count + 1) * 10,
                    peakGuard = kind == "glm"
                });
                m_Dirty = true;
            }

            private void SaveKeys()
            {
                var seen = new HashSet<string>();
                foreach (var k in m_Keys)
                {
                    if (string.IsNullOrWhiteSpace(k.id))
                        k.id = string.IsNullOrWhiteSpace(k.label)
                            ? Guid.NewGuid().ToString("N").Substring(0, 6)
                            : k.label;
                    var baseId = k.id;
                    var n = 1;
                    while (!seen.Add(k.id)) k.id = $"{baseId}-{++n}";
                }

                VexKeyVault.SaveKeys(m_Keys);
                m_Dirty = false;
            }

            private static string Popup(string label, string value, string[] values, string[] labels)
            {
                return Popup(new GUIContent(label), value, values, labels);
            }

            private static string Popup(GUIContent label, string value, string[] values, string[] labels)
            {
                var rawIdx = Array.IndexOf(values, value ?? "");
                var idx = Mathf.Max(0, rawIdx);
                var contents = new GUIContent[labels.Length];
                for (var i = 0; i < labels.Length; i++) contents[i] = new GUIContent(labels[i]);
                var sel = EditorGUILayout.Popup(label, idx, contents);
                if (sel == idx && rawIdx < 0) return value;
                return values[Mathf.Clamp(sel, 0, values.Length - 1)];
            }

            private void Refresh()
            {
                var flueDir = CodexSettings.Load().FlueDir;
                if (string.IsNullOrEmpty(flueDir))
                {
                    m_Status = "flue directory is not set (Codex Designer ▸ Settings).";
                    return;
                }

                m_Status = "Loading…";
                FlueService.Run("keys", new { }, flueDir, new FlueCallbacks
                {
                    OnResult = card =>
                    {
                        m_Status = string.IsNullOrEmpty(card.Explanation) ? card.Result : card.Explanation;
                    },
                    OnError = msg => { m_Status = "flue error: " + msg; }
                });
            }
        }
    }
}