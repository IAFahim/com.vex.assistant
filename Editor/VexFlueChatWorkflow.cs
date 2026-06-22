using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.AI.Assistant.Backend;
using Unity.AI.Assistant.Data;
using Unity.AI.Assistant.Socket.Communication;
using Unity.AI.Assistant.Socket.Protocol.Models;
using Unity.AI.Assistant.Socket.Protocol.Models.FromClient;
using Unity.AI.Assistant.Socket.Protocol.Models.FromServer;
using Unity.AI.Assistant.Socket.Workflows.Chat;
using Vex.Codex.Editor;

namespace Vex.Assistant.Editor
{
    /// <summary>
    /// A chat workflow that runs the Assistant window on the user's OWN model via flue, instead of Unity's cloud
    /// websocket. It extends <see cref="BaseChatWorkflow"/> (which owns the state machine + events) and only swaps the
    /// transport: there is no socket — we synthesize the server message sequence the base expects
    /// (DiscussionInitialization → Acknowledgment → ChatResponse fragments) from a flue run.
    ///
    /// Streaming: flue's STRUCTURED output ({answer, code}) means the answer prose is NOT available token-by-token —
    /// it only exists in the final card. What IS live is the model's pre-finish reasoning + status beats, which flue
    /// streams on stderr (text_delta lines are two-space-prefixed; status lines start with "[flue] "). We surface
    /// those live as &lt;THOUGHT&gt; blocks (the window's reasoning surface, rendered above the answer), then drop the
    /// real card in as the AnswerBlock on completion. All callbacks are already marshalled to the Editor main thread
    /// by FlueService, so mutating workflow state + firing events here is valid.
    ///
    /// Context: each prompt carries (a) a snapshot of the current Editor context — what the user attached via the
    /// window's "+" button and the live Hierarchy/Project selection — and (b) the persisted conversation transcript
    /// (<see cref="VexChatHistory"/>), since flue runs each turn as a fresh process with no cross-run memory.
    /// </summary>
    sealed class VexFlueChatWorkflow : BaseChatWorkflow
    {
        const string k_Workflow = "chat";

        // How many streamed reasoning lines to coalesce into a single THOUGHT block before flushing it. Keeps the
        // reasoning surface from exploding into one block per line (the parser can't update an existing thought).
        const int k_ReasoningLinesPerThought = 6;

        readonly string m_Model; // null => flue default model
        FlueHandle m_Handle;

        // Per-turn streaming state. One responseId for the whole turn so thoughts + answer attach to one message and
        // the answer accumulates into a single block.
        string m_ResponseId;
        string m_Prompt;
        bool m_Acknowledged;
        bool m_Finished;
        readonly StringBuilder m_ReasoningBuffer = new();
        int m_ReasoningLinesPending;

        public VexFlueChatWorkflow(string conversationId = null, IFunctionCaller functionCaller = null, string model = null)
            : base(conversationId, functionCaller)
        {
            m_Model = model;
            // When the user clicks Stop the base flips us to Canceling. Hook that to actually KILL the live flue
            // process (otherwise node keeps running to completion, burning model tokens, and its late result tries to
            // emit after the turn is over) and then return to Idle so the input re-enables.
            OnWorkflowStateChanged += OnStateChanged;
        }

        // The Stop button enters Canceling (BaseChatWorkflow.CancelCurrentChatRequest). The subsequent
        // CancelChatRequestV1 the base routes to SendMessageInternal is a no-op for us (no socket), so we handle the
        // teardown here: stop the flue process, swallow any in-flight result, and advance Canceling → Idle (the same
        // transition the real workflow makes on the server's cancel ack) so the spinner clears.
        void OnStateChanged(State s)
        {
            if (s != State.Canceling)
                return;
            // Kill the live flue process immediately so it stops burning tokens, and swallow any late result.
            m_Finished = true;
            try { m_Handle?.Cancel(); } catch { /* process may already be gone */ }
            m_Handle = null;
            // Do NOT assign WorkflowState here. We're inside the base setter's OnWorkflowStateChanged invocation,
            // which fires BEFORE it writes m_WorkflowState — setting Idle now is immediately clobbered back to
            // Canceling when the outer setter resumes. Defer to the next editor tick: by then the field holds
            // Canceling, so the Canceling→Idle transition fires its event cleanly and the spinner clears. Guard so we
            // only force Idle if nothing else moved us on (e.g. a result that raced in).
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (WorkflowState == State.Canceling)
                    WorkflowState = State.Idle;
            };
        }

