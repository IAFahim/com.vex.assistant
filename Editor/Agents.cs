using System.Collections.Generic;

namespace Vex.Assistant.Editor
{
    /// <summary>A specialized vex agent: id + display + the flue route it maps to.</summary>
    internal sealed class VexAgentDef
    {
        public string Id;
        public string Name;
        public string Description;

        /// <summary>flue routing: the workflow + track key this agent maps to in flue-pipeline.
        /// Null FlueWorkflow means this agent has no flue route.</summary>
        public string FlueWorkflow;
        public string FlueTrack;
    }

    /// <summary>
    /// Registry of vex specialized agents, routed to flue-pipeline by assistant_run / listed by agent_list.
    /// (Ships one; more are added here.)
    /// </summary>
    internal static class VexAgents
    {
        public static readonly IReadOnlyList<VexAgentDef> All = new List<VexAgentDef>
        {
            new VexAgentDef
            {
                Id = "timeline-author",
                Name = "Timeline Track Author",
                Description = "Authors DOTS Timeline tracks on a director using the deterministic vex tools, with verification and a replayable undo journal.",
                // flue route: the track-task workflow carrying every track mastery skill (the "boss"),
                // so this one agent can author any DOTS Timeline track family via the user's own model.
                FlueWorkflow = "track-task",
                FlueTrack = "__boss__",
            },
        };

        public static VexAgentDef Find(string id)
        {
            foreach (var a in All)
                if (a.Id == id) return a;
            return null;
        }
    }
}
