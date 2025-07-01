using CommonInterfaces;
using CommonServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrintPlusService.Services;
using PrintPlusService.Utilities;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Data.SqlClient;
using Dropbox.Api.Files;



/// <summary>
/// Entry point for the PrintPlus application.
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        // Path to the application settings file for logging configuration
        var logConfigPath = "appsettings.json"; // Just the filename

        try
        {
            // NEW: Load config and determine which DB provider to use
            var config = new ConfigurationBuilder()
               .SetBasePath(AppDomain.CurrentDomain.BaseDirectory) 
               .AddJsonFile(logConfigPath)
               .Build();

            // Configure Serilog for logging using the configuration file
            Log.Logger = new LoggerConfiguration()
              .ReadFrom.Configuration(config)
              .CreateLogger();

            Log.Information("Starting PrintPlus application");

            var useSqlServer = config.GetValue<bool>("UseSqlServer"); // NEW: Read UseSqlServer switch

            var host = CreateHostBuilder(args, useSqlServer, config).Build(); // NEW: Pass switch/config

            //// Build the application host
            //var host = CreateHostBuilder(args).Build();

            // Create a service scope for initializing database and configurations
            using (var scope = host.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                try
                {
                    // Retrieve the database context
                    var context = services.GetRequiredService<PrintPlusContext>();
                    // Set connection timeout
                    context.Database.SetCommandTimeout(180);
                    // Log the connection string for verification
                    var connectionString = context.Database.GetDbConnection().ConnectionString;
                 
                    // Ensure the database is created
                    context.Database.EnsureCreated();

                    // NEW: Only run PRAGMA for SQLite
                    if (!useSqlServer)
                    {
                        using (var connection = context.Database.GetDbConnection())
                        {
                            connection.Open();
                            using (var command = connection.CreateCommand())
                            {
                                command.CommandText = "PRAGMA journal_mode=WAL;";
                                command.ExecuteNonQuery();
                            }
                        }
                    }

                    // Apply pending migrations to update the database schema
                   // context.Database.Migrate();
                }
                catch (Exception ex)
                {    
                    // Log any exceptions during database initialization
                    var logger = services.GetRequiredService<ILogger<Program>>();
                    logger.LogError(ex, "An error occurred while migrating the database.");
                }
            }

            // Run the application host
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            // Log any unhandled exceptions during application startup
            Log.Fatal(ex, "An unhandled exception occurred");
        }
        finally
        {
            Log.Information("Shutting down PrintPlus application");

            // Flush and close Serilog resources
            Log.CloseAndFlush();
        }
    }

   

    /// <summary>
    /// Configures the application host with services, logging, and other dependencies.
    /// </summary>
    // NEW: Overload CreateHostBuilder to take useSqlServer/config
    public static IHostBuilder CreateHostBuilder(string[] args, bool useSqlServer, IConfiguration config) =>
     Host.CreateDefaultBuilder(args)
         .UseSerilog() // Use Serilog for logging
         .UseWindowsService() // Configure as a Windows service
         .ConfigureServices((context, services) =>
         {
             services.AddSignalR(); // SignalR services
             services.Configure<ApiSettings>(context.Configuration.GetSection("ApiSettings"));
             services.AddSingleton<ILoggerService, SerilogLoggerService>();
             services.AddSingleton<IFileConverter, FileConverter>();
             services.AddScoped<ISharePointService, SharePointService>();
             services.AddHttpClient(); // Register HttpClientFactory

             // NEW: Register DbContext based on provider switch
             if (useSqlServer)
             {
                 string sqlConnectionString = config.GetConnectionString("SqlServerConnection");

                 services.AddDbContextFactory<PrintPlusContext>(options =>
                     options.UseSqlServer(sqlConnectionString));

                 // Extract database name using SqlConnectionStringBuilder
                 var builder = new SqlConnectionStringBuilder(sqlConnectionString);
                 string dbName = builder.InitialCatalog;

                 Log.Information("Application successfully connected to SQL Server database: {DatabaseName}", dbName);
             }
             else
             {
                 services.AddDbContext<PrintPlusContext>(options =>
                     options.UseSqlite(config.GetConnectionString("dbConnection")));

                 Log.Information("Application successfully connected to SQLite database.");
             }

             services.AddScoped<Func<PrintPlusContext>>(provider => () => provider.GetService<PrintPlusContext>());
             services.AddScoped<IDataAccess, DataAccess>();
             services.AddScoped<IFileProcessor, FileProcessor>();
             services.AddScoped<IFileWatcherService, FileWatcherService>();
             services.AddHostedService<Worker>();
             services.AddScoped<IConfigurationService, ConfigurationService>();
             services.AddScoped<IPrinterService, PrinterService>();
             services.AddScoped<IWebClientWrapper, WebClientWrapper>();

             // Registering the SAPRfcClient
             services.AddScoped<ISAPRfcClient, SAPRfcClient>();

             // Registering the status reporter
             services.AddScoped<IStatusReporter, StatusReporter>();

             // Registering the SapDMSService
             services.AddScoped<ISapDMSService, SapDMSService>();
             services.AddHttpClient<ISapDMSService, SapDMSService>();

             // Retrieve RFCSettings from the database and register it
             using (var scope = services.BuildServiceProvider().CreateScope())
             {
                 var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                 try
                 {
                     var rfcSettings = dataAccess.GetRFCSettingsAsync().GetAwaiter().GetResult();
                     services.AddSingleton(rfcSettings);
                 }
                 catch (Exception ex)
                 {
                     var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
                     logger.LogError(ex, "An error occurred while retrieving RFC settings.");
                 }
             }
         });
}
