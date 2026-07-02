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
using UnityEditor;
using UnityEngine;
using Vex.Codex.Editor;

namespace Vex.Assistant.Editor
{
    internal sealed class VexFlueChatWorkflow : BaseChatWorkflow
    {
        private const string k_Workflow = "chat";

        private const int k_ReasoningLinesPerThought = 6;

        private readonly string m_Model;
        private readonly StringBuilder m_ReasoningBuffer = new();
        private bool m_Acknowledged;
        private bool m_Finished;
        private FlueHandle m_Handle;
        private string m_Prompt;
        private int m_ReasoningLinesPending;

        private string m_ResponseId;

        public VexFlueChatWorkflow(string conversationId = null, IFunctionCaller functionCaller = null,
            string model = null)
            : base(conversationId, functionCaller)
        {
            m_Model = model;

            OnWorkflowStateChanged += OnStateChanged;
        }

        private void OnStateChanged(State s)
        {
            if (s != State.Canceling)
                return;

            m_Finished = true;
            try
            {
                m_Handle?.Cancel();
            }
            catch
            {
            }

            m_Handle = null;

            EditorApplication.delayCall += () =>
            {
                if (WorkflowState == State.Canceling)
                    WorkflowState = State.Idle;
            };
        }

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
                    ChatTimeoutSeconds = 1200
                });
                MarkStarted();
            }
            catch (Exception ex)
            {
                MarkStartFailed(ex);
            }
        }

        protected override Task StartConnectionInternal(ICredentialsContext credentialsContext, bool skipInitialization)
        {
            throw new NotImplementedException("VexFlueChatWorkflow uses Start()");
        }

        protected override Task SendMessageInternal(object message, CancellationToken cancellationToken)
        {
            if (message is ChatRequestV1 req)
                StartFlue(req.Markdown ?? string.Empty, BuildEditorContext(req));
            return Task.CompletedTask;
        }

        private void StartFlue(string prompt, string editorContext)
        {
            m_Prompt = prompt;
            m_ResponseId = Guid.NewGuid().ToString();
            m_Acknowledged = false;
            m_Finished = false;
            m_ReasoningBuffer.Clear();
            m_ReasoningLinesPending = 0;

            var flueDir = CodexSettings.Load().FlueDir;
            if (string.IsNullOrEmpty(flueDir))
            {
                EmitFinalAnswer(
                    "⚠️ flue directory is not set (~/.unity-codex/settings.json `flue_dir`). Set it in the Codex Designer window's Settings.");
                return;
            }

            var context = Combine(editorContext, VexChatHistory.Context(ConversationId));

            var payload = new JObject
            {
                ["request"] = prompt,
                ["context"] = context
            };
            if (!string.IsNullOrEmpty(ConversationId)) payload["session"] = ConversationId;
            if (!string.IsNullOrEmpty(m_Model)) payload["model"] = m_Model;

            var allowedSkills = VexEnabledSkills.AllowedNames();
            if (allowedSkills.Count > 0) payload["skills"] = new JArray(allowedSkills);

            m_Handle = FlueService.Run(k_Workflow, payload, flueDir, new FlueCallbacks
            {
                OnProgress = OnFlueProgress,

                OnResult = card =>
                {
                    if (EmitFinalAnswer(Render(card))) VexChatHistory.Append(ConversationId, prompt, card.Explanation);
                },
                OnError = msg => EmitFinalAnswer("⚠️ flue error: " + msg)
            });
        }

        private static string Combine(string a, string b)
        {
            return string.IsNullOrEmpty(a) ? (b ?? string.Empty) : string.IsNullOrEmpty(b) ? a : a + "\n\n" + b;
        }

        private void OnFlueProgress(string line)
        {
            if (m_Finished || WorkflowState == State.Closed || string.IsNullOrEmpty(line))
                return;

            EnsureAcknowledged();

            if (line.Length >= 2 && line[0] == ' ' && line[1] == ' ' && !line.TrimStart().StartsWith("[flue]"))
            {
                AppendReasoning(line.Substring(2));
                return;
            }

            var status = TranslateStatus(line);
            if (status == null)
                return;

            FlushReasoning();
            EmitThought(status);
        }

        private static string TranslateStatus(string line)
        {
            var t = line.Trim();
            if (t.StartsWith("[flue] thinking:start")) return null;
            if (t.StartsWith("[flue] tool:start"))
            {
                var rest = t.Substring("[flue] tool:start".Length).Trim();
                var sep = rest.IndexOf(" :: ", StringComparison.Ordinal);
                var name = sep < 0 ? rest : rest.Substring(0, sep);
                var detail = sep < 0 ? string.Empty : rest.Substring(sep + 4).Trim();
                return string.IsNullOrEmpty(detail) ? "▸ `" + name + "`" : "▸ `" + name + "` " + detail;
            }

            if (t.StartsWith("[flue] tool:done")) return null;
            if (t.StartsWith("[flue] tool:error"))
                return "⚠️ **tool failed** `" + t.Substring("[flue] tool:error".Length).Trim() + "`";
            if (t.StartsWith("[flue] compaction:start")) return "**Compacting context…**";
            if (t.StartsWith("[flue] ERROR")) return t.Substring("[flue] ".Length);
            if (t.StartsWith("[codex] ")) return t.Substring("[codex] ".Length);

            return null;
        }

        private void AppendReasoning(string text)
        {
            if (m_ReasoningBuffer.Length > 0) m_ReasoningBuffer.Append('\n');
            m_ReasoningBuffer.Append(text);
            if (++m_ReasoningLinesPending >= k_ReasoningLinesPerThought)
                FlushReasoning();
        }

        private void FlushReasoning()
        {
            if (m_ReasoningBuffer.Length == 0)
                return;
            EmitThought(m_ReasoningBuffer.ToString());
            m_ReasoningBuffer.Clear();
            m_ReasoningLinesPending = 0;
        }

        private void EnsureAcknowledged()
        {
            if (m_Acknowledged || WorkflowState == State.Closed)
                return;
            m_Acknowledged = true;

            Receive(new ChatAcknowledgmentV1
            {
                MessageId = Guid.NewGuid().ToString(),
                Markdown = m_Prompt,
                AttachedContextMetadata = new List<ChatAcknowledgmentV1.AttachedContextMetadataV1>()
            });
        }

        private void EmitThought(string content)
        {
            if (m_Finished || WorkflowState == State.Closed || string.IsNullOrEmpty(content))
                return;
            EnsureAcknowledged();

            var json = JsonConvert.SerializeObject(new { content },
                new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml });
            Receive(new ChatResponseV1
            {
                MessageId = m_ResponseId,
                Markdown = "<THOUGHT>" + json + "</THOUGHT>",
                LastMessage = false
            });
        }

        private bool EmitFinalAnswer(string answer)
        {
            if (m_Finished || WorkflowState == State.Closed)
                return false;
            EnsureAcknowledged();
            FlushReasoning();
            m_Finished = true;
            m_Handle = null;

            Receive(new ChatResponseV1
                { MessageId = m_ResponseId, Markdown = answer ?? string.Empty, LastMessage = false });

            Receive(new ChatResponseV1 { MessageId = m_ResponseId, Markdown = string.Empty, LastMessage = true });
            return true;
        }

        private void Receive(IModel message)
        {
            ProcessReceiveResult(new ReceiveResult { IsDeserializedSuccessfully = true, DeserializedData = message });
        }

        private static string BuildEditorContext(ChatRequestV1 req)
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

        private static string DescribeAttached(ChatRequestV1.AttachedContextModel item)
        {
            var m = item?.Metadata;
            if (m == null)
                return null;

            var name = string.IsNullOrEmpty(m.DisplayValue) ? "(unnamed)" : m.DisplayValue;
            var type = m.ValueType ?? "?";

            switch (m.EntryType)
            {
                case 2:
                    return $"GameObject \"{name}\" — scene path: {CleanScenePath(m.Value)} (type {type})";
                case 3:
                    return
                        $"Component {type} on \"{name}\" — scene path: {CleanScenePath(m.Value)} (component index {m.ValueIndex})";
                case 1:
                    return $"Project asset \"{name}\" — GUID: {m.Value} (type {type})";
                case 5:
                    return $"Sub-asset \"{name}\" — {m.Value} (type {type})";
                case 4:
                    return $"Console {type}: {Truncate(m.Value, 400)}";
                case 6:
                    return $"Attachment \"{name}\" (type {type})";
                default:
                    return $"\"{name}\" — {m.Value} (type {type})";
            }
        }

        private static string DescribeSelection()
        {
            var sb = new StringBuilder();
            foreach (var obj in Selection.objects)
            {
                if (obj == null)
                    continue;

                if (obj is GameObject go && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(go)))
                {
                    sb.Append("- GameObject \"").Append(go.name).Append("\" — scene path: ")
                        .Append(GetScenePath(go)).AppendLine();
                }
                else
                {
                    var path = AssetDatabase.GetAssetPath(obj);
                    if (!string.IsNullOrEmpty(path))
                    {
                        var guid = AssetDatabase.AssetPathToGUID(path);
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

        private static string CleanScenePath(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "(unknown)";
            var nl = value.IndexOf('\n');
            return nl >= 0 ? value.Substring(0, nl) : value;
        }

        private static string GetScenePath(GameObject go)
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

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + " …";
        }

        private static string Render(MemoryCard c)
        {
            var sb = new StringBuilder();
            if (!c.Ok) sb.AppendLine("⚠️ _ok=false — work may be incomplete._").AppendLine();
            if (!string.IsNullOrEmpty(c.Explanation)) sb.AppendLine(c.Explanation).AppendLine();
            if (!string.IsNullOrEmpty(c.Result)) sb.AppendLine("```").AppendLine(c.Result).AppendLine("```");
            AppendUndoJournal(sb, c.Undo);

            if (c.Skills != null && c.Skills.Count > 0)
                sb.AppendLine().AppendLine("**Skills used:** " + string.Join(", ", c.Skills));
            AppendForgeVerdict(sb, c.RawJson);
            if (!string.IsNullOrEmpty(c.Gaps)) sb.AppendLine().AppendLine("**Gaps:** " + c.Gaps);
            if (!string.IsNullOrEmpty(c.Model)) sb.AppendLine().AppendLine("_model: " + c.Model + "_");
            var text = sb.ToString();
            return string.IsNullOrEmpty(text) ? "(flue returned an empty card)" : text;
        }

        private static void AppendUndoJournal(StringBuilder sb, IReadOnlyList<string> undo)
        {
            if (undo == null || undo.Count == 0)
                return;
            sb.AppendLine().AppendLine("**Undo journal:**");
            foreach (var u in undo) sb.AppendLine("- " + u);
        }

        private static void AppendForgeVerdict(StringBuilder sb, string rawJson)
        {
            if (string.IsNullOrEmpty(rawJson))
                return;
            JObject root;
            try
            {
                root = JObject.Parse(rawJson);
            }
            catch
            {
                return;
            }

            if (root["judge"] is not JObject judge)
                return;

            AppendVerdictLine(sb, judge, root["rounds"] as JArray);
            AppendBlockingIssues(sb, judge["blocking_issues"] as JArray);
            if (judge.Value<bool?>("modelsDiverge") == false)
                sb.AppendLine("_note: coder and judge ran the same model — less verification diversity._");
        }

        private static void AppendVerdictLine(StringBuilder sb, JObject judge, JArray rounds)
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

        private static void AppendBlockingIssues(StringBuilder sb, JArray issues)
        {
            if (issues == null || issues.Count == 0)
                return;
            sb.AppendLine("**Blocking issues:**");
            foreach (var i in issues) sb.Append("- ").AppendLine(i?.ToString());
        }

        protected override void DisposeTransport()
        {
            OnWorkflowStateChanged -= OnStateChanged;
            try
            {
                m_Handle?.Cancel();
            }
            catch
            {
            }

            m_Handle = null;
        }

        protected override void SubscribeToTransportEvents()
        {
        }

        protected override void UnsubscribeFromTransportEvents()
        {
        }
    }
}
