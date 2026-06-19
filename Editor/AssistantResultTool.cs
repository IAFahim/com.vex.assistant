using Newtonsoft.Json.Linq;
using UnityCliConnector;

namespace Vex.Assistant.Editor
{
    /// <summary>
    /// unity-cli poll: fetch the state of a flue agent job started by assistant_run (backend=flue). Returns
    /// running (with the latest progress tail), error (with the message), or done (with the full memory card:
    /// ok / explanation / code / result / undo / gaps / session). Poll until status != "running".
    /// </summary>
    [UnityCliTool(
        Name = "assistant_result",
        Group = "vex",
        Description = "Poll a flue agent job started by assistant_run (backend=flue): status running|done|error, with the memory card when done. Poll until status is not 'running'.")]
    public static class AssistantResultTool
    {
        public class Parameters
        {
            [ToolParameter("The jobId returned by assistant_run.")]
            public string Job { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            string id = (string)@params["job"];
            if (string.IsNullOrEmpty(id))
                return new ErrorResponse("Required param 'job' is missing (the jobId from assistant_run).", new { code = "MISSING_PREREQUISITE" });

            var job = FlueJobs.Get(id);
            if (job == null)
                return new ErrorResponse($"No flue job '{id}' (it may predate the last domain reload).", new { code = "NOT_FOUND" });

            switch (job.Status)
            {
                case "done":
                    var data = FlueJobs.CardData(job.Agent, job.Card);
                    data["jobId"] = job.Id;
                    data["status"] = "done";
                    return new SuccessResponse(
                        string.IsNullOrEmpty(job.Card.Explanation) ? "flue job done" : job.Card.Explanation, data);

                case "error":
                    return new ErrorResponse($"flue job failed: {job.Error}",
                        new { code = "RUN_FAILED", jobId = job.Id, status = "error", progress = FlueJobs.Tail(job.Progress, 20) });

                default:
                    return new SuccessResponse("flue job running",
                        new { jobId = job.Id, agent = job.Agent, status = "running", progress = FlueJobs.Tail(job.Progress, 12) });
            }
        }
    }
}
