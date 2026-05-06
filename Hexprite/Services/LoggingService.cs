using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Serilog.Context;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Sentry;

namespace Hexprite.Services
{
    public static class LoggingService
    {
        private const string DefaultAppName = "Hexel";
        private const string AppSettingsFile = "appsettings.json";
        private static readonly string UserSettingsDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hexprite");
        private static readonly string UserPrivacySettingsFile =
            Path.Combine(UserSettingsDirectory, "privacy-settings.json");
        private static string? _currentSessionId;

        public static void Initialize()
        {
            string appName = GetAppName();
            IConfiguration configuration = BuildConfiguration();
            string appNameFromConfig = configuration["Logging:AppName"] ?? appName;
            string logDirectory = BuildLogDirectory(appNameFromConfig);

            try
            {
                Directory.CreateDirectory(logDirectory);
                ConfigureSerilogSelfDiagnostics(logDirectory);

                string? sentryDsn = configuration["Sentry:Dsn"];
                string sentryEnvironment = configuration["Sentry:Environment"] ?? "beta";
                bool sentryAutoSessionTracking = ParseBool(configuration["Sentry:AutoSessionTracking"], true);

                LogEventLevel minimumLevel = ParseLogLevel(configuration["Logging:MinimumLevel"], LogEventLevel.Information);
                LogEventLevel sentryBreadcrumbLevel = ParseLogLevel(configuration["Sentry:MinimumBreadcrumbLevel"], LogEventLevel.Information);
                LogEventLevel sentryEventLevel = ParseLogLevel(configuration["Sentry:MinimumEventLevel"], LogEventLevel.Error);

                int retainedFileCount = ParseInt(configuration["Logging:RetainedFileCountLimit"], 14);
                int fileSizeLimitMb = ParseInt(configuration["Logging:FileSizeLimitMB"], 10);

                string sessionId = Guid.NewGuid().ToString("N");
                _currentSessionId = sessionId;

                var loggerConfiguration = new LoggerConfiguration()
                    .MinimumLevel.Is(minimumLevel)
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("System", LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .Enrich.WithMachineName()
                    .Enrich.WithThreadId()
                    .Enrich.WithProperty("AppName", appNameFromConfig)
                    .Enrich.WithProperty("AppVersion", GetAppVersion())
                    .Enrich.WithProperty("Environment", sentryEnvironment)
                    .Enrich.WithProperty("SessionId", sessionId)
                    .Enrich.WithProperty("ProcessId", Environment.ProcessId)
                    .WriteTo.File(
                        path: Path.Combine(logDirectory, "log-.txt"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: retainedFileCount,
                        fileSizeLimitBytes: fileSizeLimitMb * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        shared: true,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] (Thread:{ThreadId} Process:{ProcessId} Session:{SessionId}) {Message:lj}{NewLine}{Exception}");

                // Serilog's Sentry sink initializes its own SDK; it does not pick up a separate SentrySdk.Init.
                // DSN and core options must be set on the sink's options or GetDsn() throws.
                if (!string.IsNullOrWhiteSpace(sentryDsn))
                {
                    loggerConfiguration = loggerConfiguration.WriteTo.Sentry(options =>
                    {
                        options.Dsn = sentryDsn;
                        options.Environment = sentryEnvironment;
                        options.Release = $"{appNameFromConfig}@{GetAppVersion()}";
                        options.AutoSessionTracking = sentryAutoSessionTracking;
                        options.AttachStacktrace = true;
                        options.MinimumBreadcrumbLevel = sentryBreadcrumbLevel;
                        options.MinimumEventLevel = sentryEventLevel;
                    });
                }

                Log.Logger = loggerConfiguration.CreateLogger();
                LogStartupSummary(logDirectory, minimumLevel, retainedFileCount, fileSizeLimitMb, !string.IsNullOrWhiteSpace(sentryDsn));

                if (string.IsNullOrWhiteSpace(sentryDsn))
                {
                    Log.Warning(
                        "Sentry DSN is not configured. Set Sentry:Dsn via dotnet user-secrets (dev), environment variable HEXEL_Sentry__Dsn, or appsettings.json. Optional: Sentry:CrashFlushTimeoutSeconds (1-30) or HEXEL_Sentry__CrashFlushTimeoutSeconds for terminating-crash upload wait.");
                }
            }
            catch (Exception ex)
            {
                SetupFallbackConsoleLogger();
                Log.Error(ex, "Failed to initialize file/Sentry logging. Falling back to console logger.");
            }
        }

        public static void Shutdown()
        {
            try
            {
                Log.Information("Shutting down logging.");
                Log.CloseAndFlush();
            }
            finally
            {
                SentrySdk.Close();
            }
        }

        public static string GetLogDirectory()
        {
            IConfiguration configuration = BuildConfiguration();
            string appName = configuration["Logging:AppName"] ?? GetAppName();
            return BuildLogDirectory(appName);
        }

        /// <summary>
        /// Max number of newest log files to attach to manual bug reports (clamped 1–10).
        /// Config: BugReporting:MaxAttachedLogs; override: HEXEL_BugReporting__MaxAttachedLogs
        /// </summary>
        public static int GetBugReportingMaxAttachedLogs()
        {
            IConfiguration configuration = BuildConfiguration();
            int n = ParseInt(configuration["BugReporting:MaxAttachedLogs"], 2);
            return Math.Clamp(n, 1, 10);
        }

        public static PrivacyOptions GetPrivacyOptions()
        {
            IConfiguration configuration = BuildConfiguration();
            var defaults = new PrivacyOptions(
                telemetryEnabled: ParseBool(configuration["Privacy:TelemetryEnabled"], true),
                attachLogsByDefault: ParseBool(configuration["Privacy:AttachLogsByDefault"], false),
                allowLogAttachments: ParseBool(configuration["Privacy:AllowLogAttachments"], true),
                redactPersonalData: ParseBool(configuration["Privacy:RedactPersonalData"], true),
                shareContactEmailByDefault: ParseBool(configuration["Privacy:ShareContactEmailByDefault"], false),
                allowContactEmailInTelemetry: ParseBool(configuration["Privacy:AllowContactEmailInTelemetry"], false));

            PrivacyOptions? saved = LoadPrivacyOptionsFromDisk();
            return saved is null ? defaults : defaults.MergeWith(saved);
        }

        public static bool SavePrivacyOptions(PrivacyOptions options)
        {
            try
            {
                Directory.CreateDirectory(UserSettingsDirectory);
                string json = JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(UserPrivacySettingsFile, json);
                Log.Information("Saved privacy settings to {SettingsFile}", UserPrivacySettingsFile);
                return true;
            }
            catch (Exception ex)
            {
                HandledErrorReporter.Error(ex, "LoggingService.SavePrivacyOptions", new { UserPrivacySettingsFile });
                return false;
            }
        }

        public static string SanitizeForTelemetry(string? value)
        {
            PrivacyOptions options = GetPrivacyOptions();
            return SanitizeForTelemetry(value, options, allowEmail: false);
        }

        public static string SanitizeForTelemetry(string? value, PrivacyOptions options, bool allowEmail)
        {
            string text = value?.Trim() ?? string.Empty;
            if (!options.RedactPersonalData || text.Length == 0)
            {
                return text;
            }

            text = RedactWindowsPaths(text);
            if (!allowEmail)
            {
                text = RedactEmails(text);
            }

            return text;
        }

        /// <summary>
        /// Attaches the newest rolling log files to a Sentry scope (manual bug or feedback reports).
        /// </summary>
        public static void AttachRecentLogFilesToScope(Scope scope)
        {
            try
            {
                PrivacyOptions privacy = GetPrivacyOptions();
                if (!privacy.AllowLogAttachments)
                {
                    Log.Information("Log attachment disabled by privacy settings.");
                    return;
                }

                string logDirectory = GetLogDirectory();
                if (!Directory.Exists(logDirectory))
                {
                    return;
                }

                int maxFiles = GetBugReportingMaxAttachedLogs();
                string[] latestFiles = Directory.EnumerateFiles(logDirectory, "*.txt")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(maxFiles)
                    .ToArray();

                foreach (string file in latestFiles)
                {
                    scope.AddAttachment(file);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Unable to attach recent log files to report scope.");
            }
        }

        /// <summary>
        /// Creates a timing scope that logs operation start, completion, and duration.
        /// </summary>
        public static IDisposable BeginOperation(string operationName, object? context = null)
        {
            return new TimedOperationScope(operationName, context);
        }

        /// <summary>
        /// Pushes standard operation properties into Serilog's log context.
        /// Usage: using var _ = LoggingService.PushContext("OpenFile", new { path });
        /// </summary>
        public static IDisposable PushContext(string operationName, object? context = null)
        {
            IDisposable op = LogContext.PushProperty("Operation", operationName);
            IDisposable? payload = context is null ? null : LogContext.PushProperty("OperationContext", context, true);
            return new CompositeDisposable(op, payload);
        }

        private static string GetAppName()
        {
            return Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyProductAttribute>()?
                .Product
                ?? DefaultAppName;
        }

        private static string GetAppVersion()
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        }

        private static IConfiguration BuildConfiguration()
        {
            string basePath = AppContext.BaseDirectory;

            return new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile(AppSettingsFile, optional: true, reloadOnChange: false)
                .AddUserSecrets(typeof(LoggingService).Assembly, optional: true)
                .AddEnvironmentVariables(prefix: "HEXEL_")
                .Build();
        }

        private static string BuildLogDirectory(string appName)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                appName,
                "Logs");
        }

