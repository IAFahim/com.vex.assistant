using System.Collections.Generic;
using System.IO;
using Unity.AI.Assistant.Editor;       // SkillsScanner
using Unity.AI.Assistant.Skills;       // SkillsRegistry, SkillUtils, SkillDefinition, SkillRegistryTags, SkillFileIssue
using UnityEditor;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Vex.Assistant.Editor
{
    /// <summary>
    /// Surfaces per-package skills shipped the repo-owned way — <c>&lt;package&gt;/Plugins~/skills/&lt;name&gt;/SKILL.md</c>
    /// (the trailing <c>~</c> keeps the folder out of the AssetDatabase/build; also consumable by Claude Code) — in
    /// the Unity AI Assistant's <b>Manage Skills</b> window. Unity natively scans only <c>&lt;package&gt;/AIAssistantSkills/</c>,
    /// so this bridge adds the <c>Plugins~/skills/</c> convention. No fork edits: friend access only.
    ///
    /// SCOPE: ALL registered packages (your embedded Timeline packages AND lib/cached packages like app-ui, bl-core, …)
    /// — Manage Skills lists everything and you Allow/Deny per skill, so a full catalog is the useful default.
    ///
    /// IDEMPOTENT: our skills carry a dedicated tag (for clear-then-add management) plus the Package tag (so they render
    /// in the "Package skills" section). We RemoveByTag(ours)+AddSkills every run, so repeated scans never duplicate.
    /// Unity's own package scan does ReplaceSkillsByTag(Package, …) which clears the Package tag (and thus ours), so we
    /// re-assert after each Unity rescan.
    /// </summary>
    [InitializeOnLoad]
    internal static class VexPackageSkills
    {
        const string k_VexTag = "Skills.Vex.PackagePlugins";
        static bool s_Reasserting;

        static VexPackageSkills()
        {
            EditorApplication.delayCall += Apply;
            SkillsScanner.OnSkillsRescanned += OnUnityRescanned;
        }

        [MenuItem("Vex/Rescan Package Skills (Plugins~/skills)")]
        public static void Apply()
        {
            var skills = new List<SkillDefinition>();
            var issues = new List<SkillFileIssue>();

            foreach (var pkg in UpmPackageInfo.GetAllRegisteredPackages())
            {
                if (pkg == null || string.IsNullOrEmpty(pkg.resolvedPath))
                    continue;
                var folder = Path.Combine(pkg.resolvedPath, "Plugins~", "skills");
                if (Directory.Exists(folder))
                    SkillUtils.LoadSkillsFromFolder(folder, SkillRegistryTags.Package, skills, issues);
            }

            foreach (var s in skills)
                s.Tags.Add(k_VexTag); // mark ours so the clear-then-add below is idempotent

            SkillsRegistry.RemoveByTag(k_VexTag);
            if (skills.Count > 0)
                SkillsRegistry.AddSkills(skills);
        }

        // Unity's package scan does ReplaceSkillsByTag(Package, …), clearing our skills; re-add them. AddSkills does not
        // itself fire OnSkillsRescanned, so this cannot loop; the guard is belt-and-suspenders.
        static void OnUnityRescanned()
        {
            if (s_Reasserting)
                return;
            s_Reasserting = true;
            try { Apply(); }
            finally { s_Reasserting = false; }
        }
    }
}
