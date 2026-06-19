using System.Reflection;
using Unity.AI.Assistant.UI.Editor.Scripts;
using Unity.AI.Toolkit.Accounts.Services;
using Unity.AI.Toolkit.Accounts.Services.Data;
using UnityEditor;

namespace Vex.Assistant.Editor
{
    /// <summary>
    /// Runs the Unity Assistant window on the vex flue backend AND keeps its chat input un-gated.
    ///
    /// The UI enables the input only when <c>!ProviderStateObserver.IsUnityProvider || points.CanAfford(...)</c>.
    /// Our injected flue backend reads as the "Unity" provider, so without a subscription the points gate disables
    /// the input ("Start the 14-day trial"). We open the gate on the POINTS side — set a local points balance — which
    /// keeps the provider as Unity (so the ACP/relay status layer is never engaged: no "executable not found"
    /// banner) while the actual prompt submission still routes through the injected backend (flue). No real points
    /// are spent: our backend never calls Unity's billing. The relay periodically refreshes the balance from the
    /// server, so a light watcher re-asserts it; it also re-injects the backend after domain reloads (the override
    /// is one-shot). Self-contained in this package; the fork's only footprint stays the additive friend files.
    /// </summary>
    [InitializeOnLoad]
    internal static class VexFlueController
    {
        const string k_ActiveKey = "vex.flue.gate.active";
        const string k_ModelKey = "vex.flue.gate.model";
        const long k_Points = 1_000_000_000;

        static double s_NextTick;
        static double s_LastInject;

        static VexFlueController()
        {
            EditorApplication.update += Tick;
        }

        static bool Active
        {
            get => SessionState.GetBool(k_ActiveKey, false);
            set => SessionState.SetBool(k_ActiveKey, value);
        }

        static string Model
        {
            get { var m = SessionState.GetString(k_ModelKey, ""); return string.IsNullOrEmpty(m) ? null : m; }
            set => SessionState.SetString(k_ModelKey, value ?? "");
        }

        /// <summary>Open the Assistant window on the flue backend and keep it un-gated.</summary>
        public static void Enable(string model)
        {
            Active = true;
            Model = model;
            var window = AssistantWindow.ShowWindow();
            window.InternalConfigureBackend(new VexFlueBackend(model));
            s_LastInject = EditorApplication.timeSinceStartup;
            s_NextTick = 0;
        }

        static void Tick()
        {
            if (!Active)
                return;
            if (EditorApplication.timeSinceStartup < s_NextTick)
                return;
            s_NextTick = EditorApplication.timeSinceStartup + 0.5;

            var window = AssistantWindow.FindExistingWindow();
            if (window == null)
                return;

            // A domain reload reverts the one-shot backend override to Unity's cloud. Re-inject flue (throttled so
            // the close+reopen can't loop) so the open gate never points at a dead Unity backend.
            if (!BackendIsOurs(window) && EditorApplication.timeSinceStartup - s_LastInject > 3.0)
            {
                s_LastInject = EditorApplication.timeSinceStartup;
                window.InternalConfigureBackend(new VexFlueBackend(Model));
                return;
            }

            // Keep a local balance that covers the chat pre-authorization so CanEnableChat() stays true. Setting
            // Value fires the balance OnChange, which makes the view re-run its input-enable check. No-op once set.
            var points = Account.pointsBalance;
            if (points != null && (points.Value == null || points.Value.PointsAvailable < k_Points))
            {
                points.Value = new PointsBalanceRecord(null) { PointsAvailable = k_Points, PointsAllocated = k_Points };
            }

            // Suppress the "Build faster with Unity AI — Start the 14-day trial" upsell. SessionStatusBanner shows it
            // whenever !settings.CanSpendPoints (no Unity subscription). We don't spend Unity points (flue is our
            // backend), so flip that flag on locally. Flip it ON THE EXISTING record via `with` so every other field
            // (IsAiAssistantEnabled, legal, …) keeps the server's value — replacing the whole record with a blank one
            // would instead trip the "AI is disabled" banner. The server's periodic settings refresh resets it, hence
            // the re-assert here. Guarded so it only writes (and fires OnChange) when actually false.
            var settings = Account.settings;
            var sv = settings?.Value;
            if (sv != null && !sv.CanSpendPoints)
                settings.Value = sv with { CanSpendPoints = true };

            // flue is our LOCAL backend and never talks to Unity cloud, so the project does not need to be
            // cloud-bound. SessionStatusBanner shows the "missing a cloud connection / Select a cloud project"
            // gate (and keeps chat disabled via ApiAccessibleState) whenever cloudConnected != Connected — here
            // CloudProjectSettings.projectBound is false even though a project id is set. Re-assert Connected
            // locally so the gate clears; the periodic UnityConnect refresh resets it, hence the re-assert each
            // Tick (same pattern as the points/settings re-asserts above). Guarded to only write when wrong.
            if (Account.cloudConnected.Value != ProjectStatus.Connected)
                Account.cloudConnected.Value = ProjectStatus.Connected;
        }

        // True if the window's Assistant is running our injected flue backend. Uses the typed friend-accessible path
        // (AssistantWindow.AssistantInstance is internal; the concrete Assistant.Backend is public) instead of string
        // reflection — compile-checked, so an upstream rename fails the build rather than silently mis-reporting and
        // stranding the open points-gate on the real Unity cloud backend. Null instance (window mid-construction) →
        // false → re-inject, which is correct and bounded by the 3s throttle.
        static bool BackendIsOurs(AssistantWindow window) =>
            (window.AssistantInstance as Unity.AI.Assistant.Editor.Assistant)?.Backend is VexFlueBackend;
    }
}
