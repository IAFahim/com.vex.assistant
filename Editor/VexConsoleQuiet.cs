using System;
using UnityEditor;
using UnityEngine;
using Unity.Scripting.LifecycleManagement;
using Object = UnityEngine.Object;

namespace Vex.Assistant.Editor
{
    internal static partial class VexConsoleQuiet
    {
        private const string k_Needle = "no additional error information was provided";
        private static ILogHandler s_Original;

        static VexConsoleQuiet()
        {
            s_Original = Debug.unityLogger.logHandler;
            if (s_Original is Filter)
                return;
            Debug.unityLogger.logHandler = new Filter(s_Original);
        }

        [OnCodeUnloading]
        private static void OnCodeUnloading()
        {
            if (s_Original != null)
                Debug.unityLogger.logHandler = s_Original;
        }

        private sealed class Filter : ILogHandler
        {
            private readonly ILogHandler m_Inner;

            public Filter(ILogHandler inner)
            {
                m_Inner = inner;
            }

            public void LogFormat(LogType logType, Object context, string format, params object[] args)
            {
                if (logType == LogType.Log && Matches(format, args))
                    return;
                m_Inner.LogFormat(logType, context, format, args);
            }

            public void LogException(Exception exception, Object context)
            {
                m_Inner.LogException(exception, context);
            }

            private static bool Matches(string format, object[] args)
            {
                if (format != null && format.IndexOf(k_Needle, StringComparison.Ordinal) >= 0)
                    return true;

                if (args != null)
                    foreach (var a in args)
                        if (a is string s && s.IndexOf(k_Needle, StringComparison.Ordinal) >= 0)
                            return true;
                return false;
            }
        }
    }
}