        private static void ConfigureSerilogSelfDiagnostics(string logDirectory)
        {
            string selfLogPath = Path.Combine(logDirectory, "serilog-selflog.txt");
            SelfLog.Enable(message =>
            {
                try
                {
                    File.AppendAllText(selfLogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
                }
                catch
                {
                    // Never throw from self-diagnostic channel.
                }
            });
        }

        private static void SetupFallbackConsoleLogger()
        {
            string fallbackPath = Path.Combine(Path.GetTempPath(), "hexel-fallback-log.txt");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .Enrich.FromLogContext()
                .WriteTo.File(
                    fallbackPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    shared: true)
                .CreateLogger();
        }

        private static void LogStartupSummary(
            string logDirectory,
            LogEventLevel minimumLevel,
            int retainedFileCount,
            int fileSizeLimitMb,
            bool sentryEnabled)
        {
            Log.Information(
                "Logging initialized. Directory={LogDirectory} MinLevel={MinLevel} RetainedFileCount={RetainedFileCount} FileSizeLimitMB={FileSizeLimitMB} SentryEnabled={SentryEnabled} SessionId={SessionId}",
                logDirectory,
                minimumLevel,
                retainedFileCount,
                fileSizeLimitMb,
                sentryEnabled,
                _currentSessionId ?? "unknown");
        }

        private static bool ParseBool(string? value, bool fallback)
        {
            return bool.TryParse(value, out bool parsed) ? parsed : fallback;
        }

        private static int ParseInt(string? value, int fallback)
        {
            return int.TryParse(value, out int parsed) ? parsed : fallback;
        }

        private static LogEventLevel ParseLogLevel(string? value, LogEventLevel fallback)
        {
            return Enum.TryParse(value, ignoreCase: true, out LogEventLevel parsed) ? parsed : fallback;
        }

        private static string RedactEmails(string input)
        {
            return Regex.Replace(
                input,
                @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
                "[redacted-email]");
        }

        private static string RedactWindowsPaths(string input)
        {
            return Regex.Replace(
                input,
                @"\b[A-Za-z]:\\(?:[^\\/:*?""<>|\r\n]+\\)*[^\\/:*?""<>|\r\n]*",
                "[redacted-path]");
        }

        private static PrivacyOptions? LoadPrivacyOptionsFromDisk()
        {
            try
            {
                if (!File.Exists(UserPrivacySettingsFile))
                {
                    return null;
                }

                string json = File.ReadAllText(UserPrivacySettingsFile);
                return JsonSerializer.Deserialize<PrivacyOptions>(json);
            }
            catch (Exception ex)
            {
                HandledErrorReporter.Warning(ex, "LoggingService.LoadPrivacyOptionsFromDisk", new { UserPrivacySettingsFile });
                return null;
            }
        }

        public sealed class PrivacyOptions
        {
            public bool TelemetryEnabled { get; init; }
            public bool AttachLogsByDefault { get; init; }
            public bool AllowLogAttachments { get; init; }
            public bool RedactPersonalData { get; init; }
            public bool ShareContactEmailByDefault { get; init; }
            public bool AllowContactEmailInTelemetry { get; init; }

            public PrivacyOptions()
            {
            }

            public PrivacyOptions(
                bool telemetryEnabled,
                bool attachLogsByDefault,
                bool allowLogAttachments,
                bool redactPersonalData,
                bool shareContactEmailByDefault,
                bool allowContactEmailInTelemetry)
            {
                TelemetryEnabled = telemetryEnabled;
                AttachLogsByDefault = attachLogsByDefault;
                AllowLogAttachments = allowLogAttachments;
                RedactPersonalData = redactPersonalData;
                ShareContactEmailByDefault = shareContactEmailByDefault;
                AllowContactEmailInTelemetry = allowContactEmailInTelemetry;
            }

            public PrivacyOptions MergeWith(PrivacyOptions? overrideOptions)
            {
                if (overrideOptions is null)
                {
                    return this;
                }

                return new PrivacyOptions(
                    telemetryEnabled: overrideOptions.TelemetryEnabled,
                    attachLogsByDefault: overrideOptions.AttachLogsByDefault,
                    allowLogAttachments: overrideOptions.AllowLogAttachments,
                    redactPersonalData: overrideOptions.RedactPersonalData,
                    shareContactEmailByDefault: overrideOptions.ShareContactEmailByDefault,
                    allowContactEmailInTelemetry: overrideOptions.AllowContactEmailInTelemetry);
            }
        }

        private sealed class TimedOperationScope : IDisposable
        {
            private readonly string _operationName;
            private readonly object? _context;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            public TimedOperationScope(string operationName, object? context)
            {
                _operationName = operationName;
                _context = context;
                _stopwatch = Stopwatch.StartNew();

                if (_context is null)
                {
                    Log.Information("Starting operation {Operation}", _operationName);
                }
                else
                {
                    Log.Information("Starting operation {Operation} {@OperationContext}", _operationName, _context);
                }
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _stopwatch.Stop();

                if (_context is null)
                {
                    Log.Information("Completed operation {Operation} in {ElapsedMs}ms", _operationName, _stopwatch.ElapsedMilliseconds);
                    return;
                }

                Log.Information(
                    "Completed operation {Operation} in {ElapsedMs}ms {@OperationContext}",
                    _operationName,
                    _stopwatch.ElapsedMilliseconds,
                    _context);
            }
        }

        private sealed class CompositeDisposable : IDisposable
        {
            private readonly IDisposable _first;
            private readonly IDisposable? _second;

            public CompositeDisposable(IDisposable first, IDisposable? second)
            {
                _first = first;
                _second = second;
            }

            public void Dispose()
            {
                _second?.Dispose();
                _first.Dispose();
            }
        }
    }
}
