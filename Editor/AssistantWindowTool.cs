using Newtonsoft.Json.Linq;
using UnityCliConnector;
using UnityEditor;

namespace Vex.Assistant.Editor
{
    [UnityCliTool(
        Name = "assistant_window",
        Group = "vex",
        Description =
            "Open the Unity AI Assistant window running on the vex flue backend (your own model via flue-pipeline, no Unity subscription). Optional 'model' (e.g. minimax/MiniMax-M2.7).")]
    public static class AssistantWindowTool
    {
        [MenuItem("Vex/Assistant on flue (your own model)")]
        public static void OpenOnFlueMenu()
        {
            OpenOnFlue(null);
        }

        private static void OpenOnFlue(string model)
        {
            VexFlueController.Enable(string.IsNullOrEmpty(model) ? VexKeyVault.LoadPolicy().defaultChatModel : model);
        }

        public static object HandleCommand(JObject @params)
        {
            var model = (string)@params["model"];
            OpenOnFlue(string.IsNullOrEmpty(model) ? null : model);
            return new SuccessResponse(
                $"Opened the Assistant window on the flue backend (model: {(string.IsNullOrEmpty(model) ? "default" : model)}).",
                new { backend = "flue", model });
        }

        public class Parameters
        {
            [ToolParameter("Optional flue provider/model, e.g. minimax/MiniMax-M2.7. Omit for the default.")]
            public string Model { get; set; }
        }
    }
}