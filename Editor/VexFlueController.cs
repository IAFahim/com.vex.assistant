using Unity.AI.Assistant.UI.Editor.Scripts;
using Unity.AI.Toolkit.Accounts.Services;
using Unity.AI.Toolkit.Accounts.Services.Data;
using UnityEditor;

namespace Vex.Assistant.Editor
{
    [InitializeOnLoad]
    internal static class VexFlueController
    {
        private const string k_ActiveKey = "vex.flue.gate.active";
        private const string k_ModelKey = "vex.flue.gate.model";
        private const long k_Points = 1_000_000_000;

        private static double s_NextTick;
        private static double s_LastInject;

        static VexFlueController()
        {
            EditorApplication.update += Tick;
        }

        private static bool Active
        {
            get => SessionState.GetBool(k_ActiveKey, false);
            set => SessionState.SetBool(k_ActiveKey, value);
        }

        private static string Model
        {
            get
            {
                var m = SessionState.GetString(k_ModelKey, "");
                return string.IsNullOrEmpty(m) ? null : m;
            }
            set => SessionState.SetString(k_ModelKey, value ?? "");
        }

        public static void Enable(string model)
        {
            Active = true;
            Model = model;
            var window = AssistantWindow.ShowWindow();
            window.InternalConfigureBackend(new VexFlueBackend(model));
            s_LastInject = EditorApplication.timeSinceStartup;
            s_NextTick = 0;
        }

        private static void Tick()
        {
            if (!Active)
                return;
            if (EditorApplication.timeSinceStartup < s_NextTick)
                return;
            s_NextTick = EditorApplication.timeSinceStartup + 0.5;

            var window = AssistantWindow.FindExistingWindow();
            if (window == null)
                return;

            if (!BackendIsOurs(window) && EditorApplication.timeSinceStartup - s_LastInject > 3.0)
            {
                s_LastInject = EditorApplication.timeSinceStartup;
                window.InternalConfigureBackend(new VexFlueBackend(Model));
                return;
            }

            var points = Account.pointsBalance;
            if (points != null && (points.Value == null || points.Value.PointsAvailable < k_Points))
                points.Value = new PointsBalanceRecord(null) { PointsAvailable = k_Points, PointsAllocated = k_Points };

            var settings = Account.settings;
            var sv = settings?.Value;
            if (sv != null && !sv.CanSpendPoints)
                settings.Value = sv with { CanSpendPoints = true };

            if (Account.cloudConnected.Value != ProjectStatus.Connected)
                Account.cloudConnected.Value = ProjectStatus.Connected;
        }

        private static bool BackendIsOurs(AssistantWindow window)
        {
            return (window.AssistantInstance as Unity.AI.Assistant.Editor.Assistant)?.Backend is VexFlueBackend;
        }
    }
}