using System;
using Serilog;

namespace Hexprite.Services
{
    /// <summary>
    /// Structured logging for caught exceptions. Events at Error and above are forwarded to Sentry
    /// when the Serilog Sentry sink is configured (see <see cref="LoggingService"/>).
    /// </summary>
    public static class HandledErrorReporter
    {
        public static void Error(Exception ex, string operation, object? context = null)
        {
            if (context is null)
            {
                Log.Error(ex, "Handled error: {HandledOperation}", operation);
            }
            else
            {
                Log.Error(ex, "Handled error: {HandledOperation} {@HandledContext}", operation, context);
            }
        }

        public static void Warning(Exception ex, string operation, object? context = null)
        {
            if (context is null)
            {
                Log.Warning(ex, "Handled warning: {HandledOperation}", operation);
            }
            else
            {
                Log.Warning(ex, "Handled warning: {HandledOperation} {@HandledContext}", operation, context);
            }
        }
    }
}
