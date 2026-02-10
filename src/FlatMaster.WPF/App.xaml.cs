using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Configuration;
using FlatMaster.Infrastructure.Services;
using FlatMaster.WPF.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlatMaster.WPF;

public partial class App : Application
{
    public IServiceProvider ServiceProvider { get; private set; } = null!;
    public IConfiguration Configuration { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            HookGlobalExceptionHandlers();

            // Build configuration
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

            var baseConfig = builder.Build();
            var autoConfig = GetAutoTunedMetadataSettings(baseConfig);
            if (autoConfig.Count > 0)
            {
                builder.AddInMemoryCollection(autoConfig);
            }

            Configuration = builder.Build();

            // Configure services
            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            ServiceProvider = serviceCollection.BuildServiceProvider();

            // Show main window
            var mainWindow = ServiceProvider.GetRequiredService<Views.MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
            Shutdown(1);
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Configuration
        services.AddSingleton(Configuration);

        // Options
        services.Configure<MetadataReaderOptions>(Configuration.GetSection("MetadataReader"));

        // Logging
        var logPath = Path.Combine(Path.GetTempPath(), $"FlatMaster_detailed_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        services.AddLogging(configure =>
        {
            configure.AddConsole();
            configure.AddDebug();
            configure.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
            configure.AddFile(logPath, minimumLevel: LogLevel.Debug);
            configure.SetMinimumLevel(LogLevel.Debug);
        });
        
        // Store log path for UI access
        Configuration["Runtime:DetailedLogPath"] = logPath;

        // Memory cache (unbounded unless limit configured)
        var cacheLimit = Configuration.GetValue<int>("MetadataReader:CacheSizeLimitEntries");
        services.AddMemoryCache(options =>
        {
            if (cacheLimit > 0)
            {
                options.SizeLimit = cacheLimit;
            }
        });

        // Core services
        services.AddSingleton<IMetadataReaderService, MetadataReaderService>();
        services.AddSingleton<IFileScannerService, FileScannerService>();
        services.AddSingleton<IDarkMatchingService, DarkMatchingService>();
        services.AddSingleton<IPixInsightService, PixInsightService>();
        services.AddSingleton<IImageProcessingEngine, NativeProcessingService>(); // Native Engine
        services.AddSingleton<IProcessingReportService, ProcessingReportService>();
        services.AddSingleton<IOutputPathService, OutputPathService>();

        // ViewModels
        services.AddTransient<MainViewModel>();

        // Views
        services.AddTransient<Views.MainWindow>();
    }

    private static void HookGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                ReportStartupFailure(ex);
            }
        };

        Dispatcher.CurrentDispatcher.UnhandledException += (_, args) =>
        {
            ReportStartupFailure(args.Exception);
            args.Handled = true;
        };
    }

    private static void ReportStartupFailure(Exception ex)
    {
        var message = "FlatMaster failed to start.\n\n" + ex;
        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), "FlatMaster_startup.log");
            File.AppendAllText(logPath, DateTime.Now + "\n" + ex + "\n\n");
            message += "\n\nDetails were written to: " + logPath;
        }
        catch
        {
            // Ignore logging failures
        }

        MessageBox.Show(message, "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static Dictionary<string, string?> GetAutoTunedMetadataSettings(IConfiguration baseConfig)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var configuredParallelism = baseConfig.GetValue<int>("MetadataReader:MaxParallelism");
        if (configuredParallelism > 0)
        {
            return settings;
        }

        var cores = Math.Max(1, Environment.ProcessorCount);
        var availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var availableGiB = availableBytes > 0 ? availableBytes / (1024.0 * 1024.0 * 1024.0) : 0.0;

        var multiplier = availableGiB switch
        {
            >= 96.0 => 8,
            >= 64.0 => 6,
            >= 32.0 => 4,
            _ => 2
        };

        var maxParallelism = cores * multiplier;
        maxParallelism = Math.Clamp(maxParallelism, 8, 256);

        settings["MetadataReader:MaxParallelism"] = maxParallelism.ToString();
        return settings;
    }
}
