using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Vex.Assistant.Editor
{
    [InitializeOnLoad]
    internal static class VexConsoleQuiet
    {
        private const string k_Needle = "no additional error information was provided";

        static VexConsoleQuiet()
        {
            if (Debug.unityLogger.logHandler is Filter)
                return;
            Debug.unityLogger.logHandler = new Filter(Debug.unityLogger.logHandler);
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