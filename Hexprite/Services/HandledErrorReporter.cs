using System;
using System.Runtime.CompilerServices;
using Serilog;

namespace Hexprite.Services
{
    /// <summary>
    /// Structured logging for caught exceptions. Events at Error and above are forwarded to Sentry
    /// when the Serilog Sentry sink is configured (see <see cref="LoggingService"/>).
    /// </summary>
    public static class HandledErrorReporter
    {
        public static void Error(
            Exception ex,
            string operation,
            object? context = null,
            [CallerMemberName] string? callerMemberName = null)
        {
            if (context is null)
            {
                Log.Error(ex, "Handled error: {HandledOperation} Caller={CallerMember}", operation, callerMemberName);
            }
            else
            {
                Log.Error(ex, "Handled error: {HandledOperation} Caller={CallerMember} {@HandledContext}", operation, callerMemberName, context);
            }
        }

        public static void Warning(
            Exception ex,
            string operation,
            object? context = null,
            [CallerMemberName] string? callerMemberName = null)
        {
            if (context is null)
            {
                Log.Warning(ex, "Handled warning: {HandledOperation} Caller={CallerMember}", operation, callerMemberName);
            }
            else
            {
                Log.Warning(ex, "Handled warning: {HandledOperation} Caller={CallerMember} {@HandledContext}", operation, callerMemberName, context);
            }
        }
    }
}