        /// <summary>Drive the workflow to Idle without any network: synthesize the discussion-init handshake.</summary>
        public void Start()
        {
            try
            {
                if (WorkflowState != State.NotStarted)
                    return;

                WorkflowState = State.AwaitingDiscussionInitialization;
                Receive(new DiscussionInitializationV1
                {
                    ConversationId = string.IsNullOrEmpty(ConversationId) ? Guid.NewGuid().ToString() : ConversationId,
                    ChatTimeoutSeconds = 1200,
                });
                MarkStarted();
            }
            catch (Exception ex)
            {
                MarkStartFailed(ex);
            }
        }

        // BaseChatWorkflow uses Start() above; this abstract hook is never reached (mirrors ChatWorkflow).
        protected override Task StartConnectionInternal(ICredentialsContext credentialsContext, bool skipInitialization)
            => throw new NotImplementedException("VexFlueChatWorkflow uses Start()");

        protected override Task SendMessageInternal(object message, CancellationToken cancellationToken)
        {
            // The base sends a ChatRequestV1 when the user submits a prompt; everything else (cancel, etc.) we ignore.
            // When this runs, the base has already set WorkflowState = AwaitingChatAcknowledgement (see SendChatRequest),
            // so the flue callbacks below are free to synthesize the ack + fragments.
            if (message is ChatRequestV1 req)
                StartFlue(req.Markdown ?? string.Empty, BuildEditorContext(req));
            return Task.CompletedTask;
        }

        void StartFlue(string prompt, string editorContext)
        {
            // Fresh per-turn streaming state.
            m_Prompt = prompt;
            m_ResponseId = Guid.NewGuid().ToString();
            m_Acknowledged = false;
            m_Finished = false;
            m_ReasoningBuffer.Clear();
            m_ReasoningLinesPending = 0;

            var flueDir = CodexSettings.Load().FlueDir;
            if (string.IsNullOrEmpty(flueDir))
            {
                EmitFinalAnswer("⚠️ flue directory is not set (~/.unity-codex/settings.json `flue_dir`). Set it in the Codex Designer window's Settings.");
                return;
            }

            // flue runs each turn as a fresh process (named sessions don't survive across runs), so `context` carries
            // the memory: a snapshot of the current Editor context (attached items + selection) above the persisted
            // conversation transcript.
            var context = Combine(editorContext, VexChatHistory.Context(ConversationId));

            var payload = new JObject
            {
                ["request"] = prompt,
                ["context"] = context,
            };
            if (!string.IsNullOrEmpty(ConversationId)) payload["session"] = ConversationId;
            if (!string.IsNullOrEmpty(m_Model)) payload["model"] = m_Model;

            // Bridge the Manage Skills opt-in into the chat agent: pass the user-allowed skill names so flue's
            // buildChatAgent carries the ones it ships (on top of its always-on operating/knowledge skills). Read on
            // the main thread here (registry + EditorPrefs); flue ignores names it doesn't recognize.
            var allowedSkills = VexEnabledSkills.AllowedNames();
            if (allowedSkills.Count > 0) payload["skills"] = new JArray(allowedSkills);

            // FlueService runs Node off-thread and marshals callbacks to the Editor main thread, where mutating the
            // workflow state + firing UI events is valid.
            m_Handle = FlueService.Run(k_Workflow, payload, flueDir, new FlueCallbacks
            {
                OnProgress = OnFlueProgress,
                // Persist the turn ONLY if the answer was actually emitted — if the user cancelled (m_Finished) the
                // result raced in too late and must not pollute the transcript with a turn that was never shown.
                OnResult = card => { if (EmitFinalAnswer(Render(card))) VexChatHistory.Append(ConversationId, prompt, card.Explanation); },
                OnError = msg => EmitFinalAnswer("⚠️ flue error: " + msg),
            });
        }

