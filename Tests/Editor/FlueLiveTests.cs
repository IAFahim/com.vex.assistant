using System.Collections;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Vex.Codex.Editor;

namespace Vex.Assistant.Tests
{
    public class FlueLiveTests
    {
        private const double TimeoutSeconds = 150;

        [UnityTest]
        public IEnumerator Chat_workflow_real_model_returns_requested_token()
        {
            var flueDir = CodexSettings.Load().FlueDir;
            if (string.IsNullOrEmpty(flueDir) || !Directory.Exists(flueDir))
                Assert.Ignore("flue_dir not set (~/.unity-codex/settings.json) — skipping live LLM test.");
            if (!File.Exists(Path.Combine(flueDir, "dist", "server.mjs")))
                Assert.Ignore("flue dist not built (dist/server.mjs missing) — skipping live LLM test.");

            MemoryCard card = null;
            string error = null;
            var done = false;

            var payload = new JObject { ["request"] = "Reply with exactly one word and nothing else: pong" };

            var handle = FlueService.Run("chat", payload, flueDir, new FlueCallbacks
            {
                OnProgress = _ => { },
                OnResult = c =>
                {
                    card = c;
                    done = true;
                },
                OnError = e =>
                {
                    error = e;
                    done = true;
                }
            });

            var deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;
            while (!done && EditorApplication.timeSinceStartup < deadline)
                yield return null;

            if (!done)
            {
                handle.Cancel();
                Assert.Fail(
                    $"flue chat did not return within {TimeoutSeconds}s (Node on the Editor's PATH? dist fresh?).");
            }

            Assert.IsNull(error, "flue transport/LLM error: " + error);
            Assert.IsNotNull(card, "flue returned no parseable card.");
            Assert.IsFalse(string.IsNullOrEmpty(card.Explanation), "the model returned an empty answer.");
            StringAssert.Contains("pong", card.Explanation.ToLowerInvariant(),
                "the model did not produce the requested token — answer was: " + card.Explanation);

            Debug.Log($"[FlueLiveTests] chat ok={card.Ok} model={card.Model} answer={card.Explanation}");
        }
    }
}