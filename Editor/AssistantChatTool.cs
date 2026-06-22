using Newtonsoft.Json.Linq;
using UnityCliConnector;
using Vex.Codex.Editor;

namespace Vex.Assistant.Editor
{
    [UnityCliTool(
        Name = "assistant_chat",
        Group = "vex",
        Description =
            "Send a chat prompt through the vex flue chat pipeline (auto-routes build/author requests to the forge validation loop). Fire-and-forget: returns a jobId; poll assistant_result {\"job\":\"…\"} for the full memory card. Params: prompt (required), model, session, context.")]
    public static class AssistantChatTool
    {
        private static string Str(JToken t)
        {
            return t == null || t.Type == JTokenType.Null ? null :
                t.Type == JTokenType.String ? (string)t : t.ToString();
        }

        public static object HandleCommand(JObject @params)
        {
            var prompt = Str(@params["prompt"]);
            if (string.IsNullOrEmpty(prompt))
                return new ErrorResponse("Required param 'prompt' is missing.", new { code = "MISSING_PREREQUISITE" });

            var flueDir = CodexSettings.Load().FlueDir;
            if (string.IsNullOrEmpty(flueDir))
                return new ErrorResponse(
                    "flue directory is not set (~/.unity-codex/settings.json 'flue_dir'); set it in the Codex Designer window's Settings.",
                    new { code = "MISSING_PREREQUISITE" });

            var payload = new JObject
            {
                ["request"] = prompt,
                ["context"] = Str(@params["context"]) ?? string.Empty
            };
            var session = Str(@params["session"]);
            var model = Str(@params["model"]);
            if (!string.IsNullOrEmpty(session)) payload["session"] = session;
            if (!string.IsNullOrEmpty(model)) payload["model"] = model;

            var allowed = VexEnabledSkills.AllowedNames();
            if (allowed.Count > 0) payload["skills"] = new JArray(allowed);

            var job = FlueJobs.Create("chat");
            FlueService.Run("chat", payload, flueDir, new FlueCallbacks
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
                    job.Error = msg ?? "flue chat failed";
                    job.Status = "error";
                }
            }, jobFile: FlueJobs.JobFilePath(job.Id));

            return new SuccessResponse(
                $"chat job started (model: {(string.IsNullOrEmpty(model) ? "default" : model)}). Poll it with: assistant_result {{\"job\":\"{job.Id}\"}}.",
                new { jobId = job.Id, status = "running" });
        }

        public class Parameters
        {
            [ToolParameter("The chat prompt — exactly what you'd type into the Assistant window and hit enter.")]
            public string Prompt { get; set; }

            [ToolParameter(
                "Optional model alias/override: default | glm | glm:glm-5.2 | provider/model. Omit for the default.")]
            public string Model { get; set; }

            [ToolParameter("Optional conversation/session name to thread follow-ups (memory across turns).")]
            public string Session { get; set; }

            [ToolParameter(
                "Optional extra context text prepended to the turn (e.g. a prior memory card or scene notes).")]
            public string Context { get; set; }
        }
    }
}