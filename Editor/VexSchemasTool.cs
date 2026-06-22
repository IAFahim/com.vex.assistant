using System.Linq;
using Newtonsoft.Json.Linq;
using UnityCliConnector;

namespace Vex.Assistant.Editor
{
    [UnityCliTool(
        Name = "vex_schemas",
        Group = "vex",
        Description =
            "List the deterministic vex-group unity-cli tools (name, group, parameters) so an agent can call them via the tool dispatch instead of authoring C#.")]
    public static class VexSchemasTool
    {
        public static object HandleCommand(JObject p)
        {
            var all = JArray.FromObject(ToolDiscovery.GetToolSchemas());
            var tools = new JArray(all.Where(t => (string)t["group"] == "vex"));
            return new SuccessResponse($"{tools.Count} vex tool(s).", new { tools });
        }
    }
}