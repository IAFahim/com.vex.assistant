using System;
using UnityEngine;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Vex.Assistant.Editor
{
    /// <summary>
    /// Silences ONE specific upstream console line: the model-selector's
    /// <c>"Error reason is 'NoSubscription' … no additional error information was provided"</c>
    /// <see cref="Debug.Log"/> (Modules/Unity.AI.ModelSelector/.../ModelSelectorSuperProxyActions.cs). It fires on
    /// every Assistant window open because the org has no Unity AI subscription — but we run the window on our OWN
    /// model via flue and never use Unity's model catalog, so the message is pure noise.
    ///
    /// We can't change that <c>Debug.Log</c> without editing the fork, so we wrap <see cref="Debug.unityLogger"/>'s
    /// log handler with a thin filter that DROPS that one message and forwards everything else verbatim to the
    /// original handler. Deliberately surgical: it matches a distinctive substring that appears in no other message,
    /// only considers <see cref="LogType.Log"/> (never Warning/Error/Exception), and is fully reversible. Guarded so
    /// repeated domain reloads can't nest the wrapper.
    /// </summary>
    [InitializeOnLoad]
    internal static class VexConsoleQuiet
    {
        // The invariant tail of ModelSelectorSuperProxyActions line 487 — unique to that single Debug.Log.
        const string k_Needle = "no additional error information was provided";

        static VexConsoleQuiet()
        {
            if (Debug.unityLogger.logHandler is Filter)
                return; // already installed (survives domain reload on the UnityEngine logger)
            Debug.unityLogger.logHandler = new Filter(Debug.unityLogger.logHandler);
        }

        sealed class Filter : ILogHandler
        {
            readonly ILogHandler m_Inner;
            public Filter(ILogHandler inner) => m_Inner = inner;

            public void LogFormat(LogType logType, Object context, string format, params object[] args)
            {
                // Only ever drop plain Logs; warnings/errors always pass through untouched.
                if (logType == LogType.Log && Matches(format, args))
                    return;
                m_Inner.LogFormat(logType, context, format, args);
            }

            public void LogException(Exception exception, Object context) => m_Inner.LogException(exception, context);

            static bool Matches(string format, object[] args)
            {
                if (format != null && format.IndexOf(k_Needle, StringComparison.Ordinal) >= 0)
                    return true;
                // Debug.Log(string) arrives as format "{0}" with the message in args[0].
                if (args != null)
                    foreach (var a in args)
                        if (a is string s && s.IndexOf(k_Needle, StringComparison.Ordinal) >= 0)
                            return true;
                return false;
            }
        }
    }
}
