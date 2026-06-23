using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Vex.Assistant.Editor
{
    internal static class VexChatHistory
    {
        private const int k_MaxTurns = 8;
        private const int k_MaxTurnChars = 1500;

        private static string Key(string conversationId)
        {
            return "vex.flue.history." + (string.IsNullOrEmpty(conversationId) ? "default" : conversationId);
        }

        public static List<string> Get(string conversationId)
        {
            var raw = SessionState.GetString(Key(conversationId), "");
            if (string.IsNullOrEmpty(raw))
                return new List<string>();
            try
            {
                return JArray.Parse(raw).Select(t => t.ToString()).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        public static string Context(string conversationId)
        {
            return string.Join("\n\n", Get(conversationId));
        }

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
