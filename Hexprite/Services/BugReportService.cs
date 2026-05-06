using System;
using System.Reflection;
using Sentry;
using Serilog;

namespace Hexprite.Services
{
    public sealed class BugReportService : IBugReportService
    {
        public BugReportResult SubmitReport(BugReportInput input)
        {
            using var operation = LoggingService.BeginOperation(
                "BugReportService.SubmitReport",
                new { includeRecentLogs = input.IncludeRecentLogs, hasContactEmail = !string.IsNullOrWhiteSpace(input.ContactEmail) });

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
                            LoggingService.AttachRecentLogFilesToScope(scope);
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
                HandledErrorReporter.Error(ex, "BugReportService.SubmitReport");
                return new BugReportResult
                {
                    Success = false,
                    Message = $"Unable to submit bug report: {ex.Message}"
                };
            }
        }

        private static string GetAppVersion()
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        }
    }
}
