using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Vex.Codex.Editor;

namespace Vex.Assistant.Tests
{
    public class MemoryCardTests
    {
        private static string Pretty(JObject o)
        {
            return o.ToString();
        }

        [Test]
        public void Parses_clean_card_fields()
        {
            var c = MemoryCard.FromStdout(Pretty(new JObject
            {
                ["ok"] = true,
                ["track"] = "chat",
                ["request"] = "hi",
                ["explanation"] = "did the thing",
                ["code"] = "// c#",
                ["result"] = "SUBSCENE|x|ok",
                ["repairRounds"] = 2,
                ["undo"] = new JArray("step b", "step a"),
                ["gaps"] = "none",
                ["session"] = "abc",
                ["model"] = "minimax/MiniMax-M2.7",
                ["skills"] = new JArray("unity-cli")
            }));

            Assert.IsNotNull(c);
            Assert.IsTrue(c.Ok);
            Assert.AreEqual("chat", c.Track);
            Assert.AreEqual("did the thing", c.Explanation);
            Assert.AreEqual(2, c.RepairRounds);
            Assert.AreEqual(2, c.Undo.Count);
            Assert.AreEqual("minimax/MiniMax-M2.7", c.Model);
            Assert.AreEqual("abc", c.Session);
        }

        [Test]
        public void Parses_last_object_after_progress_noise()
        {
            var stdout = "[flue] building\n[flue] Run ID: 123\n" +
                         Pretty(new JObject { ["ok"] = true, ["explanation"] = "answer" });

            var c = MemoryCard.FromStdout(stdout);
            Assert.IsNotNull(c);
            Assert.IsTrue(c.Ok);
            Assert.AreEqual("answer", c.Explanation);
        }

        [Test]
        public void Structured_undo_journal_is_replayable()
        {
            var c = MemoryCard.FromStdout(Pretty(new JObject
            {
                ["ok"] = true,
                ["undo"] = new JArray(
                    new JObject
                    {
                        ["tool"] = "asset_delete", ["params"] = new JObject { ["asset"] = "Assets/X.playable" }
                    },
                    new JObject { ["tool"] = "director_bind", ["params"] = new JObject() })
            }));

            Assert.IsNotNull(c);
            Assert.IsTrue(c.HasReplayableUndo);
            Assert.IsNotNull(c.UndoJournal);
            Assert.AreEqual(2, c.UndoJournal.Count);
        }

        [Test]
        public void String_undo_is_listed_but_not_replayable()
        {
            var c = MemoryCard.FromStdout(Pretty(new JObject
            {
                ["ok"] = true,
                ["undo"] = new JArray("// undo c# a", "// undo c# b")
            }));

            Assert.IsNotNull(c);
            Assert.IsFalse(c.HasReplayableUndo);
            Assert.AreEqual(2, c.Undo.Count);
        }

        [Test]
        public void Garbage_and_empty_return_null()
        {
            Assert.IsNull(MemoryCard.FromStdout("not json at all"));
            Assert.IsNull(MemoryCard.FromStdout(""));
        }

        [Test]
        public void Detects_stopped_short_prerequisite()
        {
            var c = MemoryCard.FromStdout(Pretty(new JObject
            {
                ["ok"] = false,
                ["result"] = "MISSING_PREREQUISITE: no director in the subscene"
            }));

            Assert.IsNotNull(c);
            Assert.IsTrue(c.StoppedShort);
        }
    }
}