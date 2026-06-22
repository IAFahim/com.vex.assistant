using System.Collections.Generic;

namespace Vex.Assistant.Editor
{
    internal sealed class VexAgentDef
    {
        public string Description;
        public string FlueTrack;

        public string FlueWorkflow;
        public string Id;
        public string Name;
    }

    internal static class VexAgents
    {
        public static readonly IReadOnlyList<VexAgentDef> All = new List<VexAgentDef>
        {
            new()
            {
                Id = "timeline-author",
                Name = "Timeline Track Author",
                Description =
                    "Authors DOTS Timeline tracks on a director using the deterministic vex tools, with verification and a replayable undo journal.",

                FlueWorkflow = "track-task",
                FlueTrack = "__boss__"
            }
        };

        public static VexAgentDef Find(string id)
        {
            foreach (var a in All)
                if (a.Id == id)
                    return a;
            return null;
        }
    }
}