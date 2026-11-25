// -----------------------------------------------------------------------
// <copyright file="App.xaml.cs" company="MonzoManager">
// Copyright (c) MonzoManager. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.IO;
using Microsoft.UI.Xaml;
using Serilog;

namespace MonzoManager;

/// <summary>
/// Entry point for the Parc Spend WinUI application.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// Configures logging and launches the main application window.
    /// </summary>
    /// <param name="args">Launch event arguments.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        ConfigureLogging();
        Log.Information("Starting Parc Spend.");
        _window = new MainWindow();
        _window.Activate();
    }

    /// <summary>
    /// Records the type of an unhandled UI error without serializing its contents.
    /// </summary>
    /// <param name="sender">The application instance.</param>
    /// <param name="e">The unhandled UI exception event.</param>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception ({ErrorType}).", e.Exception.GetType().Name);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="App"/> class.
    /// </summary>
    public App()
    {
        this.InitializeComponent();
        this.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.CloseAndFlush();
    }

    private static void ConfigureLogging()
    {
        string appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MonzoManager");
        Directory.CreateDirectory(appDataDirectory);

        string logPath = Path.Combine(appDataDirectory, "logs", "app-.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7,
                fileSizeLimitBytes: 1024 * 1024, rollOnFileSizeLimit: true)
            .CreateLogger();
    }
}
