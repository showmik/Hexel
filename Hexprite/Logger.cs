using System;
using System.IO;

namespace Hexprite
{
    public static class Logger
    {
        // 1. Static lock object to synchronize thread access
        private static readonly object _fileLock = new object();

        // 2. Safe absolute path matching ThemeService persistence
        private static readonly string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hexprite");
        private static readonly string LogFile = Path.Combine(LogDir, "debug.log");

        public static void Log(string msg)
        {
            // 3. Serialize access so only one thread writes at a time
            lock (_fileLock)
            {
                try
                {
                    if (!Directory.Exists(LogDir))
                    {
                        Directory.CreateDirectory(LogDir);
                    }

                    File.AppendAllText(LogFile, msg + "\n");
                }
                catch
                {
                    // Swallow logging exceptions to prevent crashing the host application
                }
            }
        }
    }
}