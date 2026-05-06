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
            LoggingService.PrivacyOptions privacy = LoggingService.GetPrivacyOptions();
            using var operation = LoggingService.BeginOperation(
                "BugReportService.SubmitReport",
                new
                {
                    includeRecentLogs = input.IncludeRecentLogs,
                    includeContactEmail = input.IncludeContactEmail,
                    telemetryEnabled = privacy.TelemetryEnabled
                });

            if (!privacy.TelemetryEnabled)
            {
                Log.Warning("Bug report submission blocked because telemetry is disabled in privacy settings.");
                return new BugReportResult
                {
                    Success = false,
                    Message = "Telemetry is disabled in privacy settings."
                };
            }

            try
            {
                string summary = LoggingService.SanitizeForTelemetry(input.Summary, privacy, allowEmail: false);
                string steps = LoggingService.SanitizeForTelemetry(input.StepsToReproduce, privacy, allowEmail: false);
                string expected = LoggingService.SanitizeForTelemetry(input.ExpectedBehavior, privacy, allowEmail: false);
                string actual = LoggingService.SanitizeForTelemetry(input.ActualBehavior, privacy, allowEmail: false);
                string? contactEmail = null;
                if (input.IncludeContactEmail && privacy.AllowContactEmailInTelemetry)
                {
                    string sanitizedEmail = LoggingService.SanitizeForTelemetry(input.ContactEmail, privacy, allowEmail: true);
                    contactEmail = string.IsNullOrWhiteSpace(sanitizedEmail) ? null : sanitizedEmail;
                }

                SentryId eventId = SentrySdk.CaptureMessage(
                    string.IsNullOrWhiteSpace(summary) ? "Manual bug report submitted" : summary,
                    scope =>
                    {
                        scope.SetTag("report.type", "manual");
                        scope.SetTag("report.channel", "in-app");
                        scope.SetTag("app.version", GetAppVersion());
                        scope.SetExtra("stepsToReproduce", steps);
                        scope.SetExtra("expectedBehavior", expected);
                        scope.SetExtra("actualBehavior", actual);
                        scope.SetTag("report.contact_email_included", contactEmail is null ? "false" : "true");
                        if (contactEmail is not null)
                        {
                            scope.SetExtra("contactEmail", contactEmail);
                        }

                        if (input.IncludeRecentLogs && privacy.AllowLogAttachments)
                        {
                            LoggingService.AttachRecentLogFilesToScope(scope);
                        }
                    },
                    SentryLevel.Error);

                // Also send to Sentry's Feedback feature so the report
                // appears on the associated event's feedback tab.
                _ = SentrySdk.CaptureFeedback(
                    BuildBugReportComments(input),
                    string.IsNullOrWhiteSpace(input.ContactEmail) ? null : input.ContactEmail.Trim(),
                    name: null,
                    associatedEventId: eventId);

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

        private static string BuildBugReportComments(BugReportInput input)
        {
            // This ends up in Sentry's "User Feedback" comments field.
            // Keep it readable but compact.
            string summary = string.IsNullOrWhiteSpace(input.Summary) ? "(no summary)" : input.Summary.Trim();
            string steps = string.IsNullOrWhiteSpace(input.StepsToReproduce) ? "(not provided)" : input.StepsToReproduce.Trim();
            string expected = string.IsNullOrWhiteSpace(input.ExpectedBehavior) ? "(not provided)" : input.ExpectedBehavior.Trim();
            string actual = string.IsNullOrWhiteSpace(input.ActualBehavior) ? "(not provided)" : input.ActualBehavior.Trim();

            return
                $"Summary:\n{summary}\n\n" +
                $"Steps to reproduce:\n{steps}\n\n" +
                $"Expected behavior:\n{expected}\n\n" +
                $"Actual behavior:\n{actual}";
        }

        private static string GetAppVersion()
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        }
    }
}
