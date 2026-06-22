using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Vex.Assistant.Editor
{
    /// <summary>
    /// Per-conversation chat transcript, persisted in <see cref="SessionState"/> so the window's memory survives
    /// domain reloads (script recompiles) within an Editor session. flue runs each turn as a fresh process and its
    /// named sessions don't persist across runs, so this transcript — passed back as the flue <c>context</c> — IS
    /// the conversation memory. Keyed by the Assistant conversation id.
    /// </summary>
    internal static class VexChatHistory
    {
        const int k_MaxTurns = 8;
        const int k_MaxTurnChars = 1500;

        static string Key(string conversationId) => "vex.flue.history." + (string.IsNullOrEmpty(conversationId) ? "default" : conversationId);

        public static List<string> Get(string conversationId)
        {
            var raw = SessionState.GetString(Key(conversationId), "");
            if (string.IsNullOrEmpty(raw))
                return new List<string>();
            try { return JArray.Parse(raw).Select(t => t.ToString()).ToList(); }
            catch { return new List<string>(); }
        }

        /// <summary>The transcript as the flue context block (oldest → newest), or "" when empty.</summary>
        public static string Context(string conversationId) => string.Join("\n\n", Get(conversationId));

        public static void Append(string conversationId, string user, string assistant)
        {
            var u = (user ?? string.Empty).Trim();
            if (u.Length > k_MaxTurnChars) u = u.Substring(0, k_MaxTurnChars) + " …";

            var a = (assistant ?? string.Empty).Trim();
            if (a.Length > k_MaxTurnChars) a = a.Substring(0, k_MaxTurnChars) + " …";

            var list = Get(conversationId);
            list.Add($"User: {u}\nVex: {a}");
            while (list.Count > k_MaxTurns)
                list.RemoveAt(0);

            SessionState.SetString(Key(conversationId), new JArray(list).ToString(Formatting.None));
        }
    }
}
