using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Sentry;

namespace Hexprite.Services
{
    public static class LoggingService
    {
        private const string DefaultAppName = "Hexel";
        private const string AppSettingsFile = "appsettings.json";

        public static void Initialize()
        {
            string appName = GetAppName();
            IConfiguration configuration = BuildConfiguration();
            string appNameFromConfig = configuration["Logging:AppName"] ?? appName;
            string logDirectory = BuildLogDirectory(appNameFromConfig);

            Directory.CreateDirectory(logDirectory);

            string? sentryDsn = configuration["Sentry:Dsn"];
            string sentryEnvironment = configuration["Sentry:Environment"] ?? "beta";
            bool sentryAutoSessionTracking = ParseBool(configuration["Sentry:AutoSessionTracking"], true);

            LogEventLevel minimumLevel = ParseLogLevel(configuration["Logging:MinimumLevel"], LogEventLevel.Information);
            LogEventLevel sentryBreadcrumbLevel = ParseLogLevel(configuration["Sentry:MinimumBreadcrumbLevel"], LogEventLevel.Information);
            LogEventLevel sentryEventLevel = ParseLogLevel(configuration["Sentry:MinimumEventLevel"], LogEventLevel.Error);

            int retainedFileCount = ParseInt(configuration["Logging:RetainedFileCountLimit"], 14);
            int fileSizeLimitMb = ParseInt(configuration["Logging:FileSizeLimitMB"], 10);

            var loggerConfiguration = new LoggerConfiguration()
                .MinimumLevel.Is(minimumLevel)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()
                .Enrich.WithThreadId()
                .WriteTo.File(
                    path: Path.Combine(logDirectory, "log-.txt"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: retainedFileCount,
                    fileSizeLimitBytes: fileSizeLimitMb * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] (Thread:{ThreadId}) {Message:lj}{NewLine}{Exception}");

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
            Log.Information("Logging initialized. Directory: {LogDirectory}", logDirectory);

            if (string.IsNullOrWhiteSpace(sentryDsn))
            {
                Log.Warning(
                    "Sentry DSN is not configured. Set Sentry:Dsn via dotnet user-secrets (dev), environment variable HEXEL_Sentry__Dsn, or appsettings.json. Optional: Sentry:CrashFlushTimeoutSeconds (1–30) or HEXEL_Sentry__CrashFlushTimeoutSeconds for terminating-crash upload wait.");
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

        /// <summary>
        /// Attaches the newest rolling log files to a Sentry scope (manual bug or feedback reports).
        /// </summary>
        public static void AttachRecentLogFilesToScope(Scope scope)
        {
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
    }
}
