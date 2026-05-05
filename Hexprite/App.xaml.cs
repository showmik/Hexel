using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Hexprite.Services;
using Hexprite.ViewModels;

namespace Hexprite
{
    public partial class App : Application
    {
        private IServiceProvider _serviceProvider = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            // Apply the persisted theme before showing any UI
            var themeService = (ThemeService)_serviceProvider.GetRequiredService<IThemeService>();
            themeService.Initialize();

            _serviceProvider.GetRequiredService<MainWindow>().Show();
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            // Shared services (stateless or app-wide)
            services.AddSingleton<ICodeGeneratorService, CodeGeneratorService>();
            services.AddSingleton<IDrawingService, DrawingService>();
            services.AddSingleton<IClipboardService, ClipboardService>();
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IThemeService, ThemeService>();

            // ShellViewModel is the app-level VM that manages tabs
            services.AddSingleton<ShellViewModel>();

            // MainWindow is transient (created once on startup)
            services.AddTransient<MainWindow>(sp => new MainWindow(
                sp.GetRequiredService<ShellViewModel>()));
        }
    }
}
