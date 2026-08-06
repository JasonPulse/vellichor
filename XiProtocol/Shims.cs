// Trimmed global usings for the shared protocol lib — only the protocol namespaces (NOT the
// bot's Capabilities/Brains/Interfaces/etc., which don't exist here).
global using XiHeadless;
global using XiHeadless.Net;
global using XiHeadless.Game;
global using XiHeadless.Interfaces;

namespace XiHeadless;

/// <summary>
/// Minimal logger stub so the linked Net/Game code compiles without the bot's Diagnostics.cs.
/// Net/Game only call Log.Info / Log.Always (string). Silent by default; set Verbose to echo.
/// </summary>
public static class Log
{
    public static bool Verbose = false;
    public static void Info(string msg) { if (Verbose) System.Console.WriteLine(msg); }
    public static void Always(string msg) => System.Console.WriteLine(msg);
    public static void Warn(string msg) => System.Console.WriteLine("WARN: " + msg);
    public static void Error(string msg) => System.Console.WriteLine("ERROR: " + msg);
}
