using Newtonsoft.Json.Linq;
using UnityCliConnector;
using Vex.Codex.Editor;

namespace Vex.Assistant.Editor
{
    /// <summary>
    /// unity-cli entrypoint for the in-Editor chat — "type text, hit enter" from the command line. Runs the EXACT
    /// same flue <c>chat</c> workflow the Assistant-on-flue window uses, so a plain prompt flows through the whole
    /// pipeline: the chat agent classifies it and ROUTES build/author requests to the forge validation loop
    /// (skill-discovery → curated coder → judge → reflection), while questions / inspection / trivial edits stay
    /// single-pass. The full memory card (incl. the forge superset: curated <c>skills</c>, the judge verdict, and the
    /// raw envelope) comes back via <c>assistant_result</c>.
    ///
    /// Fire-and-forget by necessity: the flue process calls unity-cli back into the SAME serialized connector, so
    /// awaiting here would deadlock (flue's exec waits on the lock this command holds). We start the job and return
    /// immediately, releasing the lock; <c>assistant_result {"job":"…"}</c> polls until status != "running".
    /// </summary>
    [UnityCliTool(
        Name = "assistant_chat",
        Group = "vex",
        Description = "Send a chat prompt through the vex flue chat pipeline (auto-routes build/author requests to the forge validation loop). Fire-and-forget: returns a jobId; poll assistant_result {\"job\":\"…\"} for the full memory card. Params: prompt (required), model, session, context.")]
    public static class AssistantChatTool
    {
        public class Parameters
        {
            [ToolParameter("The chat prompt — exactly what you'd type into the Assistant window and hit enter.")]
            public string Prompt { get; set; }

            [ToolParameter("Optional model alias/override: default | glm | glm:glm-5.2 | provider/model. Omit for the default.")]
            public string Model { get; set; }

            [ToolParameter("Optional conversation/session name to thread follow-ups (memory across turns).")]
            public string Session { get; set; }

            [ToolParameter("Optional extra context text prepended to the turn (e.g. a prior memory card or scene notes).")]
            public string Context { get; set; }
        }

        // Reads a string param defensively: a JSON string returns its value, null/missing returns null, and any
        // non-scalar (object/array) token falls through to its text form rather than throwing InvalidCastException
        // (Newtonsoft's explicit (string) cast throws on JObject/JArray). Lets the IsNullOrEmpty guards do their job.
        private static string Str(JToken t) =>
            t == null || t.Type == JTokenType.Null ? null :
            t.Type == JTokenType.String ? (string)t : t.ToString();

        public static object HandleCommand(JObject @params)
        {
            string prompt = Str(@params["prompt"]);
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
                ["context"] = Str(@params["context"]) ?? string.Empty,
            };
            string session = Str(@params["session"]);
            string model = Str(@params["model"]);
            if (!string.IsNullOrEmpty(session)) payload["session"] = session;
            if (!string.IsNullOrEmpty(model)) payload["model"] = model;

            // Bridge the Manage Skills opt-in (same as the window does) so the chat classifier carries the user's
            // allowed skills. The forge route ignores this and runs its own discovery over the full catalog.
            var allowed = VexEnabledSkills.AllowedNames();
            if (allowed.Count > 0) payload["skills"] = new JArray(allowed);

            var job = FlueJobs.Create("chat");
            FlueService.Run("chat", payload, flueDir, new FlueCallbacks
            {
                OnProgress = line => { if (line != null) job.Progress.Add(line); },
                OnResult = card => { job.Card = card; job.Status = "done"; },
                OnError = msg => { job.Error = msg ?? "flue chat failed"; job.Status = "error"; },
            }, jobFile: FlueJobs.JobFilePath(job.Id));

            return new SuccessResponse(
                $"chat job started (model: {(string.IsNullOrEmpty(model) ? "default" : model)}). Poll it with: assistant_result {{\"job\":\"{job.Id}\"}}.",
                new { jobId = job.Id, status = "running" });
        }
    }
}
