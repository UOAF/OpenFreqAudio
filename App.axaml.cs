using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using BMSAudioSim.ViewModels;
using BMSAudioSim.Views;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BMSAudioSim;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        
        #if DEBUG
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .MinimumLevel.Debug()
            .CreateLogger();
        #else
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .MinimumLevel.Information()
            .CreateLogger();
        #endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            var services = new ServiceCollection();
        
            services.AddLogging(loggingBuilder =>
                loggingBuilder.AddSerilog(dispose: true));
        
            // Register MainWindow in DI
            services.AddTransient<MainWindow>();
            services.AddSingleton<MainWindowViewModel>();
        
            var serviceProvider = services.BuildServiceProvider();
        
            // Get MainWindow from DI - it will get ILoggerFactory injected
            desktop.MainWindow = serviceProvider.GetRequiredService<MainWindow>();

        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}