        static string Combine(string a, string b) =>
            string.IsNullOrEmpty(a) ? (b ?? string.Empty) : string.IsNullOrEmpty(b) ? a : a + "\n\n" + b;

        // ---- live progress → thought blocks --------------------------------------------------------------------

        // Each stderr line is one of: model reasoning text (two-space prefix, run-dist.mjs logEvent text_delta path),
        // or a "[flue] ..." / "[run-dist] ..." / "[codex] ..." status beat. We render both as THOUGHT blocks so the
        // window shows live progress above the (still-empty) answer.
        void OnFlueProgress(string line)
        {
            if (m_Finished || WorkflowState == State.Closed || string.IsNullOrEmpty(line))
                return;

            EnsureAcknowledged();

            // Reasoning text: run-dist prefixes every complete text_delta line with exactly two spaces.
            if (line.Length >= 2 && line[0] == ' ' && line[1] == ' ' && !line.TrimStart().StartsWith("[flue]"))
            {
                AppendReasoning(line.Substring(2));
                return;
            }

            // Status beat. Flush any buffered reasoning first so ordering reads naturally, then show the beat.
            var status = TranslateStatus(line);
            if (status == null)
                return; // opaque/noisy line — skip rather than spam the reasoning surface.

            FlushReasoning();
            EmitThought(status);
        }

        // Map a "[flue] ..." status line to a short human label, or null to skip. Mirrors run-dist.mjs logEvent().
        static string TranslateStatus(string line)
        {
            var t = line.Trim();
            if (t.StartsWith("[flue] thinking:start")) return null; // suppressed at source; ignore any legacy beats
            if (t.StartsWith("[flue] tool:start"))
            {
                // Format: "[flue] tool:start <name>[ :: <detail>]" — render as `name` detail so the user sees WHAT it's
                // doing (e.g. `vex_call` subscene_object_create) instead of an opaque "Running vex_call".
                var rest = t.Substring("[flue] tool:start".Length).Trim();
                var sep = rest.IndexOf(" :: ", StringComparison.Ordinal);
                var name = sep < 0 ? rest : rest.Substring(0, sep);
                var detail = sep < 0 ? string.Empty : rest.Substring(sep + 4).Trim();
                return string.IsNullOrEmpty(detail) ? "▸ `" + name + "`" : "▸ `" + name + "` " + detail;
            }
            if (t.StartsWith("[flue] tool:done")) return null; // suppressed at source (success is silent)
            if (t.StartsWith("[flue] tool:error")) return "⚠️ **tool failed** `" + t.Substring("[flue] tool:error".Length).Trim() + "`";
            if (t.StartsWith("[flue] compaction:start")) return "**Compacting context…**";
            if (t.StartsWith("[flue] ERROR")) return t.Substring("[flue] ".Length);
            if (t.StartsWith("[codex]")) return t.Substring("[codex] ".Length);
            // "[flue] Running workflow", "[flue] Run ID", "[flue] Done.", "[run-dist] ...", server passthrough: skip.
            return null;
        }

        void AppendReasoning(string text)
        {
            if (m_ReasoningBuffer.Length > 0) m_ReasoningBuffer.Append('\n');
            m_ReasoningBuffer.Append(text);
            if (++m_ReasoningLinesPending >= k_ReasoningLinesPerThought)
                FlushReasoning();
        }

