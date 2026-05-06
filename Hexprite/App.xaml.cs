using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Hexprite.Services;
using Hexprite.ViewModels;
using Serilog;

namespace Hexprite
{
    public partial class App : Application
    {
        private IServiceProvider _serviceProvider = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            LoggingService.Initialize();
            Log.Information("Application startup invoked. ArgsCount={ArgsCount}", e.Args?.Length ?? 0);

            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            base.OnStartup(e);

            try
            {
                var services = new ServiceCollection();
                ConfigureServices(services);
                _serviceProvider = services.BuildServiceProvider();

                // Apply the persisted theme before showing any UI
                // FIX: Use the interface directly without downcasting to the concrete type.
                var themeService = _serviceProvider.GetRequiredService<IThemeService>();
                themeService.Initialize();

                _serviceProvider.GetRequiredService<MainWindow>().Show();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application startup failed");
                SentryCrashFlush.TryFlushPendingEvents();
                MessageBox.Show(
                    $"The application could not start.\n\n{ex.Message}",
                    "Hexel",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Information("Application exiting with code {ExitCode}", e.ApplicationExitCode);
            DispatcherUnhandledException -= App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;

            LoggingService.Shutdown();
            base.OnExit(e);
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            // Shared services (stateless or app-wide)
            services.AddSingleton<ICodeGeneratorService, CodeGeneratorService>();
            services.AddSingleton<IDrawingService, DrawingService>();
            services.AddSingleton<IClipboardService, ClipboardService>();
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<IBugReportService, BugReportService>();
            services.AddSingleton<IUserFeedbackService, UserFeedbackService>();

            // ShellViewModel is the app-level VM that manages tabs
            services.AddSingleton<ShellViewModel>();

            // MainWindow is transient (created once on startup)
            services.AddTransient<MainWindow>(sp => new MainWindow(
                sp.GetRequiredService<ShellViewModel>()));
        }

        private static void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            // Serilog Sentry sink forwards Fatal + exception to Sentry when configured.
            Log.Fatal(e.Exception, "Unhandled UI thread exception. IsTerminating=true");
            SentryCrashFlush.TryFlushPendingEvents();

            MessageBox.Show(
                "An unexpected error occurred and the application must close.",
                "Hexel",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            // Keep false so unrecoverable crashes terminate cleanly.
            e.Handled = false;
        }

        private static void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "Unhandled non-UI exception. IsTerminating={IsTerminating}", e.IsTerminating);
                if (e.IsTerminating)
                {
                    SentryCrashFlush.TryFlushPendingEvents();
                }

                return;
            }

            Log.Fatal(
                "Unhandled non-UI exception was a non-Exception object. IsTerminating={IsTerminating} Type={ExceptionType}",
                e.IsTerminating,
                e.ExceptionObject?.GetType().FullName ?? "null");
            if (e.IsTerminating)
            {
                SentryCrashFlush.TryFlushPendingEvents();
            }
        }

        private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Log.Error(e.Exception, "Unobserved task exception. ObservedBeforeSet={ObservedBeforeSet}", e.Observed);
            e.SetObserved();
        }
    }
}
