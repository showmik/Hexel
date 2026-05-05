using System; using System.IO; namespace Hexprite { public static class Logger { public static void Log(string msg) { File.AppendAllText(@"debug.log", msg + "\n"); } } }
