using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Vex.Codex.Editor;

namespace Vex.Assistant.Editor
{
    internal sealed class FlueJob
    {
        public readonly List<string> Progress = new();
        public string Agent;
        public MemoryCard Card;
        public string Error;
        public string Id;
        public string Status = "running";
    }

    internal static class FlueJobs
    {
        private const int MaxRetainedJobs = 64;
        private static readonly Dictionary<string, FlueJob> s_Jobs = new();

        private static readonly Queue<string> s_Order = new();
        private static readonly object s_Gate = new();

        public static FlueJob Create(string agent)
        {
            var job = new FlueJob
            {
                Id = $"flue-{agent}-{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                Agent = agent
            };
            lock (s_Gate)
            {
                EvictFinished();
                s_Jobs[job.Id] = job;
                s_Order.Enqueue(job.Id);
            }

            return job;
        }

        private static void EvictFinished()
        {
            var scan = s_Order.Count;
            while (s_Jobs.Count >= MaxRetainedJobs && scan-- > 0)
            {
                var id = s_Order.Dequeue();
                if (!s_Jobs.TryGetValue(id, out var j))
                    continue;
                if (j.Status == "running")
                    s_Order.Enqueue(id);
                else
                    s_Jobs.Remove(id);
            }
        }

        public static FlueJob Get(string id)
        {
            lock (s_Gate)
            {
                if (s_Jobs.TryGetValue(id, out var j))
                    return j;
            }

            return LoadFromDisk(id);
        }

        public static string JobFilePath(string id)
        {
            var dir = CodexSettings.Load()?.FlueDir;
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, ".vex-jobs", id + ".json");
        }

        private static FlueJob LoadFromDisk(string id)
        {
            try
            {
                var path = JobFilePath(id);
                if (path == null || !File.Exists(path))
                    return null;
                var root = JObject.Parse(File.ReadAllText(path));
                var status = (string)root["status"] ?? "running";
                var job = new FlueJob { Id = id, Agent = (string)root["agent"] ?? AgentFromId(id), Status = status };

                if (root["progress"] is JArray progress)
                    foreach (var line in progress)
                        if (line != null && line.Type != JTokenType.Null)
                            job.Progress.Add(line.ToString());
                if (status == "done")
                {
                    var result = root["result"];
                    if (result != null && result.Type != JTokenType.Null)
                        job.Card = MemoryCard.FromStdout(result.ToString());
                    job.Card ??= new MemoryCard { Ok = true };
                }
                else if (status == "error")
                {
                    job.Error = (string)root["error"] ?? "flue failed";
                }

                return job;
            }
            catch
            {
                return null;
            }
        }

        public static Dictionary<string, object> CardData(string agent, MemoryCard card)
        {
            return new Dictionary<string, object>
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

                ["skills"] = card.Skills,
                ["raw"] = card.RawJson
            };
        }

        public static string[] Tail(IReadOnlyList<string> lines, int n)
        {
            return lines.Skip(Math.Max(0, lines.Count - n)).ToArray();
        }

        private static string AgentFromId(string id)
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