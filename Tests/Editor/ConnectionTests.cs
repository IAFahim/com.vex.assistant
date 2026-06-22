using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Vex.Assistant.Editor;
using Vex.Codex.Editor;

namespace Vex.Assistant.Tests
{
    public class ConnectionTests
    {
        [Test]
        public void Connector_and_vex_tools_are_loaded()
        {
            Assert.IsTrue(VexService.Available,
                "unity-cli connector / vex tools not loaded (tool_undo handler not found).");
        }

        [Test]
        public void Vex_schemas_list_the_timeline_authoring_tools()
        {
            var r = VexService.Call("vex_schemas", new JObject());
            Assert.IsTrue(r.Ok, r.Message);

            var names = (r.Data?["tools"] as JArray)?.Select(t => (string)t["name"]).ToList();
            Assert.IsNotNull(names, "vex_schemas returned no tools array.");
            CollectionAssert.Contains(names, "track_author");
            CollectionAssert.Contains(names, "tool_undo");
            CollectionAssert.Contains(names, "director_inspect");
        }

        [Test]
        public void In_process_dispatch_reaches_a_real_tool()
        {
            var r = VexService.Call("director_inspect", new JObject());
            StringAssert.DoesNotContain("Unknown vex tool", r.Message,
                "in-process dispatch did not reach a real tool.");
        }

        [Test]
        public void Agent_list_contains_timeline_author()
        {
            var r = VexService.Call("agent_list", new JObject());
            Assert.IsTrue(r.Ok, r.Message);

            var agents = r.Data?["agents"] as JArray;
            Assert.IsNotNull(agents);
            Assert.IsTrue(agents.Any(a => (string)a["id"] == "timeline-author"),
                "timeline-author agent not in the roster.");
        }

        [Test]
        public void Agent_registry_routes_to_the_flue_boss()
        {
            var def = VexAgents.Find("timeline-author");
            Assert.IsNotNull(def);
            Assert.AreEqual("track-task", def.FlueWorkflow);
            Assert.AreEqual("__boss__", def.FlueTrack);
        }

        [Test]
        public void Chat_history_round_trips_and_caps_turns()
        {
            var conv = "test-" + Guid.NewGuid().ToString("N");
            VexChatHistory.Append(conv, "hello", "hi there");

            StringAssert.Contains("User: hello", VexChatHistory.Context(conv));
            StringAssert.Contains("hi there", VexChatHistory.Context(conv));

            for (var i = 0; i < 20; i++)
                VexChatHistory.Append(conv, "q" + i, "a" + i);

            Assert.LessOrEqual(VexChatHistory.Get(conv).Count, 8, "transcript should cap at k_MaxTurns.");
        }

        [Test]
        public void Chat_history_truncates_long_turns()
        {
            var conv = "test-" + Guid.NewGuid().ToString("N");
            VexChatHistory.Append(conv, "q", new string('x', 5000));

            var stored = VexChatHistory.Get(conv)[0];
            Assert.Less(stored.Length, 2000, "a long turn should be truncated.");
            StringAssert.Contains("…", stored);
        }

        [Test]
        public void Flue_job_lifecycle_and_card_view()
        {
            var job = FlueJobs.Create("timeline-author");
            Assert.AreEqual("running", job.Status);
            Assert.AreSame(job, FlueJobs.Get(job.Id));

            var card = MemoryCard.FromStdout(
                new JObject { ["ok"] = true, ["explanation"] = "done", ["model"] = "m" }.ToString());

            var data = FlueJobs.CardData("timeline-author", card);
            Assert.AreEqual("timeline-author", data["agent"]);
            Assert.AreEqual(true, data["ok"]);
            Assert.AreEqual("m", data["model"]);
        }
    }
}