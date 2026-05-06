using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Sentry;
using Serilog;

namespace Hexprite.Services
{
    public sealed class BugReportService : IBugReportService
    {
        public BugReportResult SubmitReport(BugReportInput input)
        {
            try
            {
                SentryId eventId = SentrySdk.CaptureMessage(
                    string.IsNullOrWhiteSpace(input.Summary) ? "Manual bug report submitted" : input.Summary.Trim(),
                    scope =>
                    {
                        scope.SetTag("report.type", "manual");
                        scope.SetTag("report.channel", "in-app");
                        scope.SetTag("app.version", GetAppVersion());
                        scope.SetExtra("stepsToReproduce", input.StepsToReproduce.Trim());
                        scope.SetExtra("expectedBehavior", input.ExpectedBehavior.Trim());
                        scope.SetExtra("actualBehavior", input.ActualBehavior.Trim());
                        scope.SetExtra("contactEmail", string.IsNullOrWhiteSpace(input.ContactEmail) ? string.Empty : input.ContactEmail.Trim());

                        if (input.IncludeRecentLogs)
                        {
                            AttachRecentLogs(scope);
                        }
                    },
                    SentryLevel.Error);

                Log.Information("Manual bug report submitted. EventId={EventId}", eventId.ToString());

                return new BugReportResult
                {
                    Success = true,
                    EventId = eventId.ToString(),
                    Message = "Thanks! Your bug report was submitted."
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Manual bug report submission failed.");
                return new BugReportResult
                {
                    Success = false,
                    Message = $"Unable to submit bug report: {ex.Message}"
                };
            }
        }

        private static void AttachRecentLogs(Scope scope)
        {
            string logDirectory = LoggingService.GetLogDirectory();
            if (!Directory.Exists(logDirectory))
            {
                return;
            }

            int maxFiles = LoggingService.GetBugReportingMaxAttachedLogs();
            string[] latestFiles = Directory.EnumerateFiles(logDirectory, "*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(maxFiles)
                .ToArray();

            foreach (string file in latestFiles)
            {
                scope.AddAttachment(file);
            }
        }

        private static string GetAppVersion()
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        }
    }
}
