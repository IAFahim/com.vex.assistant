using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityCliConnector;
using Vex.Codex.Editor;

namespace Vex.Assistant.Editor
{
    [UnityCliTool(
        Name = "assistant_run",
        Group = "vex",
        Description =
            "Run a vex specialized agent (see agent_list) headlessly via flue-pipeline (your own model, no Unity subscription). Fire-and-forget: returns a jobId; poll assistant_result {\"job\":\"…\"} for the memory card.")]
    public static class AssistantRunTool
    {
        public static object HandleCommand(JObject @params)
        {
            var prompt = (string)@params["prompt"];
            var def = Validate(@params, out var prereqError);
            if (prereqError != null)
                return prereqError;

            var flueDir = CodexSettings.Load().FlueDir;
            if (string.IsNullOrEmpty(flueDir))
                return new ErrorResponse(
                    "flue directory is not set (~/.unity-codex/settings.json 'flue_dir'); set it in the Codex Designer window's Settings.",
                    new { code = "MISSING_PREREQUISITE" });

            var refs = (@params["refs"] as JArray)?
                .Select(r => r?.ToString())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList() ?? new List<string>();

            var payload = new JObject
            {
                ["track"] = def.FlueTrack,
                ["request"] = prompt,
                ["context"] = ComposeContext((string)@params["context"], refs)
            };
            var session = (string)@params["session"];
            var model = (string)@params["model"];
            if (!string.IsNullOrEmpty(session)) payload["session"] = session;
            if (!string.IsNullOrEmpty(model)) payload["model"] = model;

            var job = FlueJobs.Create(def.Id);
            FlueService.Run(def.FlueWorkflow, payload, flueDir, new FlueCallbacks
            {
                OnProgress = line =>
                {
                    if (line != null) job.Progress.Add(line);
                },
                OnResult = card =>
                {
                    job.Card = card;
                    job.Status = "done";
                },
                OnError = msg =>
                {
                    job.Error = msg ?? "flue failed";
                    job.Status = "error";
                }
            }, jobFile: FlueJobs.JobFilePath(job.Id));

            return new SuccessResponse(
                $"flue job started for agent '{def.Id}' (model: {(string.IsNullOrEmpty(model) ? "default" : model)}). Poll it with: assistant_result {{\"job\":\"{job.Id}\"}}.",
                new { jobId = job.Id, agent = def.Id, backend = "flue", status = "running" });
        }

        private static VexAgentDef Validate(JObject @params, out object prereqError)
        {
            prereqError = null;

            var agentId = (string)@params["agent"];
            if (string.IsNullOrEmpty(agentId))
            {
                prereqError = new ErrorResponse("Required param 'agent' is missing (see agent_list).",
                    new { code = "MISSING_PREREQUISITE" });
                return null;
            }

            var prompt = (string)@params["prompt"];
            if (string.IsNullOrEmpty(prompt))
            {
                prereqError = new ErrorResponse("Required param 'prompt' is missing.",
                    new { code = "MISSING_PREREQUISITE" });
                return null;
            }

            var def = VexAgents.Find(agentId);
            if (def == null)
            {
                var ids = string.Join(", ", VexAgents.All.Select(a => a.Id));
                prereqError = new ErrorResponse($"No agent '{agentId}'. Available: {ids}", new { code = "NOT_FOUND" });
                return null;
            }

            if (string.IsNullOrEmpty(def.FlueWorkflow))
            {
                prereqError = new ErrorResponse($"Agent '{def.Id}' has no flue route.",
                    new { code = "MISSING_PREREQUISITE" });
                return null;
            }

            return def;
        }

        private static string ComposeContext(string contextText, List<string> refs)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(contextText)) parts.Add(contextText);
            if (refs.Count > 0)
                parts.Add("Reference targets (rediscover these in-project): " + string.Join(", ", refs));
            return string.Join("\n\n", parts);
        }

        public class Parameters
        {
            [ToolParameter("Specialized agent id (see agent_list), e.g. timeline-author.")]
            public string Agent { get; set; }

            [ToolParameter("The prompt / task for the agent.")]
            public string Prompt { get; set; }

            [ToolParameter("Optional model override, e.g. 'minimax/MiniMax-M2.7'. Omit for the agent's default.")]
            public string Model { get; set; }

            [ToolParameter("Optional conversation/session name to thread follow-ups.")]
            public string Session { get; set; }

            [ToolParameter("Optional asset paths to attach as references / targets, e.g. [\"Assets/X.playable\"].")]
            public string[] Refs { get; set; }

            [ToolParameter("Optional raw context text (e.g. a prior memory card) attached to the run.")]
            public string Context { get; set; }
        }
    }
}