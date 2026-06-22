using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Vex.Codex.Editor;

namespace Vex.Assistant.Editor
{
    /// <summary>
    /// One in-flight (or finished) flue agent run started by assistant_run (backend=flue).
    ///
    /// flue runs in a Node subprocess that mutates THIS Editor by calling unity-cli — which re-enters the connector's
    /// CommandRouter. CommandRouter serializes every command through a non-reentrant lock, so assistant_run MUST NOT
    /// hold that lock while flue runs (it would deadlock flue's own unity-cli exec). Hence the job model: assistant_run
    /// starts the run and returns immediately (releasing the lock); assistant_result polls this record.
    /// </summary>
    internal sealed class FlueJob
    {
        public string Id;
        public string Agent;
        public string Status = "running"; // running | done | error
        public MemoryCard Card;
        public string Error;
        public readonly List<string> Progress = new List<string>();
    }

    internal static class FlueJobs
    {
        static readonly Dictionary<string, FlueJob> s_Jobs = new Dictionary<string, FlueJob>();
        // Insertion order of s_Jobs ids, so Create can evict the OLDEST finished jobs first (FIFO) once the registry
        // exceeds the cap — bounding per-session growth of retained MemoryCards (incl. full RawJson) + Progress lines.
        static readonly Queue<string> s_Order = new Queue<string>();
        static readonly object s_Gate = new object();

        // Soft cap on retained jobs. A finished (done/error) job stays recoverable from its durable on-disk file after
        // eviction, and a still-running job is never evicted (its callbacks still write to it), so this is leak-safe.
        const int MaxRetainedJobs = 64;

        public static FlueJob Create(string agent)
        {
            var job = new FlueJob
            {
                Id = $"flue-{agent}-{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                Agent = agent,
            };
            lock (s_Gate)
            {
                EvictFinished();
                s_Jobs[job.Id] = job;
                s_Order.Enqueue(job.Id);
            }
            return job;
        }

        // Caller must hold s_Gate. Drops the oldest FINISHED (done/error) jobs while the registry is over the cap;
        // running jobs are skipped (kept, re-enqueued at the back) so an in-flight run's callbacks are never orphaned.
        static void EvictFinished()
        {
            int scan = s_Order.Count;
            while (s_Jobs.Count >= MaxRetainedJobs && scan-- > 0)
            {
                var id = s_Order.Dequeue();
                if (!s_Jobs.TryGetValue(id, out var j))
                    continue; // already evicted / stale id
                if (j.Status == "running")
                    s_Order.Enqueue(id); // still in-flight — keep it, revisit later
                else
                    s_Jobs.Remove(id); // finished — durable disk file still covers post-eviction Get()
            }
        }

        public static FlueJob Get(string id)
        {
            lock (s_Gate)
                if (s_Jobs.TryGetValue(id, out var j))
                    return j;
            // In-memory miss — most likely a domain reload wiped the registry mid-run. Recover from the durable file
            // the node process writes (it survives the reload), so a long authoring run's result isn't lost.
            return LoadFromDisk(id);
        }

        /// <summary>Per-job durable file the node process mirrors its status+result to (survives domain reloads).</summary>
        public static string JobFilePath(string id)
        {
            var dir = CodexSettings.Load()?.FlueDir;
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, ".vex-jobs", id + ".json");
        }

        static FlueJob LoadFromDisk(string id)
        {
            try
            {
                var path = JobFilePath(id);
                if (path == null || !File.Exists(path))
                    return null;
                var root = JObject.Parse(File.ReadAllText(path));
                var status = (string)root["status"] ?? "running";
                var job = new FlueJob { Id = id, Agent = (string)root["agent"] ?? AgentFromId(id), Status = status };
                // Restore any progress the node process mirrored to disk, so a still-running recovered job's tail isn't
                // empty after a reload (the disk file exists precisely to bridge that reload).
                if (root["progress"] is JArray progress)
                    foreach (var line in progress)
                        if (line != null && line.Type != JTokenType.Null)
                            job.Progress.Add(line.ToString());
                if (status == "done")
                {
                    var result = root["result"];
                    if (result != null && result.Type != JTokenType.Null)
                        job.Card = MemoryCard.FromStdout(result.ToString());
                    job.Card ??= new MemoryCard { Ok = true }; // 'done' with a null/unparsable result
                }
                else if (status == "error")
                {
                    job.Error = (string)root["error"] ?? "flue failed";
                }
                return job;
            }
            catch { return null; }
        }

        /// <summary>The memory-card view shared by assistant_run (Unity backend has no card) and assistant_result.</summary>
        public static Dictionary<string, object> CardData(string agent, MemoryCard card) => new Dictionary<string, object>
        {
            ["agent"] = agent,
            ["backend"] = "flue",
            ["ok"] = card.Ok,
            ["track"] = card.Track,
            ["explanation"] = card.Explanation,
            ["code"] = card.Code,
            ["result"] = card.Result,
            ["repairRounds"] = card.RepairRounds,
            ["undo"] = card.Undo,
            ["gaps"] = card.Gaps,
            ["session"] = card.Session,
            ["model"] = card.Model,
            // forge superset (when the chat turn routed to the validation loop): the curated skills + the full raw
            // envelope, so a CLI caller sees the judge verdict / skillRationale / skillRatings / skillSuggestions that
            // MemoryCard has no typed field for. Lossless — `raw` is the exact JSON flue returned.
            ["skills"] = card.Skills,
            ["raw"] = card.RawJson,
        };

        public static string[] Tail(IReadOnlyList<string> lines, int n) =>
            lines.Skip(Math.Max(0, lines.Count - n)).ToArray();

        // jobIds are minted as `flue-{agent}-{8hexGuid}` (see Create). Recover the agent (which may itself contain
        // hyphens, e.g. timeline-author) by stripping the `flue-` prefix and the trailing `-{guid}` segment. Used as the
        // fallback when a recovered-from-disk job file has no explicit `agent` field.
        static string AgentFromId(string id)
        {
            if (string.IsNullOrEmpty(id) || !id.StartsWith("flue-"))
                return "chat";
            var inner = id.Substring("flue-".Length);
            var lastDash = inner.LastIndexOf('-');
            var agent = lastDash > 0 ? inner.Substring(0, lastDash) : inner;
            return string.IsNullOrEmpty(agent) ? "chat" : agent;
        }
    }
}
