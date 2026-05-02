using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Hexel.Services;
using Hexel.ViewModels;

namespace Hexel
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

            _serviceProvider.GetRequiredService<MainWindow>().Show();
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            // All singletons so state is shared across the app lifetime
            services.AddSingleton<ICodeGeneratorService, CodeGeneratorService>();
            services.AddSingleton<IDrawingService, DrawingService>();
            services.AddSingleton<IHistoryService, HistoryService>();
            services.AddSingleton<ISelectionService, SelectionService>();
            services.AddSingleton<IClipboardService, ClipboardService>();
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IFileService, FileService>();
            services.AddSingleton<MainViewModel>();

            // MainWindow is transient (created once on startup, not pooled)
            services.AddTransient<MainWindow>(sp => new MainWindow(
                sp.GetRequiredService<MainViewModel>(),
                sp.GetRequiredService<ISelectionService>()));
        }
    }
}
