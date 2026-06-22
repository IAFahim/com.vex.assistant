using System.Collections.Generic;
using System.IO;
using Unity.AI.Assistant.Editor;
using Unity.AI.Assistant.Skills;
using UnityEditor;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Vex.Assistant.Editor
{
    [InitializeOnLoad]
    internal static class VexPackageSkills
    {
        private const string k_VexTag = "Skills.Vex.PackagePlugins";
        private static bool s_Reasserting;

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
                s.Tags.Add(k_VexTag);

            SkillsRegistry.RemoveByTag(k_VexTag);
            if (skills.Count > 0)
                SkillsRegistry.AddSkills(skills);
        }

        private static void OnUnityRescanned()
        {
            if (s_Reasserting)
                return;
            s_Reasserting = true;
            try
            {
                Apply();
            }
            finally
            {
                s_Reasserting = false;
            }
        }
    }
}