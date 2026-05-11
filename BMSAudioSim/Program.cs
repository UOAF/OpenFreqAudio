using Avalonia;
using System;
using Avalonia.Controls;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI.Avalonia;
using Serilog;

namespace BMSAudioSim;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            // prepare and run your App here
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText("BMSAudioSim_errors.log", $"{DateTime.Now}: {ex.Message}\n{ex.StackTrace}\n");
            ShowErrorNotification(ex);
        }
    }

    

    private static async void ShowErrorNotification(Exception ex)
    {
        // Post to UI thread
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            var messageBox = new Window
            {
                Title = "Error",
                Width = 400,
                Height = 200,
                Content = new TextBlock
                {
                    Text = $"An unexpected error occurred: {ex.Message}\n\n{ex.StackTrace}",
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                }
            };

            messageBox.Show();
        });
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}