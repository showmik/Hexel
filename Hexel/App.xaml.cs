using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Hexel.Services;
using Hexel.ViewModels;

namespace Hexel
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private IServiceProvider _serviceProvider;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<ICodeGeneratorService, CodeGeneratorService>();
            services.AddSingleton<IDrawingService, DrawingService>();
            services.AddSingleton<IHistoryService, HistoryService>();

            services.AddSingleton<MainViewModel>();
            services.AddTransient<MainWindow>(sp => new MainWindow(sp.GetRequiredService<MainViewModel>()));
        }
    }

}
