using System.Linq;
using Newtonsoft.Json.Linq;
using UnityCliConnector;

namespace Vex.Assistant.Editor
{
    [UnityCliTool(
        Name = "agent_list",
        Group = "vex",
        Description = "List the vex specialized agents (id, name, description) runnable via assistant_run.")]
    public static class AgentListTool
    {
        public static object HandleCommand(JObject @params)
        {
            var agents = VexAgents.All
                .Select(a => new { id = a.Id, name = a.Name, description = a.Description })
                .ToArray();
            return new SuccessResponse($"{agents.Length} vex agent(s).", new { agents });
        }
    }
}