        void FlushReasoning()
        {
            if (m_ReasoningBuffer.Length == 0)
                return;
            EmitThought(m_ReasoningBuffer.ToString());
            m_ReasoningBuffer.Clear();
            m_ReasoningLinesPending = 0;
        }

        // ---- fragment synthesis ---------------------------------------------------------------------------------

        // Send the synthetic ChatAcknowledgmentV1 once, lazily, on the first sign of progress. This drives the base
        // from AwaitingChatAcknowledgement → AwaitingChatResponse so subsequent fragments are accepted.
        void EnsureAcknowledged()
        {
            if (m_Acknowledged || WorkflowState == State.Closed)
                return;
            m_Acknowledged = true;

            Receive(new ChatAcknowledgmentV1
            {
                MessageId = Guid.NewGuid().ToString(),
                Markdown = m_Prompt,
                AttachedContextMetadata = new List<ChatAcknowledgmentV1.AttachedContextMetadataV1>(),
            });
        }

        // A non-last fragment carrying a <THOUGHT> block. ChatResponseUtils parses this into a ThoughtBlock rendered
        // above the answer; the empty answer payload keeps the AnswerBlock from being created prematurely.
        void EmitThought(string content)
        {
            if (m_Finished || WorkflowState == State.Closed || string.IsNullOrEmpty(content))
                return;
            EnsureAcknowledged();

            // EscapeHtml turns '<'/'>' into </> so the model's reasoning text can never contain a literal
            // </THOUGHT> (or <TOOL_CALL>) marker inside the JSON payload — otherwise ChatResponseUtils.ParseTags would
            // close on the inner marker, hand a truncated JSON fragment to the deserializer, and drop the whole turn.
            var json = JsonConvert.SerializeObject(new { content }, new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml });
            Receive(new ChatResponseV1
            {
                MessageId = m_ResponseId,
                Markdown = "<THOUGHT>" + json + "</THOUGHT>",
                LastMessage = false,
            });
        }

        // The terminal emit: flush remaining reasoning, drop the real answer in as the AnswerBlock (one non-last
        // fragment), then close the stream with the empty last fragment (returns the base to Idle). Idempotent.
        // Returns true if the answer was emitted, false if suppressed (already finished/closed) — the caller uses this
        // to decide whether to persist the turn.
        bool EmitFinalAnswer(string answer)
        {
            if (m_Finished || WorkflowState == State.Closed)
                return false;
            m_Finished = true;
            m_Handle = null; // the result has been delivered; the flue process has exited

            EnsureAcknowledged();
            FlushReasoning();

            Receive(new ChatResponseV1 { MessageId = m_ResponseId, Markdown = answer ?? string.Empty, LastMessage = false });
            // Empty last fragment closes the AnswerBlock and returns the workflow to Idle.
            Receive(new ChatResponseV1 { MessageId = m_ResponseId, Markdown = string.Empty, LastMessage = true });
            return true;
        }

        void Receive(IModel message) =>
            ProcessReceiveResult(new ReceiveResult { IsDeserializedSuccessfully = true, DeserializedData = message });

        // ---- editor context (attached items + selection) -------------------------------------------------------

        // A human/agent-readable snapshot of the current Editor context: the items the user explicitly attached via
        // the window's "+" button / drag-drop (req.AttachedContext), or — when nothing was attached — the live
        // Hierarchy/Project selection. The chat agent (which authors C# run via unity-cli) uses the scene paths /
        // asset GUIDs to act on them. Runs on the main thread (SendMessageInternal) before the off-thread flue run.
        static string BuildEditorContext(ChatRequestV1 req)
        {
            var sb = new StringBuilder();

            var attached = req?.AttachedContext;
            if (attached != null && attached.Count > 0)
            {
                sb.AppendLine("Attached context (the user attached these to this message):");
                foreach (var item in attached)
                {
                    var line = DescribeAttached(item);
                    if (!string.IsNullOrEmpty(line))
                        sb.Append("- ").AppendLine(line);
                }
            }
            else
            {
                var sel = DescribeSelection();
                if (!string.IsNullOrEmpty(sel))
                {
                    sb.AppendLine("Current Editor selection (Hierarchy/Project):");
                    sb.Append(sel);
                }
            }

            return sb.ToString().TrimEnd();
        }

        // EntryType ints mirror Unity.AI.Assistant.Data.AssistantContextType (Unknown=0, HierarchyObject=1,
        // SceneObject=2, Component=3, ConsoleMessage=4, SubAsset=5, Virtual=6). The Value field meaning depends on it.
        static string DescribeAttached(ChatRequestV1.AttachedContextModel item)
        {
            var m = item?.Metadata;
            if (m == null)
                return null;

            var name = string.IsNullOrEmpty(m.DisplayValue) ? "(unnamed)" : m.DisplayValue;
            var type = m.ValueType ?? "?";

            switch (m.EntryType)
            {
                case 2: // SceneObject — Value is "/scene/path\n<instanceID>"
                    return $"GameObject \"{name}\" — scene path: {CleanScenePath(m.Value)} (type {type})";
                case 3: // Component — Value is the host GameObject's "/scene/path\n<instanceID>"
                    return $"Component {type} on \"{name}\" — scene path: {CleanScenePath(m.Value)} (component index {m.ValueIndex})";
                case 1: // HierarchyObject — Value is the asset GUID
                    return $"Project asset \"{name}\" — GUID: {m.Value} (type {type})";
                case 5: // SubAsset — Value is "guid_localFileId"
                    return $"Sub-asset \"{name}\" — {m.Value} (type {type})";
                case 4: // ConsoleMessage — Value is the full message
                    return $"Console {type}: {Truncate(m.Value, 400)}";
                case 6: // Virtual — image / screenshot / folder
                    return $"Attachment \"{name}\" (type {type})";
                default:
                    return $"\"{name}\" — {m.Value} (type {type})";
            }
        }

        // Fallback: the live Editor selection, even when nothing was explicitly attached.
        static string DescribeSelection()
        {
            var sb = new StringBuilder();
            foreach (var obj in UnityEditor.Selection.objects)
            {
                if (obj == null)
                    continue;

                if (obj is UnityEngine.GameObject go && string.IsNullOrEmpty(UnityEditor.AssetDatabase.GetAssetPath(go)))
                {
                    // Scene GameObject: give the full hierarchy path the agent can GameObject.Find().
                    sb.Append("- GameObject \"").Append(go.name).Append("\" — scene path: ")
                        .Append(GetScenePath(go)).AppendLine();
                }
                else
                {
                    var path = UnityEditor.AssetDatabase.GetAssetPath(obj);
                    if (!string.IsNullOrEmpty(path))
                    {
                        var guid = UnityEditor.AssetDatabase.AssetPathToGUID(path);
                        sb.Append("- Project asset \"").Append(obj.name).Append("\" — path: ")
                            .Append(path).Append(" (GUID ").Append(guid).Append(", type ")
                            .Append(obj.GetType().FullName).AppendLine(")");
                    }
                    else
                    {
                        sb.Append("- \"").Append(obj.name).Append("\" (type ")
                            .Append(obj.GetType().FullName).AppendLine(")");
                    }
                }
            }
            return sb.ToString();
        }

        // The attached Value for scene objects/components is "/scene/path\n<instanceID>" — keep only the path part.
        static string CleanScenePath(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "(unknown)";
            var nl = value.IndexOf('\n');
            return nl >= 0 ? value.Substring(0, nl) : value;
        }

        static string GetScenePath(UnityEngine.GameObject go)
        {
            var path = "/" + go.name;
            var t = go.transform.parent;
            while (t != null)
            {
                path = "/" + t.name + path;
                t = t.parent;
            }
            return path;
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + " …";
        }

        // ---- rendering -----------------------------------------------------------------------------------------

        static string Render(MemoryCard c)
        {
            var sb = new StringBuilder();
            if (!c.Ok) sb.AppendLine("⚠️ _ok=false — work may be incomplete._").AppendLine();
            if (!string.IsNullOrEmpty(c.Explanation)) sb.AppendLine(c.Explanation).AppendLine();
            if (!string.IsNullOrEmpty(c.Result)) sb.AppendLine("```").AppendLine(c.Result).AppendLine("```");
            AppendUndoJournal(sb, c.Undo);
            // forge-routed turns: show which skills the discovery router curated for the worker (c.Skills is parsed
            // from the envelope's `skills`), plus the validation-loop verdict + per-round trail (RawJson-only fields).
            if (c.Skills != null && c.Skills.Count > 0)
                sb.AppendLine().AppendLine("**Skills used:** " + string.Join(", ", c.Skills));
            AppendForgeVerdict(sb, c.RawJson);
            if (!string.IsNullOrEmpty(c.Gaps)) sb.AppendLine().AppendLine("**Gaps:** " + c.Gaps);
            if (!string.IsNullOrEmpty(c.Model)) sb.AppendLine().AppendLine("_model: " + c.Model + "_");
            var text = sb.ToString();
            return string.IsNullOrEmpty(text) ? "(flue returned an empty card)" : text;
        }

        static void AppendUndoJournal(StringBuilder sb, IReadOnlyList<string> undo)
        {
            if (undo == null || undo.Count == 0)
                return;
            sb.AppendLine().AppendLine("**Undo journal:**");
            foreach (var u in undo) sb.AppendLine("- " + u);
        }

        // The forge envelope (superset card) adds judge{verdict,score,blocking_issues,model,modelsDiverge} and
        // rounds[]. Parse them out of RawJson and render a compact verdict line + blocking issues + round trail.
        static void AppendForgeVerdict(StringBuilder sb, string rawJson)
        {
            if (string.IsNullOrEmpty(rawJson))
                return;
            JObject root;
            try { root = JObject.Parse(rawJson); }
            catch { return; }

            if (root["judge"] is not JObject judge)
                return;

            AppendVerdictLine(sb, judge, root["rounds"] as JArray);
            AppendBlockingIssues(sb, judge["blocking_issues"] as JArray);
            if (judge.Value<bool?>("modelsDiverge") == false)
                sb.AppendLine("_note: coder and judge ran the same model — less verification diversity._");
        }

        static void AppendVerdictLine(StringBuilder sb, JObject judge, JArray rounds)
        {
            var verdict = (string)judge["verdict"] ?? "?";
            var score = judge["score"]?.ToString() ?? "?";
            var icon = string.Equals(verdict, "pass", StringComparison.OrdinalIgnoreCase) ? "✅" : "⚠️";

            sb.AppendLine().Append("**Validation loop:** ").Append(icon).Append(' ')
                .Append(verdict).Append(" (score ").Append(score).Append(')');
            if (rounds != null && rounds.Count > 0)
                sb.Append(" · ").Append(rounds.Count).Append(rounds.Count == 1 ? " round" : " rounds");
            sb.AppendLine();
        }

        static void AppendBlockingIssues(StringBuilder sb, JArray issues)
        {
            if (issues == null || issues.Count == 0)
                return;
            sb.AppendLine("**Blocking issues:**");
            foreach (var i in issues) sb.Append("- ").AppendLine(i?.ToString());
        }

        protected override void DisposeTransport()
        {
            OnWorkflowStateChanged -= OnStateChanged; // don't leave the handler reachable on a dead instance
            try { m_Handle?.Cancel(); } catch { }
            m_Handle = null;
        }

        protected override void SubscribeToTransportEvents() { }
        protected override void UnsubscribeFromTransportEvents() { }
    }
}
