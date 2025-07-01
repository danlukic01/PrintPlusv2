
using CommonInterfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrintPlusService;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using Microsoft.Extensions.Configuration;
using PrintPlusService.Services;

/// <summary>
/// Worker class that runs the main background service for file processing.
/// </summary>
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public Worker(ILogger<Worker> logger, IConfiguration configuration, IServiceProvider serviceProvider)
    {
        _logger = logger;  // Injected logger instance
        _serviceProvider = serviceProvider; // Injected service provider for creating scopes
        _configuration = configuration; // Injected configuration for accessing appsettings.json or environment variables
    }


    /// <summary>
    /// The main execution loop for the background service.
    /// Continuously processes files until the service is stopped.
    /// </summary>
    /// <param name="stoppingToken">Token used to stop the service gracefully.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Print Processing Service starting at: {time}", DateTimeOffset.Now);

        // Continuously process files while the service is running
        while (!stoppingToken.IsCancellationRequested)
        {
            // Create a new DI scope for each iteration of the loop.
            // This ensures all scoped services (including DbContext) are fresh for each batch.
            using (var scope = _serviceProvider.CreateScope())
            {
                var fileWatcherService = scope.ServiceProvider.GetRequiredService<IFileWatcherService>();
                var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();

                try
                {
                    // Fetch the SAP server settings from the database
                    var sapFolderSettings = await dataAccess.GetSapFolderSettingsAsync();
                    if (sapFolderSettings == null || !sapFolderSettings.Any())
                    {
                        _logger.LogError("No SAP server settings found in the database.");
                        return; // Exit this iteration; will retry next loop.
                    }

                    var sapServer = sapFolderSettings.First();
                    var inFolderPath = Path.Combine(sapServer.Path, sapServer.Name, "IN");

                    // Check if the path is a network path (UNC path starts with \\)
                    if (sapServer.Path.StartsWith(@"\\"))
                    {
                        // Attempt to map network drive (network authentication)
                        bool isDriveMapped = await MapNetworkDrive(sapServer.Path, sapServer.User, sapServer.Password);

                        if (!isDriveMapped)
                        {
                            _logger.LogError("Failed to map the network drive.");
                            return; // or handle it accordingly
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"Local path detected: {sapServer.Path}. No network mapping required.");
                    }

                    // Start the file watcher service
                    fileWatcherService.StartWatching(inFolderPath);

                    // Process files in the queue. 
                    // No extra DI scope is created here. Use the already scoped services.
                    await fileWatcherService.ProcessFilesFromQueue(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred while initializing the file watcher service or fetching SAP server settings.");
                }
            }

            // Wait a bit before the next loop iteration to avoid a tight loop
            await Task.Delay(1000, stoppingToken);
        }

        _logger.LogInformation("Print Processing Service stopping at: {time}", DateTimeOffset.Now);
    }


    // Method to map network drive
    private async Task<bool> MapNetworkDrive(string path, string user, string password)
    {
        try
        {
            // Disconnect existing connection to the share (if any)
            var disconnectResult = Util.LaunchExe("net", $@"use {path} /delete");
            if (disconnectResult.ExitCode != 0)
            {
                _logger.LogWarning("No existing connection to delete or disconnection failed.");
            }
            else
            {
                _logger.LogInformation($"Disconnected existing network drive: {path}");
            }

            // Map network drive
            var mapResult = Util.LaunchExe("net", $@"use {path} /user:{user} {password}");
            if (mapResult.ExitCode == 0)
            {
                _logger.LogInformation($"Successfully mapped network drive: {path}");
                return true;
            }
            else
            {
                _logger.LogError($"Error mapping network drive: {mapResult.Output}");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception occurred while mapping the network drive.");
            return false;
        }
    }

    /// <summary>
    /// Processes files from the queue using the file watcher service within a scoped service context.
    /// </summary>
    /// <param name="fileWatcherService">The file watcher service responsible for file processing.</param>
    /// <param name="stoppingToken">Token used to stop the operation gracefully.</param>
    private async Task ProcessFilesWithScopeAsync(IFileWatcherService fileWatcherService, CancellationToken stoppingToken)
    {
        // Create a new scope for each set of files processed to ensure proper handling of scoped services
        using (var scope = _serviceProvider.CreateScope())
        {
            var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();

            try
            {
                // Process the files in the queue
                await fileWatcherService.ProcessFilesFromQueue(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during file processing.");
            }
        }
    }
}
