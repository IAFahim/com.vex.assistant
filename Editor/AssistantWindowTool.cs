using Newtonsoft.Json.Linq;
using UnityCliConnector;
using UnityEditor;

namespace Vex.Assistant.Editor
{
    /// <summary>
    /// Opens the Unity AI Assistant window running on the vex flue backend (the user's own model, no Unity cloud /
    /// subscription) via AssistantWindow's designed InternalConfigureBackend override hook. Reachable from the Editor
    /// menu and from unity-cli (assistant_window). Optional 'model' selects the flue provider/model.
    /// </summary>
    [UnityCliTool(
        Name = "assistant_window",
        Group = "vex",
        Description = "Open the Unity AI Assistant window running on the vex flue backend (your own model via flue-pipeline, no Unity subscription). Optional 'model' (e.g. minimax/MiniMax-M2.7).")]
    public static class AssistantWindowTool
    {
        public class Parameters
        {
            [ToolParameter("Optional flue provider/model, e.g. minimax/MiniMax-M2.7. Omit for the default.")]
            public string Model { get; set; }
        }

        [MenuItem("Vex/Assistant on flue (your own model)")]
        public static void OpenOnFlueMenu() => OpenOnFlue(null);

        // A null/empty model uses the Editor-selected default (Vex ▸ Keys & Models window) — an alias the flue router
        // understands ('default' → MiniMax, 'glm' → GLM with fallback, 'glm:glm-5.2', …).
        static void OpenOnFlue(string model)
            => VexFlueController.Enable(string.IsNullOrEmpty(model) ? VexKeyVault.LoadPolicy().defaultChatModel : model);

        public static object HandleCommand(JObject @params)
        {
            var model = (string)@params["model"];
            OpenOnFlue(string.IsNullOrEmpty(model) ? null : model);
            return new SuccessResponse(
                $"Opened the Assistant window on the flue backend (model: {(string.IsNullOrEmpty(model) ? "default" : model)}).",
                new { backend = "flue", model });
        }
    }
}
