using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf.IO;
using PdfSharpCore.Pdf;
using CommonInterfaces;
using CommonInterfaces.Models;
using System.Globalization;
using PdfSharpCore.Drawing.Layout;
using System.Xml;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Diagnostics;
using PdfSharpCore.Exceptions;
using System.Collections.Concurrent;
using Aspose.Pdf.Operators;
using DocumentFormat.OpenXml.Drawing.Charts;
using static Dropbox.Api.TeamLog.SpaceCapsType;
using System.Text.RegularExpressions;

//using Dropbox.Api;
//using Dropbox.Api.Files;


namespace PrintPlusService.Services
{
    /// <summary>
    /// The FileProcessor class is responsible for processing job files, including parsing XML,
    /// downloading necessary documents, converting files, and managing print jobs.
    /// </summary>
    public class FileProcessor : IFileProcessor
    {
        private readonly ILoggerService _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ISharePointService _sharePointService;
        private readonly IStatusReporter _statusReporter;
        private readonly IPrinterService _printerService;
        private readonly IFileConverter _fileConverter;
        private readonly IDataAccess _dataAccess;
     
        private readonly IConfigurationService _configurationService;
        private readonly ISapDMSService _sapDMSService;
      // private readonly IDropboxService _dropBoxService;

        private bool hasSharepointAttachment = false;
        private string cacheFolderPDFRepairPath = string.Empty;
        private string _currentFilePath;
        bool hasError = false;

        private static readonly ConcurrentDictionary<string, FileLockEntry> _fileLocks = new();
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        private const double PageNumberFontSize = 8;
       
        class FileLockEntry
        {
            public SemaphoreSlim Semaphore { get; } = new(1, 1);
            public DateTime LastUsed { get; set; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Initialises a new instance of the FileProcessor class.
        /// </summary>
        /// <param name="logger">The logger service for logging information and errors.</param>
        /// <param name="serviceScopeFactory">The service scope factory for creating service scopes.</param>
        /// <param name="sharePointService">The SharePoint service for downloading files from SharePoint.</param>
        /// <param name="statusReporter">The status reporter for reporting status updates to SAP.</param>
        /// <param name="printerService">The printer service for managing print jobs.</param>
        /// <param name="fileConverter">The file converter for converting files to PDF format.</param>
        /// <param name="dataAccess">The data access service for interacting with the database.</param>
        /// <param name="configurationService">The configuration service for accessing configuration settings.</param>
        /// <param name="sapDMSService">The SAP DMS service for downloading files from SAP DMS.</param>
        /// <param name="dropBoxService">The Dropbox service for downloading files from Dropbox.</param>
        public FileProcessor(
            ILoggerService logger,
            IServiceScopeFactory serviceScopeFactory,
            ISharePointService sharePointService,
            IStatusReporter statusReporter,
            IPrinterService printerService,
            IFileConverter fileConverter,
            IDataAccess dataAccess,
            IConfigurationService configurationService,
            ISapDMSService sapDMSService
        // IDropboxService dropBoxService
        )
        {
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;
            _sharePointService = sharePointService;
            _statusReporter = statusReporter;
            _printerService = printerService;
            _fileConverter = fileConverter;
            _dataAccess = dataAccess;
            _configurationService = configurationService;
            _sapDMSService = sapDMSService;
            // _dropBoxService = dropBoxService;

            // Fetch number of processes for multithreading from the database
            var numOfProcessesConfig = dataAccess.GetConfigurationSettingAsync("AppSettings", "NumberOfProcesses").Result;

            // Parse the value to an integer and use a default value if the setting is missing or invalid
            int numOfProcesses = 10; // default value
            if (int.TryParse(numOfProcessesConfig?.Value, out int parsedValue))
            {
                numOfProcesses = parsedValue;
            }

            _semaphore = new SemaphoreSlim(numOfProcesses); // Dynamically set the number of concurrent tasks
        }

        //Continuously scans the _fileLocks dictionary and removes entries that are:
        //Not used recently (based on LastUsed time)
        //Not currently in use(based on semaphore count)
        private void StartFileLockCleanupTask(TimeSpan cleanupInterval, TimeSpan idleTimeout)
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        foreach (var kvp in _fileLocks)
                        {
                            var key = kvp.Key;
                            var entry = kvp.Value;

                            // If lock is idle and not in use
                            if (DateTime.UtcNow - entry.LastUsed > idleTimeout &&
                                entry.Semaphore.CurrentCount == 1)
                            {
                                _fileLocks.TryRemove(key, out _);
                                _logger.LogDebug($"Removed unused file lock for: {key}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during file lock cleanup.");
                    }

                    await Task.Delay(cleanupInterval);
                }
            });
        }

        /// <summary>
        /// Processes a job file, including parsing XML, downloading necessary files, and updating job status.
        /// </summary>
        /// <param name="filePath">The path of the file to process.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task representing the asynchronous operation.</returns>

        public async Task ProcessFile(string filePath, CancellationToken cancellationToken = default)
        {
            // Start the stopwatch at the beginning of the method
            var stopwatch = Stopwatch.StartNew();
            var startTime = DateTime.Now;

            string jobId = null;
            string user = null;
            string printer = null;
            string printLocation = null;
            string duplex = null;
            string uniqueJobId = Guid.NewGuid().ToString(); // Initialize a uniqueJobId

            string errorFolderPath = string.Empty;
            string inFolderPath = string.Empty;
            string cacheFolderPath = string.Empty;
            string outFolderPath = string.Empty;
            string xmlFolderPath = string.Empty;

            var fileMappings = new Dictionary<string, string>();
            var errorFilePaths = new List<string>();

            bool hasCriticalError = false; // Flag to track critical errors

            //Cleanup old files
            await CleanUpOldFilesAsync();
            //Cleanup old database logs
            await CleanUpDatabaseAsync();
  
            //Keep locks cached for safety.
            //Clean unused ones after 10 + minutes of inactivity.
            //No race conditions, no excessive memory growth.
            StartFileLockCleanupTask(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

            try
            {
                _logger.LogInformation($"Processing file: {filePath}");

                await WaitForFileExistsAsync(filePath, cancellationToken);

                // Save initial processing log entry
                await _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Information",
                    Message = $"Starting to process file: {filePath}",
                    JobId = uniqueJobId
                });

                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();

                    try
                    {
                        // Get JobId and User from XML, will throw if JobID is missing
                        (jobId, user, printer, printLocation, duplex) = await GetJobIdAndUserFromXmlAsync(filePath, uniqueJobId, dataAccess, cancellationToken);
                    }
                    catch (InvalidOperationException ex)
                    {
                        _logger.LogError(ex, $"Error processing XML file: {filePath}. {ex.Message}");
                        hasError = true;
                        hasCriticalError = true;

                        // Log error to the database
                        await dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = $"Error processing XML file: {filePath}. {ex.Message}",
                            Exception = ex.ToString(),
                            JobId = uniqueJobId
                        });
                        return;

                    }

                    if (!hasCriticalError)
                    {
                        jobId = jobId.PadLeft(10, '0');

                        var job = new Job
                        {
                            JobId = jobId,
                            User = user,
                            Printer = printer,
                            Print = printLocation,
                            Duplex = duplex,
                            XmlFilePath = filePath,
                            Status = "Pending",
                            CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"),
                            UniqueJobId = uniqueJobId
                        };

                        await dataAccess.AddJobAsync(job);

                        await dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Information",
                            Message = $"Job added to database with JobId: {jobId}",
                            JobId = uniqueJobId
                        });

                        var createdJob = await dataAccess.GetJobByIdAsync(jobId);
                        if (createdJob == null)
                        {
                            var errorMessage = $"Job with JobId {jobId} was not found after creation. Exiting.";
                            _logger.LogError(null, errorMessage);

                            await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), errorMessage);

                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Error",
                                Message = errorMessage,
                                JobId = uniqueJobId
                            });

                            hasError = true;
                            return;
                        }

                        uniqueJobId = createdJob.UniqueJobId;

                        var environmentSetting = await dataAccess.GetConfigurationSettingAsync("AppSettings", "Environment");
                        if (environmentSetting == null)
                        {
                            var errorMessage = "Environment configuration not found.";
                            _logger.LogError(null, errorMessage);

                            await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), errorMessage);

                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Error",
                                Message = errorMessage,
                                JobId = uniqueJobId
                            });

                            hasError = true;
                            return;
                        }

                        var environment = environmentSetting.Value;
                        //_logger.LogInformation($"Environment: {environment}");

                        // Logging environment setting retrieval
                        await dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Information",
                            Message = $"Environment setting retrieved: {environmentSetting.Value}",
                            JobId = uniqueJobId
                        });

                        var sapFolderSettings = await dataAccess.GetSapFolderSettingsAsync();
                        var sapServer = sapFolderSettings.FirstOrDefault(s => s.Name == environment);
                        if (sapServer == null)
                        {
                            _logger.LogError(null, "Unable to retrieve SAP server. Exiting.");
                            return;
                        }

                        //_logger.LogInformation($"SAP Server Path: {sapServer.Path}");

                        inFolderPath = Path.Combine(sapServer.Path, sapServer.Name, "IN");
                        cacheFolderPath = Path.Combine(sapServer.Path, sapServer.Name, "Cache", jobId);
                        outFolderPath = Path.Combine(sapServer.Path, sapServer.Name, sapServer.OutDir, jobId);
                        xmlFolderPath = Path.Combine(sapServer.Path, sapServer.Name, "XML");
                        errorFolderPath = Path.Combine(sapServer.Path, sapServer.Name, "Error");

                        Directory.CreateDirectory(cacheFolderPath);
                        Directory.CreateDirectory(outFolderPath);
                        Directory.CreateDirectory(xmlFolderPath);
                        Directory.CreateDirectory(errorFolderPath);

                        // Set cacheFolderPDFRepairPath for pdf repair as needed
                        cacheFolderPDFRepairPath = cacheFolderPath;

                        // Parse work orders from the XML file
                        List<string> workOrderFilePaths;
                        bool enableOperationSplitting = await dataAccess.GetEnableOperationSplittingAsync();

                        if (enableOperationSplitting)
                        {
                            workOrderFilePaths = await ParseAndCreateWorkOrderFilesWithOperationSplitting(filePath, cacheFolderPath, jobId, cancellationToken);
                        }
                        else
                        {
                            workOrderFilePaths = await ParseAndCreateWorkOrderFiles(filePath, cacheFolderPath, jobId, cancellationToken);
                        }

                        // Delete any existing error PDFs in the Cache folder
                        DeleteExistingErrorPdfs(cacheFolderPath, workOrderFilePaths);

                        // Download SAP DMS documents
                        var sapDmsFiles = workOrderFilePaths.Where(fp => IsSapDmsFile(fp)).ToList();
                        _logger.LogInformation($"Downloading {sapDmsFiles.Count} documents from SAP Cloud DMS.");

                        foreach (var sapDmsFile in sapDmsFiles)
                        {
                            try
                            {
                                var sapDmsInfo = sapDmsFile.Split(',');
                                if (sapDmsInfo.Length == 8)
                                {
                                    string destinationFolder = cacheFolderPath;
                                    var downloadedFilePath = await _sapDMSService.DownloadFileAsync(
                                        sapDmsInfo[0].Trim('\''), sapDmsInfo[1].Trim('\''), sapDmsInfo[2].Trim('\''), sapDmsInfo[3].Trim('\''),
                                        sapDmsInfo[4].Trim('\''), sapDmsInfo[5].Trim('\''), sapDmsInfo[6].Trim('\''), sapDmsInfo[7].Trim('\''),
                                        destinationFolder);
                                    workOrderFilePaths.Add(downloadedFilePath);

                                    // Assuming successful download or any operation here, log accordingly
                                    await dataAccess.AddLogEntryAsync(new LogEntry
                                    {
                                        Timestamp = DateTime.UtcNow,
                                        Level = "Information",
                                        Message = $"Successfully downloaded SAP DMS file: {sapDmsFile}",
                                        JobId = uniqueJobId
                                    });
                                }

                                else
                                {
                                    var errorMessage = $"Invalid SAP DMS file format: {sapDmsFile}";
                                    _logger.LogError(null, errorMessage);

                                    var errorFilePath = Path.Combine(cacheFolderPath, $"Error_InvalidSAPFile_{Path.GetFileNameWithoutExtension(sapDmsFile)}.pdf");
                                    SaveErrorPdf(errorFilePath, new List<string> { errorMessage });

                                    await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), "Invalid SAP DMS file format");
                                    await SaveErrorDetails(jobId, new List<string> { errorMessage }, cancellationToken);

                                    await dataAccess.AddLogEntryAsync(new LogEntry
                                    {
                                        Timestamp = DateTime.UtcNow,
                                        Level = "Error",
                                        Message = errorMessage,
                                        // Exception = ex.ToString(),
                                        JobId = uniqueJobId
                                    });

                                    hasError = true;
                                    hasCriticalError = false; // Set critical error flag
                                    break; // Exit the loop
                                }
                            }
                            catch (Exception ex)
                            {
                                var errorMessage = $"Error downloading SAP DMS file: {sapDmsFile} - {ex.Message}";
                                _logger.LogError(ex, errorMessage);

                                var errorFilePath = Path.Combine(cacheFolderPath, $"Error_SAPDMS_{Path.GetFileNameWithoutExtension(sapDmsFile)}.pdf");
                                SaveErrorPdf(errorFilePath, new List<string> { errorMessage });

                                await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), "Error downloading files from SAP DMS");
                                await SaveErrorDetails(jobId, new List<string> { errorMessage }, cancellationToken);

                                hasError = true;
                                hasCriticalError = false; // Set critical error flag
                                break; // Exit the loop
                            }
                        }

                        if (hasCriticalError)
                        {
                            _logger.LogInformation("Critical error occurred during SAP DMS download. Exiting process.");

                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Critical",
                                Message = "Critical error during SAP DMS download, process aborted.",
                                JobId = uniqueJobId
                            });

                            return; // Exit the method or trigger any necessary shutdown sequence
                        }

                        // Download SharePoint documents
                        List<string> urlsToDownload = null;
                        List<string> downloadedFiles = null;
                        var urlToFilePathMapping = new Dictionary<string, string>();

                        try
                        {
                            // Extract URLs and download files from SharePoint
                            urlsToDownload = await ExtractUrlsFromWorkOrderFiles(workOrderFilePaths, cancellationToken);
                            downloadedFiles = await DownloadAllFilesAsync(urlsToDownload, cacheFolderPath, jobId, cancellationToken);

                            if (downloadedFiles.Any())
                                hasSharepointAttachment = true;

                            // Log success to the database
                            await _dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Information",
                                Message = $"Downloaded {downloadedFiles.Count} files from SharePoint.",
                                JobId = uniqueJobId,
                                WorkOrderId = jobId
                            });

                            _logger.LogInformation($"Downloaded {downloadedFiles.Count} files from SharePoint.");

                            // Populate the dictionary with the full URL as the key
                            for (int i = 0; i < urlsToDownload?.Count; i++)
                            {
                                urlToFilePathMapping[urlsToDownload[i]] = downloadedFiles[i];
                            }
                        }
                        catch (Exception ex)
                        {
                            // Log the error to the database
                            await _dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Error",
                                Message = $"Error downloading files from SharePoint: {ex.Message}",
                                Exception = ex.ToString(),
                                JobId = uniqueJobId,
                                WorkOrderId = jobId
                            });

                            _logger.LogError(ex, "Error downloading files from SharePoint.");
                            await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), "Error downloading files from SharePoint");
                            hasError = true;
                            hasCriticalError = false; // Set critical error flag
                        }

                        // Exit if there was a critical error during the SharePoint download
                        if (hasCriticalError)
                        {
                            // Log exit due to critical error
                            await _dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Warning",
                                Message = "Critical error occurred during SharePoint download. Exiting process.",
                                JobId = uniqueJobId,
                                WorkOrderId = jobId
                            });

                            _logger.LogInformation("Critical error occurred during SharePoint download. Exiting process.");
                            return; // Gracefully exit the method
                        }


                        //// Download Dropbox documents
                        //var dropboxFiles = workOrderFilePaths.Where(fp => IsDropboxFile(fp)).ToList();
                        //_logger.LogInformation($"Downloading {dropboxFiles.Count} files from Dropbox.");

                        //foreach (var dropboxFile in dropboxFiles)
                        //{
                        //    try
                        //    {
                        //        string destinationFolder = cacheFolderPath;

                        //        // Download file using DropboxService
                        //        var downloadedFilePath = await _dropBoxService.DownloadFileAsync(dropboxFile.Trim('\''), destinationFolder);

                        //        workOrderFilePaths.Add(downloadedFilePath);

                        //        await dataAccess.AddLogEntryAsync(new LogEntry
                        //        {
                        //            Timestamp = DateTime.UtcNow,
                        //            Level = "Information",
                        //            Message = $"Successfully downloaded Dropbox file: {dropboxFile}",
                        //            JobId = uniqueJobId
                        //        });
                        //    }
                        //    catch (Exception ex)
                        //    {
                        //        var errorMessage = $"Error downloading Dropbox file: {dropboxFile} - {ex.Message}";
                        //        _logger.LogError(ex, errorMessage);

                        //        var errorFilePath = Path.Combine(cacheFolderPath, $"Error_Dropbox_{Path.GetFileNameWithoutExtension(dropboxFile)}.pdf");
                        //        SaveErrorPdf(errorFilePath, new List<string> { errorMessage });

                        //        await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), "Error downloading files from Dropbox");
                        //        await SaveErrorDetails(jobId, new List<string> { errorMessage }, cancellationToken);

                        //        await dataAccess.AddLogEntryAsync(new LogEntry
                        //        {
                        //            Timestamp = DateTime.UtcNow,
                        //            Level = "Error",
                        //            Message = errorMessage,
                        //            JobId = uniqueJobId
                        //        });

                        //        hasError = true;
                        //        hasCriticalError = false; // Set critical error flag

                        //        //break; // Exit the loop
                        //    }
                        //    //catch (Exception exc) when (exc.Message.Contains("expired_access_token"))
                        //    //{
                        //    //    // Access Token expired
                        //    //    var newAccessToken = await _dropBoxService.RefreshAccessTokenAsync(refreshToken, clientId, clientSecret);

                        //    //    // Rebuild the dropboxClient with new token
                        //    //    _dropboxClient = new DropboxClient(newAccessToken);

                        //    //    // Retry the operation
                        //    //    //    var response = await _dropboxClient.Files.DownloadAsync(dropboxPath);
                        //    //}
                        //}

                        //// Exit if there was a critical error during the SharePoint download
                        //if (hasCriticalError)
                        //{
                        //    // Log exit due to critical error
                        //    await _dataAccess.AddLogEntryAsync(new LogEntry
                        //    {
                        //        Timestamp = DateTime.UtcNow,
                        //        Level = "Warning",
                        //        Message = "Critical error occurred during SharePoint download. Exiting process.",
                        //        JobId = uniqueJobId,
                        //        WorkOrderId = jobId
                        //    });

                        //    _logger.LogInformation("Critical error occurred during SharePoint download. Exiting process.");
                        //    return; // Gracefully exit the method
                        //}



                        // Update workOrderFilePaths to exclude URLs since they are already downloaded and mapped
                        /* workOrderFilePaths = workOrderFilePaths
                              .Where(fp => !fp.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                                           !Uri.IsWellFormedUriString(fp, UriKind.Absolute) &&
                                           !IsSapDmsFile(fp))
                              .Select(fp => urlToFilePathMapping.ContainsKey(fp) ? urlToFilePathMapping[fp] : fp)
                              .ToList();*/

                        // Update workOrderFilePaths to exclude URLs, HTTP/HTTPS links, and SAP DMS files since they are already downloaded and mapped
                        workOrderFilePaths = workOrderFilePaths
                            .Where(fp => !fp.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                                         !Uri.IsWellFormedUriString(fp, UriKind.Absolute) &&    // Skip well-formed URIs (including HTTP/HTTPS)
                                         !fp.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && // Skip HTTP links
                                         !fp.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && // Skip HTTPS links
                                         !IsSapDmsFile(fp))  // Custom method to exclude SAP DMS files
                            .Select(fp => urlToFilePathMapping.ContainsKey(fp) ? urlToFilePathMapping[fp] : fp)
                            .ToList();

                        // Copy and convert files
                        try
                        {
                            (fileMappings, errorFilePaths) = await CopyAndConvertFiles(inFolderPath, cacheFolderPath, workOrderFilePaths, uniqueJobId, cancellationToken);

                            //// Log success to the database
                            await _dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Information",
                                Message = $"Files copied and converted successfully for JobId: {jobId}.",
                                JobId = uniqueJobId,
                                WorkOrderId = jobId
                            });

                            _logger.LogInformation("Files copied and converted successfully.");

                        }
                        catch (Exception ex)
                        {
                            var errorMessage = $"Error copying and converting files: {ex.Message}";
                            _logger.LogError(ex, errorMessage);

                            // Log the error to the database
                            await _dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Error",
                                Message = errorMessage,
                                Exception = ex.ToString(),
                                JobId = uniqueJobId,
                                WorkOrderId = jobId
                            });

                            var errorFilePath = Path.Combine(cacheFolderPath, "Error_CopyConvertFiles.pdf");
                            SaveErrorPdf(errorFilePath, new List<string> { errorMessage });

                            await SaveErrorDetails(jobId, new List<string> { errorMessage }, cancellationToken);

                            // Notify SAP about the error
                            await _statusReporter.ReportStatusAsync(int.Parse(jobId), null, "009", errorMessage, "0001", "BUS2007", errorFilePath, cancellationToken);

                            hasError = true;
                        }

                        // Concatenate and print files

                        try
                        {
                            await ConcatenateAndPrintFiles(jobId, uniqueJobId, outFolderPath, cacheFolderPath, filePath, outFolderPath, urlToFilePathMapping, cancellationToken);

                            // Log success to the database
                            await _dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Information",
                                Message = $"Files concatenated and printed successfully for JobId: {jobId}.",
                                JobId = uniqueJobId,
                                WorkOrderId = jobId
                            });

                            _logger.LogInformation("Files concatenated and printed successfully.");

                        }
                        catch (Exception ex)
                        {
                            var errorMessage = $"Error processing PDF files: {ex.Message} : {filePath}";
                            _logger.LogError(ex, errorMessage);

                            // Log the error to the database
                            await _dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Error",
                                Message = errorMessage,
                                Exception = ex.ToString(),
                                JobId = uniqueJobId,
                                WorkOrderId = jobId
                            });

                            var errorFilePath = Path.Combine(cacheFolderPath, "Error_ConcatPrintFiles.pdf");
                            SaveErrorPdf(errorFilePath, new List<string> { errorMessage });

                            await SaveErrorDetails(jobId, new List<string> { errorMessage }, cancellationToken);

                            // Notify SAP about the error
                            await _statusReporter.ReportStatusAsync(int.Parse(jobId), null, "009", errorMessage, "0001", "BUS2007", errorFilePath, cancellationToken);

                            hasError = true;
                        }


                        // Update job and work order statuses to "Processed" if no errors occurred
                        if (!hasError)
                        {
                            await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Processed", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

                            // Log the job status update to the database
                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Information",
                                Message = $"Job {jobId} updated to 'Processed' status.",
                                JobId = uniqueJobId,
                                WorkOrderId = jobId
                            });

                            var workOrdersInDb = await dataAccess.GetWorkOrdersByUniqueJobIdAsync(uniqueJobId);
                            foreach (var workOrder in workOrdersInDb)
                            {
                                if (workOrder.Status != "Error")
                                {
                                    await dataAccess.UpdateWorkOrderStatusAsync(uniqueJobId, workOrder.WorkOrderId, "Processed", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

                                    // Log the work order status update to the database
                                    await dataAccess.AddLogEntryAsync(new LogEntry
                                    {
                                        Timestamp = DateTime.UtcNow,
                                        Level = "Information",
                                        Message = $"WorkOrder {workOrder.WorkOrderId} updated to 'Processed' status.",
                                        JobId = uniqueJobId,
                                        WorkOrderId = workOrder.WorkOrderId
                                    });

                                    var workOrderPartsInDb = await dataAccess.GetWorkOrderPartsAsync(uniqueJobId, workOrder.WorkOrderId);
                                    foreach (var part in workOrderPartsInDb)
                                    {
                                        if (part.Status != "Error")
                                        {
                                            await dataAccess.UpdateWorkOrderPartStatusAsync(uniqueJobId, workOrder.WorkOrderId, part.PartId, "Processed", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

                                            // Log the work order part status update to the database
                                            await dataAccess.AddLogEntryAsync(new LogEntry
                                            {
                                                Timestamp = DateTime.UtcNow,
                                                Level = "Information",
                                                Message = $"WorkOrder Part {part.PartId} for WorkOrder {workOrder.WorkOrderId} updated to 'Processed' status.",
                                                JobId = uniqueJobId,
                                                WorkOrderId = workOrder.WorkOrderId
                                            });
                                        }
                                    }
                                }
                            }
                        }

                        else
                        {
                            // Error handling block
                            string errorTimestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

                            // Retrieve unique job ID and validate if the job exists
                            uniqueJobId = await dataAccess.GetUniqueJobIdByJobIdAsync(jobId);

                            if (uniqueJobId == null)
                            {
                                // If the job does not exist, log an error and exit the block
                                _logger.LogError(null, $"Job with ID {jobId} not found. Cannot proceed with error updates.");

                                // Log the error about missing job ID in the database
                                await dataAccess.AddLogEntryAsync(new LogEntry
                                {
                                    Timestamp = DateTime.UtcNow,
                                    Level = "Error",
                                    Message = $"Job with ID {jobId} not found. Cannot proceed with error updates.",
                                    JobId = jobId
                                });

                                return;
                            }

                            // Fetch the current job status from the database
                            var currentJob = await dataAccess.GetJobByIdAsync(jobId);

                            // Update the job status to "Error" only if it's not already in "Error" state
                            if (currentJob.Status != "Error")
                            {
                                await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", errorTimestamp);

                                // Log the job status update to "Error" in the database
                                await dataAccess.AddLogEntryAsync(new LogEntry
                                {
                                    Timestamp = DateTime.UtcNow,
                                    Level = "Error",
                                    Message = $"Job {jobId} status updated to 'Error'.",
                                    JobId = uniqueJobId,
                                    WorkOrderId = jobId
                                });
                            }

                            // Fetch the work orders related to the job
                            var workOrdersInDb = await dataAccess.GetWorkOrdersByUniqueJobIdAsync(uniqueJobId);

                            foreach (var workOrder in workOrdersInDb)
                            {
                                // Always check and update work order parts, regardless of the work order status
                                var workOrderPartsInDb = await dataAccess.GetWorkOrderPartsAsync(uniqueJobId, workOrder.WorkOrderId);

                                foreach (var part in workOrderPartsInDb)
                                {
                                    // Update work order part status to "Error" if it's not already in "Error" state
                                    if (part.Status != "Error")
                                    {
                                        await dataAccess.UpdateWorkOrderPartStatusAsync(uniqueJobId, workOrder.WorkOrderId, part.PartId, "Error", currentJob.ErrorMessage, errorTimestamp);

                                        // Log the work order part status update to "Error" in the database
                                        await dataAccess.AddLogEntryAsync(new LogEntry
                                        {
                                            Timestamp = DateTime.UtcNow,
                                            Level = "Error",
                                            Message = $"WorkOrder Part {part.PartId} for WorkOrder {workOrder.WorkOrderId} status updated to 'Error'.",
                                            JobId = uniqueJobId,
                                            WorkOrderId = workOrder.WorkOrderId
                                        });
                                    }
                                }
                                // Update work order status to "Error" if it's not already in "Error" state, and if any of its parts are in "Error"
                                if (workOrder.Status != "Error" && workOrderPartsInDb.Any(p => p.Status == "Error"))
                                {
                                    await dataAccess.UpdateWorkOrderStatusAsync(uniqueJobId, workOrder.WorkOrderId, "Error", errorTimestamp);

                                    // Log the work order status update to "Error" in the database
                                    await dataAccess.AddLogEntryAsync(new LogEntry
                                    {
                                        Timestamp = DateTime.UtcNow,
                                        Level = "Error",
                                        Message = $"WorkOrder {workOrder.WorkOrderId} status updated to 'Error' due to part errors.",
                                        JobId = uniqueJobId,
                                        WorkOrderId = workOrder.WorkOrderId
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"An unexpected error occurred while processing file: {filePath}. {ex.Message}");

                // Handle the case where jobId or uniqueJobId might not be set yet
                string currentJobId = jobId ?? "Unknown";
                string currentUniqueJobId = uniqueJobId ?? "Unknown";

                await _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Critical",
                    Message = $"An unexpected error occurred during file processing: {ex.Message}",
                    Exception = ex.ToString(),
                    JobId = currentUniqueJobId,
                    WorkOrderId = currentJobId
                });

                // Attempt to update job status to error if a job ID exists
                if (!string.IsNullOrEmpty(currentJobId) && currentJobId != "Unknown")
                {
                    await _dataAccess.UpdateJobStatusAsync(currentJobId, currentUniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), $"Critical error: {ex.Message}");
                }
            }
            finally
            {
                // Clean up cache and error folders if the job was processed successfully
                if (!hasError && !hasCriticalError)
                {
                    _logger.LogInformation($"Cleaning up cache folder: {cacheFolderPath}");
                    try
                    {
                        // Commented out for testing purposes
                        //if (Directory.Exists(cacheFolderPath))
                        //{
                        //    Directory.Delete(cacheFolderPath, true);
                        //}

                        _logger.LogInformation($"Moved XML file to: {xmlFolderPath}");
                        File.Move(filePath, Path.Combine(xmlFolderPath, Path.GetFileName(filePath)), true);

                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error cleaning up cache folder or moving XML file for job {jobId}: {ex.Message}");
                        // Log the cleanup error to the database
                        await _dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = $"Error during post-processing cleanup for job {jobId}: {ex.Message}",
                            Exception = ex.ToString(),
                            JobId = uniqueJobId
                        });
                    }
                }
                else // If there was an error, move the XML file to the error folder
                {
                    try
                    {
                        File.Move(filePath, Path.Combine(errorFolderPath, Path.GetFileName(filePath)), true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error moving XML file to error folder for job {jobId}: {ex.Message}");
                        // Log the error file move error to the database
                        await _dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = $"Error moving XML file to error folder for job {jobId}: {ex.Message}",
                            Exception = ex.ToString(),
                            JobId = uniqueJobId
                        });
                    }
                }

                // Stop the stopwatch at the end of the method
                stopwatch.Stop();

                var endTime = DateTime.Now;

                // Log the elapsed time
                _logger.LogInformation($"Process Start: {startTime}.");
                _logger.LogInformation($"Process End: {endTime}.");
                _logger.LogInformation($"Finished processing file: {filePath}. Total time elapsed: {stopwatch.Elapsed.Minutes} minute(s) and {stopwatch.Elapsed.Seconds:D2} seconds.");

                // Optionally, add the elapsed time to the database log
                await _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Information",
                    Message = $"Total processing time for file {filePath}: Total time elapsed: {stopwatch.Elapsed.Minutes} minute(s) and {stopwatch.Elapsed.Seconds:D2} seconds.",
                    JobId = uniqueJobId // Use the uniqueJobId if it's available
                });
            }
        }

        /// <summary>
        /// Saves error details for a given job ID, updating the corresponding work orders and parts in the database.
        /// </summary>
        /// <param name="jobId">The ID of the job for which errors are being saved.</param>
        /// <param name="errorMessages">A list of error messages to be saved.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task SaveErrorDetails(string jobId, List<string> errorMessages, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Saving error details for JobId: {jobId}");
            var errorMessage = string.Join("; ", errorMessages);

            try
            {
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();

                    // Fetch the uniqueJobId using jobId
                    var job = await dataAccess.GetJobByIdAsync(jobId);
                    if (job == null)
                    {
                        _logger.LogWarning($"Cannot find Job for JobId {jobId}. Skipping SaveErrorDetails.");
                        return; // Exit if the job is not found
                    }

                    var uniqueJobId = job.UniqueJobId;

                    // Fetch the existing work orders using uniqueJobId
                    var existingWorkOrders = await dataAccess.GetWorkOrdersByUniqueJobIdAsync(uniqueJobId);
                    if (existingWorkOrders == null || !existingWorkOrders.Any())
                    {
                        _logger.LogWarning($"WorkOrders with UniqueJobId {uniqueJobId} not found. Creating a new entry.");

                        // Create a new work order entry for the error if it doesn't exist
                        var newWorkOrder = new WorkOrder
                        {
                            WorkOrderId = jobId,
                            UniqueJobId = uniqueJobId,
                            Status = "Error",
                            ErrorMessage = errorMessage,
                            CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                            ProcessedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                        };
                        await dataAccess.AddWorkOrderAsync(newWorkOrder);

                        // Update job details based on the work order
                        await dataAccess.UpdateJobErrorMessageAsync(jobId, errorMessage);
                        await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

                        // Move the file to the Error folder if _currentFilePath is not null or empty
                        if (!string.IsNullOrEmpty(_currentFilePath))
                        {
                            await MoveFileToErrorFolderAsync(_currentFilePath, cancellationToken);
                        }

                        return;
                    }

                    // Update existing work orders
                    foreach (var existingWorkOrder in existingWorkOrders)
                    {
                        existingWorkOrder.Status = "Error";
                        existingWorkOrder.ErrorMessage = errorMessage;
                        existingWorkOrder.ProcessedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                        await dataAccess.UpdateWorkOrderAsync(existingWorkOrder);
                    }

                    // Fetch and update work order parts using uniqueJobId
                    var workOrderParts = await dataAccess.GetWorkOrderPartsAsync(uniqueJobId);
                    foreach (var part in workOrderParts)
                    {
                        await dataAccess.UpdateWorkOrderPartStatusAsync(uniqueJobId, part.WorkOrderId, part.PartId, "Error", errorMessage, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                    }

                    // Update job details based on the work order
                    await dataAccess.UpdateJobErrorMessageAsync(jobId, errorMessage);
                    await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

                    // Move the file to the Error folder if _currentFilePath is not null or empty
                    if (!string.IsNullOrEmpty(_currentFilePath))
                    {
                        await MoveFileToErrorFolderAsync(_currentFilePath, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"An error occurred while saving error details for JobId: {jobId}");
                throw; // Optionally re-throw or handle the exception as needed
            }
        }


        /// <summary>
        /// Extracts URLs from the provided work order files.
        /// </summary>
        /// <param name="workOrderFilePaths">A list of file paths for the work order files.</param>
        /// <returns>A list of extracted URLs.</returns>
        private async Task<List<string>> ExtractUrlsFromWorkOrderFiles(List<string> workOrderFilePaths, CancellationToken cancellationToken)
        {
            var urls = new List<string>();
            var errorMessages = new List<string>();

            foreach (var filePath in workOrderFilePaths)
            {
                try
                {
                    if (Path.GetExtension(filePath).Equals(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        var document = XDocument.Load(filePath);

                        var urlElements = document.Descendants("WorkOrderPart")
                            .Where(e => e.Element("Type") != null &&
                                        !e.Element("Type").Value.Equals("SAPDMS", StringComparison.OrdinalIgnoreCase) &&
                                        e.Element("Type").Value.Equals("URL", StringComparison.OrdinalIgnoreCase))
                            .Select(e => e.Element("FilePath"))
                            .Where(e => e != null) // avoid nulls
                            .ToList(); // ✅ create a safe copy for iteration

                        foreach (var element in urlElements)
                        {
                            var cdata = element.Nodes().OfType<XCData>().FirstOrDefault();

                            if (cdata != null)
                            {
                                urls.Add(cdata.Value);
                            }
                            else
                            {
                                urls.Add(element.Value);
                            }
                        }

                        // Logging
                        using (var scope = _serviceScopeFactory.CreateScope())
                        {
                            var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Information",
                                Message = $"Successfully extracted URLs from file: {filePath}",
                                JobId = ExtractWorkOrderIdFromFilePath(filePath)
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    var errorMessage = "Failed to retrieve file URL from XML. Please ensure the URL is correct.";
                    _logger.LogError(ex, $"Failed to retrieve SharePoint URL link from XML: {filePath}");

                    // Add client-friendly message to error list
                    errorMessages.Add(errorMessage);

                    var errorFilePath = Path.Combine(Path.GetDirectoryName(filePath), $"Error_ExtractURL_{Path.GetFileNameWithoutExtension(filePath)}.pdf");
                    SaveErrorPdf(errorFilePath, new List<string> { errorMessage });

                    // Log the error to the database and save error details
                    var workOrderId = ExtractWorkOrderIdFromFilePath(filePath);
                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                        await dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = $"Error extracting URL from file: {filePath}. {errorMessage}",
                            JobId = workOrderId
                        });
                        await SaveErrorDetails(workOrderId, new List<string> { errorMessage }, cancellationToken);
                    }
                }
            }

            if (errorMessages.Any())
            {
                try
                {
                    var errorFilePath = Path.Combine(Path.GetDirectoryName(workOrderFilePaths.First()), "Error_ExtractUrls.pdf");
                    SaveErrorPdf(errorFilePath, errorMessages);

                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                        var workOrderId = ExtractWorkOrderIdFromFilePath(workOrderFilePaths.First());
                        await dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = "Multiple errors occurred while extracting URLs from work order files.",
                            JobId = workOrderId
                        });
                        await SaveErrorDetails(workOrderId, errorMessages, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    var finalErrorMessage = "An error occurred while saving error details. Please contact support.";
                    _logger.LogError(ex, finalErrorMessage);

                    // Log critical error if error details cannot be saved
                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                        var workOrderId = ExtractWorkOrderIdFromFilePath(workOrderFilePaths.First());
                        await dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Critical",
                            Message = $"Critical error occurred while saving error details: {ex.Message}",
                            JobId = workOrderId
                        });
                    }
                }
            }

            return urls;
        }


        /// <summary>
        /// Downloads all files from the provided SharePoint URLs to the specified destination path.
        /// </summary>
        /// <param name="fileUrls">An enumerable collection of file URLs to download.</param>
        /// <param name="destinationPath">The path where the downloaded files will be saved.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A list of file paths for the successfully downloaded files.</returns>
        // Thread safe implementation
        private async Task<List<string>> DownloadAllFilesAsync(IEnumerable<string> fileUrls, string destinationPath, string uniqueJobId, CancellationToken cancellationToken)
        {
            var fileUrlList = fileUrls.ToList();
            var downloadedFiles = new string[fileUrlList.Count];
            var errorMessages = new ConcurrentBag<string>();
            var maxDegreeOfParallelism = 5; // Tune based on environment and SharePoint limits
            using var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);

            var downloadTasks = fileUrlList.Select((url, index) => Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var downloadedFilePath = await _sharePointService.DownloadFileAsync(url, destinationPath);
                    downloadedFiles[index] = downloadedFilePath;

                    using var scope = _serviceScopeFactory.CreateScope();
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                    await dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Information",
                        Message = $"Successfully downloaded file from URL: {url}",
                        JobId = uniqueJobId
                    });
                }
                catch (Exception ex)
                {
                    var errorMessage = $"Failed to retrieve SharePoint URL link: {url} - {ex.Message}";
                    _logger.LogError(ex, errorMessage);
                    errorMessages.Add(errorMessage);

                    try
                    {
                        await SaveWorkOrderErrorAsync("SharePointURL", errorMessage);
                        using var scope = _serviceScopeFactory.CreateScope();
                        var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                        await dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = errorMessage,
                            JobId = uniqueJobId
                        });
                    }
                    catch (Exception saveEx)
                    {
                        _logger.LogError(saveEx, $"An error occurred while saving the work order error for URL: {url}");
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }));

            await Task.WhenAll(downloadTasks);

            if (errorMessages.Any())
            {
                try
                {
                    var errorFilePath = Path.Combine(destinationPath, "Error_Downloads.pdf");
                    SaveErrorPdf(errorFilePath, errorMessages.ToList());
                }
                catch (Exception pdfEx)
                {
                    _logger.LogError(pdfEx, "An error occurred while saving the error PDF file.");
                }

                try
                {
                    await SaveErrorDetails(uniqueJobId, errorMessages.ToList(), cancellationToken);
                }
                catch (Exception saveEx)
                {
                    _logger.LogError(saveEx, "An error occurred while saving the error details in the database.");
                    using var scope = _serviceScopeFactory.CreateScope();
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                    await dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Critical",
                        Message = "Critical error occurred while saving error details to the database.",
                        JobId = uniqueJobId
                    });
                }
            }

            return downloadedFiles.Where(f => f != null).ToList(); // Remove failed/null entries
        }

        static bool HasWindowsPath(string path)
        {
            string root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && root.Length > 1 && root[1] == ':'; // Checks "C:" format
        }


        /// <summary>
        /// Copies and converts files from the source folder to the target folder, converting them to PDF if necessary.
        /// </summary>
        /// <param name="sourceFolder">The source folder where the original files are located.</param>
        /// <param name="targetFolder">The target folder where the copied/converted files will be saved.</param>
        /// <param name="workOrderFilePaths">A list of file paths to process.</param>
        /// <param name="uniqueJobId">The unique job identifier associated with the work orders.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A tuple containing a dictionary of file mappings and a list of error file paths.</returns>

        // Thread safe implementation
        private async Task<(Dictionary<string, string> fileMappings, List<string> errorFilePaths)> CopyAndConvertFiles(
        string sourceFolder, string targetFolder, List<string> workOrderFilePaths, string uniqueJobId, CancellationToken cancellationToken)
        {
            var fileMappings = new ConcurrentDictionary<string, string>();
            var errorFilePaths = new ConcurrentBag<string>();
            var errorMessages = new ConcurrentDictionary<string, ConcurrentBag<string>>();

            var tasks = workOrderFilePaths.Select(async originalFilePath =>
            {
                await _semaphore.WaitAsync(cancellationToken);
                try
                {
                    using var innerScope = _serviceScopeFactory.CreateScope();
                    var innerDataAccess = innerScope.ServiceProvider.GetRequiredService<IDataAccess>();

                    string filePath, fileName;

                    if (!HasWindowsPath(originalFilePath))
                    {
                        var localFolderSettings = await _dataAccess.GetSapFolderSettingsAsync();
                        var localFolder = localFolderSettings.FirstOrDefault();

                        string localFolderPath = localFolder.Path;
                        string localFolderClient = localFolder.Name;
                        string localFolderIN = "In";
                        string localFileName = Path.GetFileName(originalFilePath);

                        filePath = Path.Combine(localFolderPath, localFolderClient, localFolderIN, localFileName);
                        fileName = Path.GetFileName(filePath);
                    }
                    else
                    {
                        filePath = originalFilePath;
                        fileName = Path.GetFileName(filePath);
                    }

                    _logger.LogInformation($"Starting processing for file: {filePath}");

                    if (string.IsNullOrEmpty(targetFolder))
                        throw new ArgumentNullException(nameof(targetFolder), "Target folder path is null or empty.");
                    if (string.IsNullOrEmpty(fileName))
                        throw new ArgumentNullException(nameof(fileName), "File name is null or empty.");

                    var targetPath = Path.Combine(targetFolder, fileName);

                    fileMappings[originalFilePath] = targetPath;

                    if (File.Exists(filePath))
                    {
                        const int maxRetries = 3;
                        int retryCount = 0;

                        while (IsFileLocked(filePath) && retryCount < maxRetries)
                        {
                            _logger.LogWarning($"File {filePath} is in use, waiting to retry... (Attempt {retryCount + 1} of {maxRetries})");
                            await Task.Delay(3000, cancellationToken);
                            retryCount++;
                        }

                        if (!File.Exists(targetPath))
                        {
                            File.Copy(filePath, targetPath);
                            _logger.LogInformation($"Copied original file to: {targetPath}");

                            await innerDataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Information",
                                Message = $"Copied original file: {filePath} to {targetPath}",
                                JobId = uniqueJobId
                            });
                        }

                        if (Path.GetExtension(filePath).ToLower() != ".pdf")
                        {
                            try
                            {
                                var convertedFilePath = await Task.Run(() => _fileConverter.ConvertToPdf(filePath, cacheFolderPDFRepairPath));
                                await WaitForFileExistsAsync(convertedFilePath, cancellationToken);

                                filePath = convertedFilePath;
                                targetPath = Path.Combine(targetFolder, Path.GetFileName(convertedFilePath));

                                fileMappings[originalFilePath] = targetPath;

                                while (IsFileLocked(filePath) && retryCount < maxRetries)
                                {
                                    _logger.LogWarning($"File {filePath} is in use, waiting to retry... (Attempt {retryCount + 1} of {maxRetries})");
                                    await Task.Delay(3000, cancellationToken);
                                    retryCount++;
                                }

                                if (!File.Exists(targetPath))
                                {
                                    File.Copy(filePath, targetPath);
                                    _logger.LogInformation($"Copied converted file to: {targetPath}");

                                    await innerDataAccess.AddLogEntryAsync(new LogEntry
                                    {
                                        Timestamp = DateTime.UtcNow,
                                        Level = "Information",
                                        Message = $"Converted and copied file to PDF: {filePath} to {targetPath}",
                                        JobId = uniqueJobId
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                var errorMessage = $"Failed to convert to PDF: {filePath} - {ex.Message}";
                                _logger.LogError(ex, errorMessage);

                                var workOrderId = await GetWorkOrderIdByFilePath(filePath, uniqueJobId);
                                errorMessages.AddOrUpdate(workOrderId,
                                    new ConcurrentBag<string> { errorMessage },
                                    (_, existingBag) =>
                                    {
                                        existingBag.Add(errorMessage);
                                        return existingBag;
                                    });

                                await innerDataAccess.AddLogEntryAsync(new LogEntry
                                {
                                    Timestamp = DateTime.UtcNow,
                                    Level = "Error",
                                    Message = errorMessage,
                                    JobId = uniqueJobId
                                });
                            }
                        }
                    }
                    else
                    {
                        var errorMessage = $"File not found: {filePath}";
                        _logger.LogWarning(errorMessage);

                        var workOrderId = await GetWorkOrderIdByFilePath(filePath, uniqueJobId);
                        errorMessages.AddOrUpdate(workOrderId,
                            new ConcurrentBag<string> { errorMessage },
                            (_, existingBag) =>
                            {
                                existingBag.Add(errorMessage);
                                return existingBag;
                            });

                        await innerDataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Warning",
                            Message = errorMessage,
                            JobId = uniqueJobId
                        });
                    }
                }
                catch (Exception ex)
                {
                    var errorMessage = $"Problem processing file: {originalFilePath} - {ex.Message}";
                    _logger.LogError(ex, errorMessage);

                    using var errorScope = _serviceScopeFactory.CreateScope();
                    var errorDataAccess = errorScope.ServiceProvider.GetRequiredService<IDataAccess>();
                    var workOrderId = await GetWorkOrderIdByFilePath(originalFilePath, uniqueJobId);

                    errorMessages.AddOrUpdate(workOrderId,
                        new ConcurrentBag<string> { errorMessage },
                        (_, existingBag) =>
                        {
                            existingBag.Add(errorMessage);
                            return existingBag;
                        });

                    await errorDataAccess.UpdateWorkOrderStatusAsync(uniqueJobId, workOrderId, "Error", errorMessage);
                }
                finally
                {
                    _semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            // Preserve xml shop paper sequence and order
            var orderedFileMappings = workOrderFilePaths
                 .Where(fileMappings.ContainsKey)
                 .Distinct()
                 .ToDictionary(key => key, key => fileMappings[key]);

            return (orderedFileMappings, errorFilePaths.ToList());
        }

        /// <summary>
        /// Waits for a specified file to become available, checking periodically if it exists and is not locked.
        /// Retries up to a defined maximum number of attempts, with exponential backoff between retries.
        /// Logs events to both the application logger and the database for monitoring and error tracking.
        /// </summary>
        /// <param name="filePath">The path of the file to check for existence and availability.</param>
        /// <param name="cancellationToken">Token for canceling the operation if required.</param>
        /// <returns>Returns true if the file becomes available within the retry limit, otherwise returns false.</returns>
        private async Task<bool> WaitForFileExistsAsync(string filePath, CancellationToken cancellationToken)
        {
            int retryCount = 0;
            const int maxRetries = 3;  // Maximum number of retries
            var delayMilliseconds = 2000;  // Initial delay between retries
            const int maxDelayMilliseconds = 16000;  // Maximum delay for exponential backoff

            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();

                while (retryCount < maxRetries)
                {
                    try
                    {
                        if (File.Exists(filePath) && !IsFileLocked(filePath))
                        {
                            _logger.LogInformation($"File is available: {filePath}");

                            // Log success to the database
                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Information",
                                Message = $"File became available: {filePath}"
                            });

                            return true;  // File exists and is not locked
                        }

                        retryCount++;

                        // Log locked status or waiting status for each retry
                        if (IsFileLocked(filePath))
                        {
                            _logger.LogInformation($"File is locked (Attempt {retryCount}/{maxRetries}), waiting to retry: {filePath}");
                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Warning",
                                Message = $"File is locked, retrying (Attempt {retryCount}/{maxRetries}): {filePath}"
                            });
                        }
                        else
                        {
                            _logger.LogInformation($"Waiting for file to be created (Attempt {retryCount}/{maxRetries}): {filePath}");
                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Information",
                                Message = $"Waiting for file creation (Attempt {retryCount}/{maxRetries}): {filePath}"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error while checking file status (Attempt {retryCount}/{maxRetries}): {filePath}");
                        await dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = $"Error while checking file status (Attempt {retryCount}/{maxRetries}): {filePath}",
                            Exception = ex.ToString()
                        });
                    }

                    await Task.Delay(delayMilliseconds, cancellationToken);

                    // Exponential backoff with a maximum cap on the delay
                    delayMilliseconds = Math.Min(delayMilliseconds * 2, maxDelayMilliseconds);
                }

                // After retries are exhausted, log a final error to the database
                var errorMessage = $"File was not created or is still locked after {maxRetries} attempts: {filePath}";
                _logger.LogError(null, errorMessage);

                await dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = errorMessage
                });

                return false;  // File doesn't exist or is still locked
            }
        }


        /// <summary>
        /// Retrieves the work order ID associated with a specific file path and unique job ID.
        /// Logs any errors encountered in both the application log and the database.
        /// </summary>
        /// <param name="filePath">The path of the file for which the work order ID is required.</param>
        /// <param name="uniqueJobId">The unique job ID associated with the file.</param>
        /// <returns>Returns the work order ID if found; otherwise, throws an exception if an error occurs.</returns>
        private async Task<string> GetWorkOrderIdByFilePath(string filePath, string uniqueJobId)
        {
            try
            {
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                    return await dataAccess.GetWorkOrderIdFromFilePathAsync(filePath, uniqueJobId);
                }
            }
            catch (Exception ex)
            {
                // Log the error to the application log
                _logger.LogError(ex, $"An error occurred while retrieving work order ID for file path: {filePath} and unique job ID: {uniqueJobId}");

                // Log the error to the database
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                    await dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Message = $"An error occurred while retrieving work order ID for file path: {filePath} and unique job ID: {uniqueJobId}",
                        Exception = ex.ToString()
                    });
                }

                throw; // Re-throw the exception to be handled by calling code
            }
        }

        private async Task ConcatenateAndPrintFiles(string jobId, string uniqueJobId, string outFolderPath, string cacheFolderPath, string jobXmlFilePath, string jobOutFolderPath, Dictionary<string, string> urlToFilePathMapping, CancellationToken cancellationToken)
        {
            const int maxRetries = 3;
            const int delayMilliseconds = 2000;
            var errorMessages = new List<string>();
            string user = null;
            XDocument jobXmlDocument;

            try
            {
                using (var reader = new StreamReader(jobXmlFilePath, Encoding.UTF8, true))
                {
                    jobXmlDocument = XDocument.Load(reader);
                }

                var workOrders = jobXmlDocument.Descendants("WorkOrder");

                var indexedWorkOrders = workOrders
                    .Select((workOrder, index) => new
                    {
                        WorkOrder = workOrder,
                        WorkOrderId = workOrder.Element("Id")?.Value,
                        Index = index,
                        TaskFactory = new Func<Task>(() =>
                        ProcessWorkOrderAsync(workOrder, cacheFolderPath, jobId, jobOutFolderPath, urlToFilePathMapping, cancellationToken))
                    })
                    .ToList();

             
                // Await each task sequentially, preserving order
                foreach (var entry in indexedWorkOrders.OrderBy(e => e.Index))
                {
                    await entry.TaskFactory();
                }

                var allWorkOrderFiles = indexedWorkOrders
                     .Select(entry =>
                     {
                         var expectedFilePath = Path.Combine(outFolderPath, $"{entry.WorkOrderId}.pdf");
                         return File.Exists(expectedFilePath) ? expectedFilePath : null;
                     })
                     .OrderBy(path => Path.GetFileNameWithoutExtension(path))
                     .ToArray(); // Keep all entries, even if they are null

                var megaFilePath = Path.Combine(outFolderPath, $"{jobId}.pdf");

                if (allWorkOrderFiles.Length > 0)
                {
                    bool success = false;

                    await _semaphore.WaitAsync(cancellationToken);

                    try
                    {
                        // Retry loop with locked-file delete attempts before concatenate
                        for (int attempt = 1; attempt <= maxRetries; attempt++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            try
                            {
                                // Attempt to delete locked file if exists before concatenate
                                if (File.Exists(megaFilePath))
                                {
                                    try
                                    {
                                        using (var fs = File.Open(megaFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                                        File.Delete(megaFilePath);
                                    }
                                    catch (IOException ex)
                                    {
                                        _logger.LogWarning($"Attempt {attempt}: Mega file {megaFilePath} is locked, retrying after delay.");
    
                                        if (attempt == maxRetries)
                                        {
                                            var msg = $"Cannot overwrite mega file {megaFilePath}. It is locked after {maxRetries} attempts.";
                                            _logger.LogError(ex, msg);
                                            throw new IOException(msg, ex);
                                        }
 
                                        await Task.Delay(delayMilliseconds, cancellationToken);
     
                                        continue; // Retry loop
                                    }
                                }

                                // Now call the existing concatenate method
                                await ConcatenatePdfFiles(allWorkOrderFiles, megaFilePath, jobId, null, cancellationToken);

                                _logger.LogInformation($"Concatenated all work order files into mega file {megaFilePath}");

                                success = true;

                                break;
                            }
                            catch (IOException ioEx)
                            {
                                var errorMessage = $"Attempt {attempt} failed to concatenate PDF files into {megaFilePath}: {ioEx.Message}";
                                _logger.LogError(ioEx, errorMessage);

                                if (attempt == maxRetries)
                                {
                                    var finalErrorMessage = $"All {maxRetries} attempts failed to concatenate PDF files into {megaFilePath}";
                                    _logger.LogError(null, finalErrorMessage);

                                    using (var scope = _serviceScopeFactory.CreateScope())
                                    {
                                        var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                                        await dataAccess.AddLogEntryAsync(new LogEntry
                                        {
                                            Timestamp = DateTime.UtcNow,
                                            Level = "Error",
                                            Message = finalErrorMessage,
                                            Exception = ioEx.ToString()
                                        });
                                    }

                                    throw new Exception(finalErrorMessage, ioEx);
                                }

                                _logger.LogInformation($"Waiting {delayMilliseconds} milliseconds before retrying...");
                                await Task.Delay(delayMilliseconds, cancellationToken);
                            }
                        }
                    }
                    finally
                    {
                        _semaphore.Release();
                    }

                    if (!success)
                    {
                        return;
                    }

                    var megaFileDestinationPath = Path.Combine(jobOutFolderPath, Path.GetFileName(megaFilePath));

                    if (!File.Exists(megaFileDestinationPath))
                    {
                        var errorMessage = $"Mega file {megaFileDestinationPath} does not exist. Cannot proceed with '007' status call.";
                        _logger.LogError(null, errorMessage);

                        using (var scope = _serviceScopeFactory.CreateScope())
                        {
                            var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Error",
                                Message = errorMessage
                            });
                        }

                        return;
                    }

                    var sequence = "0001";
                    var objtyp = "OBJ_TYP";
                    var filePath = megaFileDestinationPath;

                    _logger.LogInformation($"Preparing to report status 007 for Job ID {jobId} using file {filePath}");
                    await _statusReporter.ReportStatusAsync(int.Parse(jobId), null, "007", "Work Order complete", sequence, objtyp, filePath, cancellationToken);
                    _logger.LogInformation($"Reported status 007 for Job ID {jobId}");

                    // Call TrackWorkOrderRunAsync at the end of the run, passing in the user and total work order count
                    var job = await _dataAccess.GetJobByIdAsync(jobId);
                    user = job?.User ?? "UnknownUser";
                    await TrackWorkOrderRunAsync(user, workOrders.Count(), jobId);
                }
                else
                {
                    _logger.LogWarning($"No work order files found to concatenate into mega file for job {jobId}");
                }
            }
            catch (Exception ex)
            {
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                    await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

                    await dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Message = $"Error concatenating and printing files for job {jobId}",
                        Exception = ex.ToString()
                    });
                }

                _logger.LogError(ex, $"Error concatenating and printing files for job {jobId}");
                throw;
            }
        }


        // Helper method to check if the file is locked by another process
        private bool IsFileLocked(string filePath)
        {
            try
            {
                using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    // If we can open the file for reading with no sharing, it means it's not locked
                    return false;
                }
            }
            catch (IOException ex)
            {
                // The file is locked, log the event to both console and database
                _logger.LogWarning($"File is locked: {filePath}");

                // Logging to database about the locked file
                var logEntry = new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Warning",
                    Message = $"File is locked and cannot be accessed: {filePath}",
                    Exception = ex.ToString(),
                    ServiceName = "FileService",
                    Properties = $"FilePath: {filePath}"
                };

                Task.Run(async () =>
                {
                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                        await dataAccess.AddLogEntryAsync(logEntry);
                    }
                }).Wait();

                return true;
            }
        }

        private async Task<bool> WaitForUnlockAsync(string filePath, int maxRetries, int delayMilliseconds, CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        return true; // Unlocked
                    }
                }
                catch (IOException)
                {
                    _logger.LogWarning($"File is locked (attempt {attempt}/{maxRetries}): {filePath}");
                    if (attempt < maxRetries)
                        await Task.Delay(delayMilliseconds, cancellationToken);
                }
            }

            return false; // Still locked after retries
        }


        /// <summary>
        /// Processes an individual work order by handling its file parts, concatenating them into a single PDF,
        /// and optionally sending the result to a printer or reporting its status to SAP.
        /// 
        /// This method performs the following steps:
        /// 1. Retrieves the main file and any additional work order parts for concatenation.
        /// 2. Creates a set of files to be combined into a single PDF.
        /// 3. If the concatenated PDF is successfully created:
        ///    - The method checks configuration settings to determine whether to send the PDF to a printer.
        ///    - If printing is enabled, sends the file to the specified printer and logs any errors if printing fails.
        /// 4. Handles error cases:
        ///    - If a printer configuration issue or processing error occurs, logs the error and generates an error PDF.
        ///    - Updates the work order status to "Error" in the database and sends an error report to SAP.
        /// 5. If the operation completes successfully, updates the work order and job status to "Processed"
        ///    and sends a success status to SAP.
        /// 
        /// This method relies on dependency-injected services for database access, logging, and SAP communication
        /// and uses a scoped data access object to ensure that each database operation is executed within a controlled scope.
        /// </summary>
        private async Task ProcessWorkOrderAsync(XElement workOrder, string cacheFolderPath, string jobId, string jobOutFolderPath, Dictionary<string, string> urlToFilePathMapping, CancellationToken cancellationToken)
        {
            var workOrderId = workOrder.Element("Id").Value;
            var concatenatedFilePath = Path.Combine(cacheFolderPath, $"{workOrderId}.pdf");
            var filesToConcatenate = new HashSet<string>();
            var filesSet = new HashSet<string>();
            var finalStatus = "Processed";
            string uniqueJobId = null;
            string user = null;
            var sequence = "0001";
            var objtyp = "BUS2007";

            // --- File Locking (Essential for concurrent file access) ---
            string lockKey = concatenatedFilePath.ToLowerInvariant();
            var lockEntry = _fileLocks.GetOrAdd(lockKey, _ => new FileLockEntry());
            lockEntry.LastUsed = DateTime.UtcNow;

            await lockEntry.Semaphore.WaitAsync(cancellationToken);

            try
            {
                var mainFilePath = workOrder.Element("File").Value;
                var mainFileName = Path.GetFileName(mainFilePath);
                var mainCachedFilePath = Path.Combine(cacheFolderPath, mainFileName);

                if (File.Exists(mainCachedFilePath))
                {
                    filesToConcatenate.Add(mainCachedFilePath);
                }
                else
                {
                    _logger.LogWarning($"File not found in cache folder: {mainCachedFilePath}");
                    await _dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Warning",
                        Message = $"File not found in cache folder: {mainCachedFilePath}",
                        JobId = jobId,
                        WorkOrderId = workOrderId
                    });
                }

                var workOrderParts = workOrder.Descendants("WorkOrderPart");
            
                var orderedPartTasks = workOrderParts
                    .Select((part, index) => new
                    {
                        Task = ProcessWorkOrderPartAsync(part, filesToConcatenate, filesSet, cacheFolderPath, workOrderId, jobId, urlToFilePathMapping, cancellationToken),
                        OriginalIndex = index
                    })
                    .ToList();
           
                // Await in original XML order
                foreach (var partTask in orderedPartTasks.OrderBy(t => t.OriginalIndex))
                {
                    await partTask.Task;
                }

                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();

                    // Fetch the Job and its UniqueJobId
                    var job = await dataAccess.GetJobByIdAsync(jobId);
                    if (job == null)
                    {
                        throw new Exception($"Job with ID {jobId} not found.");
                    }
                    uniqueJobId = job.UniqueJobId;
                    user = job.User;

                    _logger.LogInformation($"Using UniqueJobId: {uniqueJobId} for WorkOrderId: {workOrderId}");

                    await _dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Information",
                        Message = $"Using UniqueJobId: {uniqueJobId} for WorkOrderId: {workOrderId}",
                        JobId = jobId,
                        WorkOrderId = workOrderId
                    });

                    if (filesToConcatenate.Count > 0)
                    {
                        try
                        {

                            var sortedFilesArray = SortFiles(filesToConcatenate);

                            await ConcatenatePdfFiles(sortedFilesArray, concatenatedFilePath, jobId, workOrderId, cancellationToken);

                            _logger.LogInformation($"Concatenated files for work order {workOrderId} into {concatenatedFilePath}");
                            await _dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Information",
                                Message = $"Concatenated files for work order {workOrderId} into {concatenatedFilePath}",
                                JobId = jobId,
                                WorkOrderId = workOrderId
                            });

                            using (var innerScope = _serviceScopeFactory.CreateScope())
                            {
                                var innerDataAccess = innerScope.ServiceProvider.GetRequiredService<IDataAccess>();

                                var sendToPrinterSetting = await innerDataAccess.GetConfigurationSettingAsync("AppSettings", "SendToPrinter");
                                var sendToPrinter = sendToPrinterSetting != null && bool.Parse(sendToPrinterSetting.Value);

                                var printLocation = job.Print;
                                var printerName = job.Printer ?? "ProdPrinter";

                                var printerSetting = await dataAccess.GetPrinterSettingByShortNameAsync(printerName);

                                var defaultPrinterSetting = await innerDataAccess.GetConfigurationSettingAsync("AppSettings", "DefaultPrinterName");

                                sequence = workOrderParts.FirstOrDefault()?.Element("SeqId")?.Value ?? "0001";
                                objtyp = workOrder.Element("Objtyp")?.Value ?? "BUS2007";
                                var filePath = workOrder.Element("File").Value;

                                string statusCodeSuccess = "037";
                                string statusCodeFailure = "009";
                                string statusMessageSuccess = "Work Order complete";

                                // Move the file to the Out folder before making the RFC call for status "037"
                                var outFilePath = Path.Combine(jobOutFolderPath, Path.GetFileName(concatenatedFilePath));
                                if (!File.Exists(outFilePath))
                                {
                                    File.Move(concatenatedFilePath, outFilePath); // Move file from cache to Out folder
                                    _logger.LogInformation($"Moved work order file {workOrderId} to {outFilePath}");

                                    await _dataAccess.AddLogEntryAsync(new LogEntry
                                    {
                                        Timestamp = DateTime.UtcNow,
                                        Level = "Information",
                                        Message = $"Moved work order file {workOrderId} to {outFilePath}",
                                        JobId = jobId,
                                        WorkOrderId = workOrderId
                                    });
                                }
                                else
                                {
                                    _logger.LogWarning($"File {outFilePath} already exists, skipping move.");

                                    await _dataAccess.AddLogEntryAsync(new LogEntry
                                    {
                                        Timestamp = DateTime.UtcNow,
                                        Level = "Warning",
                                        Message = $"File {outFilePath} already exists, skipping move.",
                                        JobId = jobId,
                                        WorkOrderId = workOrderId
                                    });
                                }

                                // Use the updated path from the Out folder for the RFC call
                                concatenatedFilePath = outFilePath;

                                if (printerSetting == null && printLocation == "N")
                                {
                                    hasError = true;
                                    string errorMessage = $"Printer with name {printerName} not found. File {concatenatedFilePath} was not sent to the printer.";
                                    var errorFilePath = Path.Combine(cacheFolderPath, $"Error_{workOrderId}.pdf");
                                    var errorMessages = new List<string> { errorMessage };
                                    AddErrorPdfMessages(errorFilePath, errorMessages);
                                    await SaveErrorDetails(workOrderId, errorMessages, cancellationToken);
                                    await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                                    await dataAccess.AddLogEntryAsync(new LogEntry { Timestamp = DateTime.UtcNow, Level = "Error", Message = $"Updated job status to 'Error' due to printer error for JobId: {jobId}.", JobId = jobId, WorkOrderId = workOrderId });
                                }
                                else if (sendToPrinter)
                                {
                                    try
                                    {
                                        await _printerService.PrintFileAsync(printLocation == "N" ? printerSetting.ShortName : printerName, concatenatedFilePath, jobId, printLocation);
                                    }
                                    catch (Exception ex)
                                    {
                                        hasError = true;
                                        string errorMessage = $"Error sending to printer for work order {workOrderId}: {ex.Message}";
                                        //await HandleProcessingErrorAsync(workOrderId, errorMessage, cacheFolderPath, jobId, uniqueJobId, sequence, objtyp, concatenatedFilePath, dataAccess, cancellationToken);
                                        throw;
                                    }
                                }
                                else
                                {
                                    hasError = true;
                                    string errorMessage = "Printing is currently disabled. Please enable printing in the system configuration.";
                                    var errorFilePath = Path.Combine(cacheFolderPath, $"Error_{workOrderId}.pdf");
                                    var errorMessages = new List<string> { errorMessage };
                                    AddErrorPdfMessages(errorFilePath, errorMessages);
                                    await SaveErrorDetails(workOrderId, errorMessages, cancellationToken);
                                    await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                                    await dataAccess.AddLogEntryAsync(new LogEntry { Timestamp = DateTime.UtcNow, Level = "Error", Message = $"Updated job status to 'Error' because printing is disabled for JobId: {jobId}.", JobId = jobId, WorkOrderId = workOrderId });
                                }

                                // Check for error PDF before setting final status
                                if (finalStatus != "Error" && !File.Exists(Path.Combine(cacheFolderPath, $"Error_{workOrderId}.pdf")))
                                {
                                    // Update the work order status to Processed
                                    await innerDataAccess.UpdateWorkOrderStatusAsync(uniqueJobId, workOrderId, "Processed", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                                    await _dataAccess.AddLogEntryAsync(new LogEntry
                                    {
                                        Timestamp = DateTime.UtcNow,
                                        Level = "Information",
                                        Message = $"WorkOrder ID {workOrderId} marked as 'Processed' successfully.",
                                        JobId = jobId,
                                        WorkOrderId = workOrderId
                                    });

                                    // Update job status to Processed
                                    await _dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Processed", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                                    await _dataAccess.AddLogEntryAsync(new LogEntry
                                    {
                                        Timestamp = DateTime.UtcNow,
                                        Level = "Information",
                                        Message = $"Job ID {jobId} marked as 'Processed' successfully.",
                                        JobId = jobId,
                                        WorkOrderId = workOrderId
                                    });

                                    // Make the RFC call to SAP for this specific work order success
                                    await _statusReporter.ReportStatusAsync(int.Parse(jobId), workOrderId, statusCodeSuccess, statusMessageSuccess, sequence, objtyp, concatenatedFilePath, cancellationToken);
                                    _logger.LogInformation($"Reported status 'Processed' for WorkOrder ID {workOrderId}");

                                    // Log the success status report to SAP in the database
                                    await _dataAccess.AddLogEntryAsync(new LogEntry
                                    {
                                        Timestamp = DateTime.UtcNow,
                                        Level = "Information",
                                        Message = $"Reported status 'Processed' to SAP for WorkOrder ID {workOrderId}.",
                                        JobId = jobId,
                                        WorkOrderId = workOrderId
                                    });
                                }

                                else
                                {
                                    finalStatus = "Error";
                                    hasError = true;

                                    sequence = workOrderParts.FirstOrDefault()?.Element("SeqId")?.Value ?? "0001";
                                    objtyp = workOrder.Element("Objtyp")?.Value ?? "BUS2007";

                                    // Initialize error file path and write the error message to the PDF
                                    string errorFilePath = Path.Combine(cacheFolderPath, $"Error_{workOrderId}.pdf");

                                    var errorMessage = $"An error occurred during processing. Please review the Error work order file {errorFilePath}";

                                    // Log error creation in the database
                                    await _dataAccess.AddLogEntryAsync(new LogEntry
                                    {
                                        Timestamp = DateTime.UtcNow,
                                        Level = "Error",
                                        Message = $"WorkOrder ID {workOrderId} encountered an error. Created error PDF at {errorFilePath}.",
                                        JobId = jobId,
                                        WorkOrderId = workOrderId
                                    });

                                    // Send the error status to SAP
                                    await _statusReporter.ReportStatusAsync(int.Parse(jobId), workOrderId, "009", errorMessage, sequence, objtyp, errorFilePath, cancellationToken);
                                    await _dataAccess.AddLogEntryAsync(new LogEntry
                                    {
                                        Timestamp = DateTime.UtcNow,
                                        Level = "Information",
                                        Message = $"Reported error status to SAP for WorkOrder ID {workOrderId} with message: {errorMessage}.",
                                        JobId = jobId,
                                        WorkOrderId = workOrderId
                                    });

                                    _logger.LogInformation($"Reported error status to SAP for WorkOrder ID {workOrderId} due to finalStatus being 'Error'.");
                                }

                            }
                        }
                        catch (Exception ex)
                        {
                            hasError = true;
                            _logger.LogError(ex, $"Error concatenating files for work order {workOrderId}");

                            // Log error to the database
                            await _dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Error",
                                Message = $"Error concatenating files for work order {workOrderId}: {ex.Message}",
                                JobId = jobId,
                                WorkOrderId = workOrderId
                            });

                            var errorMessages = new List<string> { ex.Message };
                            await SaveErrorDetails(workOrderId, errorMessages, cancellationToken);

                            var errorFilePath = Path.Combine(cacheFolderPath, $"Error_{workOrderId}.pdf");
                            AddErrorPdfMessages(errorFilePath, errorMessages);

                            if (File.Exists(errorFilePath))
                            {
                                filesToConcatenate.Add(errorFilePath);

                                await ConcatenatePdfFiles(filesToConcatenate.ToArray(), concatenatedFilePath, jobId, workOrderId, cancellationToken);

                                // Log the addition of error files to concatenation in the database
                                await _dataAccess.AddLogEntryAsync(new LogEntry
                                {
                                    Timestamp = DateTime.UtcNow,
                                    Level = "Information",
                                    Message = $"Added error file {errorFilePath} for concatenation for work order {workOrderId}.",
                                    JobId = jobId,
                                    WorkOrderId = workOrderId
                                });
                            }

                            await _dataAccess.UpdateWorkOrderStatusAsync(uniqueJobId, workOrderId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                            await _dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Error",
                                Message = $"Updated work order status to 'Error' for WorkOrderId: {workOrderId}.",
                                JobId = jobId,
                                WorkOrderId = workOrderId
                            });

                            // Update job status to Error
                            await _dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                            await _dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Error",
                                Message = $"Updated job status to 'Error' for JobId: {jobId} with UniqueJobId: {uniqueJobId}.",
                                JobId = jobId,
                                WorkOrderId = workOrderId
                            });

                            // Send the error status to SAP
                            await _statusReporter.ReportStatusAsync(int.Parse(jobId), workOrderId, "009", ex.Message, sequence, objtyp, errorFilePath, cancellationToken);
                            await _dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Information",
                                Message = $"Reported error status to SAP for WorkOrderId: {workOrderId} with error message: {ex.Message}.",
                                JobId = jobId,
                                WorkOrderId = workOrderId
                            });

                            throw;
                        }

                    }
                    else
                    {
                        hasError = true;
                        var errorMessage = $"No files found to concatenate for work order {workOrderId}";
                        _logger.LogWarning(errorMessage);

                        // Log warning to the database
                        await _dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Warning",
                            Message = errorMessage,
                            JobId = jobId,
                            WorkOrderId = workOrderId
                        });

                        var errorFilePath = Path.Combine(cacheFolderPath, $"Error_{workOrderId}.pdf");
                        var errorMessages = new List<string> { errorMessage };
                        AddErrorPdfMessages(errorFilePath, errorMessages);

                        filesToConcatenate.Add(errorFilePath);

                        // Save error details to the database
                        await SaveErrorDetails(workOrderId, errorMessages, cancellationToken);

                        // Attempt to concatenate error PDF files
                        await ConcatenatePdfFiles(filesToConcatenate.ToArray(), concatenatedFilePath, jobId, workOrderId, cancellationToken);

                        // Update the work order status to "Error" in the database
                        await _dataAccess.UpdateWorkOrderStatusAsync(uniqueJobId, workOrderId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                        await _dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = $"Updated work order status to 'Error' for WorkOrderId: {workOrderId}",
                            JobId = jobId,
                            WorkOrderId = workOrderId
                        });

                        // Update the job status to "Error" in the database
                        await _dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                        await _dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = $"Updated job status to 'Error' for JobId: {jobId} with UniqueJobId: {uniqueJobId}",
                            JobId = jobId,
                            WorkOrderId = workOrderId
                        });

                        // Send the error status to SAP
                        await _statusReporter.ReportStatusAsync(int.Parse(jobId), workOrderId, "009", errorMessage, sequence, objtyp, errorFilePath, cancellationToken);
                        await _dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Information",
                            Message = $"Reported error status to SAP for WorkOrderId: {workOrderId} with error message: {errorMessage}",
                            JobId = jobId,
                            WorkOrderId = workOrderId
                        });
                    }

                }
            }
            catch (Exception ex)
            {
                hasError = true;
                var errorMessages = new List<string> { ex.Message };
                await SaveErrorDetails(workOrderId, errorMessages, cancellationToken);

                await _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = $"Failed to process work order {workOrderId}: {ex.Message}",
                    JobId = jobId,
                    WorkOrderId = workOrderId
                });

                var errorFilePath = Path.Combine(cacheFolderPath, $"Error_{workOrderId}.pdf");
                AddErrorPdfMessages(errorFilePath, errorMessages);

                await _dataAccess.UpdateWorkOrderStatusAsync(uniqueJobId, workOrderId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

                // Update job status to Error
                await _dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

                // Send the error status to SAP
                await _statusReporter.ReportStatusAsync(int.Parse(jobId), workOrderId, "009", ex.Message, sequence, objtyp, errorFilePath, cancellationToken);

                throw;
            }
            finally
            {
                lockEntry.Semaphore.Release();
                lockEntry.LastUsed = DateTime.UtcNow;
            }
        }

        private async Task TrackWorkOrderRunAsync(string user, int workOrderCount, string jobId)
        {
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();

                // Create a new record for the entire run, with the aggregated work order count
                var newUserWorkOrderStats = new UserWorkOrderStats
                {
                    UserName = user,
                    WorkOrderCount = workOrderCount,  // Aggregated count for the entire run
                    LastUpdated = DateTime.UtcNow
                };

                await dataAccess.AddUserWorkOrderStatsAsync(newUserWorkOrderStats);
            }
        }

        // --- Helper methods to reduce duplication and improve readability ---

        private async Task LogInfoAsync(string message, string jobId, string workOrderId, IDataAccess dataAccess)
        {
            _logger.LogInformation(message);
            await dataAccess.AddLogEntryAsync(new LogEntry { Timestamp = DateTime.UtcNow, Level = "Information", Message = message, JobId = jobId, WorkOrderId = workOrderId });
        }

        private async Task LogWarningAsync(string message, string jobId, string workOrderId, IDataAccess dataAccess)
        {
            _logger.LogWarning(message);
            await dataAccess.AddLogEntryAsync(new LogEntry { Timestamp = DateTime.UtcNow, Level = "Warning", Message = message, JobId = jobId, WorkOrderId = workOrderId });
        }

        private async Task LogErrorAsync(Exception ex, string message, string jobId, string workOrderId, IDataAccess dataAccess)
        {
            _logger.LogError(ex, message);
            await dataAccess.AddLogEntryAsync(new LogEntry { Timestamp = DateTime.UtcNow, Level = "Error", Message = message, JobId = jobId, WorkOrderId = workOrderId });
        }

        private async Task ProcessWorkOrderPartAsync(
        XElement part,
        //List<string> filesToConcatenate,
        HashSet<string> filesToConcatenate,
        HashSet<string> filesSet,
        string cacheFolderPath,
        string workOrderId,
        string jobId,
        Dictionary<string, string> urlToFilePathMapping,
        CancellationToken cancellationToken)
        {
            var partFilePath = part.Element("FilePath").Value;
            string partCachedFilePath = null;
            string uniqueJobId = null;
            bool partHasError = false;

            string lockKey = partFilePath.ToLowerInvariant();

            var lockEntry = _fileLocks.GetOrAdd(lockKey, _ => new FileLockEntry());
            lockEntry.LastUsed = DateTime.UtcNow;
            await lockEntry.Semaphore.WaitAsync(cancellationToken);

            try
            {
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                    var job = await dataAccess.GetJobByIdAsync(jobId);
                    if (job == null)
                    {
                        var errorMessage = $"Job with ID {jobId} not found.";
                        await dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = errorMessage,
                            JobId = jobId,
                            WorkOrderId = workOrderId
                        });
                        throw new Exception(errorMessage);
                    }

                    uniqueJobId = job.UniqueJobId;
                    _logger.LogInformation($"Using UniqueJobId: {uniqueJobId} for WorkOrderId: {workOrderId}");
                    await dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Information",
                        Message = $"Using UniqueJobId: {uniqueJobId} for WorkOrderId: {workOrderId}",
                        JobId = jobId,
                        WorkOrderId = workOrderId
                    });

                    if (part.Element("Type").Value == "URL")
                    {
                        _logger.LogInformation($"Processing URL part: {partFilePath}");
                        await dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Information",
                            Message = $"Processing URL part: {partFilePath}",
                            JobId = jobId,
                            WorkOrderId = workOrderId
                        });

                        var cleanUrl = CleanUrl(partFilePath);

                        if (urlToFilePathMapping.TryGetValue(partFilePath, out partCachedFilePath))
                        {
                            _logger.LogInformation($"File already downloaded: {partCachedFilePath}");
                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Information",
                                Message = $"File already downloaded: {partCachedFilePath}",
                                JobId = jobId,
                                WorkOrderId = workOrderId
                            });
                        }
                        else
                        {
                            var errorMessage = $"Failed to download the PDF from the provided SharePoint URL: {partFilePath}.\nPlease verify that the link is accessible and the file exists.";
                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Error",
                                Message = errorMessage,
                                JobId = jobId,
                                WorkOrderId = workOrderId
                            });
                            throw new FileNotFoundException(errorMessage);
                        }

                        if (!partCachedFilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation($"Converting downloaded file to PDF: {partCachedFilePath}");

                            partCachedFilePath = await ConvertToPdf(partCachedFilePath, jobId, cancellationToken);

                            _logger.LogInformation($"Converted downloaded file to PDF: {partCachedFilePath}")
                                ;
                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Information",
                                Message = $"Converted downloaded file to PDF: {partCachedFilePath}",
                                JobId = jobId,
                                WorkOrderId = workOrderId
                            });
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"Processing local file part: {partFilePath}");
                        await dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Information",
                            Message = $"Processing local file part: {partFilePath}",
                            JobId = jobId,
                            WorkOrderId = workOrderId
                        });

                        var partFileName = Path.GetFileName(partFilePath);
                        var originalFilePath = Path.Combine(cacheFolderPath, partFileName);

                        if (File.Exists(originalFilePath))
                        {
                            _logger.LogInformation($"Found work order part file: {originalFilePath}");
                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Information",
                                Message = $"Found work order part file: {originalFilePath}",
                                JobId = jobId,
                                WorkOrderId = workOrderId
                            });

                            if (!originalFilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogInformation($"Converting work order part file to PDF: {originalFilePath}");

                                partCachedFilePath = await ConvertToPdf(originalFilePath, jobId, cancellationToken);

                                _logger.LogInformation($"Converted downloaded file to PDF: {originalFilePath}");

                                await dataAccess.AddLogEntryAsync(new LogEntry
                                {
                                    Timestamp = DateTime.UtcNow,
                                    Level = "Information",
                                    Message = $"Converted work order part file to PDF: {partCachedFilePath}",
                                    JobId = jobId,
                                    WorkOrderId = workOrderId
                                });
                                part.Element("FilePath").Value = partCachedFilePath;
                            }
                            else
                            {
                                partCachedFilePath = originalFilePath;
                            }
                        }
                        else
                        {
                            var errorMessage = "File not found: " + originalFilePath;
                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Error",
                                Message = errorMessage,
                                JobId = jobId,
                                WorkOrderId = workOrderId
                            });
                            throw new FileNotFoundException(errorMessage);
                        }
                    }

                    // Add to files list if not already added and file exists
                    if (partCachedFilePath != null && File.Exists(partCachedFilePath))
                    {
                        if (filesSet.Add(partCachedFilePath))
                        {
                            filesToConcatenate.Add(partCachedFilePath);
                        }
                    }
                    else
                    {
                        var errorMessage = $"File {partFilePath} for work order part not found in cache.";
                        _logger.LogError(null, errorMessage);
                        await dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = errorMessage,
                            JobId = jobId,
                            WorkOrderId = workOrderId
                        });
                        partHasError = true;
                        throw new Exception(errorMessage);
                    }

                    await dataAccess.UpdateWorkOrderPartStatusAsync(uniqueJobId, workOrderId, part.Element("Id").Value, "Processed", "", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing work order part: {partFilePath}");

                // Log the error to the database
                await _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = $"Error processing work order part: {partFilePath} - {ex.Message}",
                    JobId = jobId,
                    WorkOrderId = workOrderId
                });

                var errorMessages = new List<string> { ex.Message };
                await SaveErrorDetails(workOrderId, errorMessages, cancellationToken); // Save error details in db

                var errorFilePath = Path.Combine(cacheFolderPath, $"Error_{workOrderId}.pdf");
                AddErrorPdfMessages(errorFilePath, errorMessages); // Write error to PDF

                if (File.Exists(errorFilePath))
                {
                    if (filesSet.Add(errorFilePath))
                    {
                        filesToConcatenate.Add(errorFilePath);
                    }
                }

                // Mark this part as having an error
                partHasError = true;

                // Update the status of the work order part to Error
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                    await dataAccess.UpdateWorkOrderPartStatusAsync(uniqueJobId, workOrderId, part.Element("Id").Value, "Error", ex.Message, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

                    // Log the work order part update to the database
                    await dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Message = $"Updated work order part status to Error for WorkOrderId: {workOrderId}, PartId: {part.Element("Id").Value} due to exception: {ex.Message}",
                        JobId = jobId,
                        WorkOrderId = workOrderId
                    });
                }
            }
            finally
            {
                // Check the status of all parts and update the work order status accordingly
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                    var parts = await dataAccess.GetWorkOrderPartsAsync(uniqueJobId, workOrderId);

                    if (parts.Any(p => p.Status == "Error"))
                    {
                        await dataAccess.UpdateWorkOrderStatusAsync(uniqueJobId, workOrderId, "Error", "One or more parts failed", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

                        // Log the work order status update to Error in the database
                        await dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = $"Updated work order status to Error for WorkOrderId: {workOrderId} due to part failures.",
                            JobId = jobId,
                            WorkOrderId = workOrderId
                        });
                    }
                    else if (!partHasError) // Only update to Processed if there are no errors
                    {
                        await dataAccess.UpdateWorkOrderStatusAsync(uniqueJobId, workOrderId, "Processed", "", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

                        // Log the work order status update to Processed in the database
                        await dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Information",
                            Message = $"Updated work order status to Processed for WorkOrderId: {workOrderId}",
                            JobId = jobId,
                            WorkOrderId = workOrderId
                        });
                    }
                }

                lockEntry.Semaphore.Release();
                lockEntry.LastUsed = DateTime.UtcNow;
            }
        }

        private string GetMappedPath(string originalUrl, Dictionary<string, string> urlMap, string jobId, string workOrderId)
        {
            var variants = new[] { originalUrl, CleanUrl(originalUrl), SanitizeFilePath(CleanUrl(originalUrl)) };
            foreach (var variant in variants)
            {
                if (urlMap.TryGetValue(variant, out var mapped)) return mapped;
            }

            throw new FileNotFoundException($"SharePoint URL not found in mapping: {originalUrl}");
        }
        

        /// </summary>
        /// <param name="filePath">The path to the file to be converted.</param>
        /// <param name="jobId">The job ID associated with the file conversion.</param>
        /// <returns>The path to the converted PDF file.</returns>
        private async Task<string> ConvertToPdf(string filePath, string jobId, CancellationToken cancellationToken)
        {
            var errorMessages = new List<string>();
            string convertedFilePath = null;
            string uniqueJobId = null;

            const int maxRetries = 3;
            const int delayMilliseconds = 2000;

            try
            {
                _logger.LogInformation($"Attempting to convert file to PDF: {filePath}");

                bool fileReady = await WaitForUnlockAsync(filePath, maxRetries, delayMilliseconds, cancellationToken);

                if (File.Exists(filePath))
                {
                    if (!fileReady)
                    {
                        var lockError = $"File is still locked after {maxRetries} attempts: {filePath}";
                        _logger.LogWarning(lockError);

                        await _dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Warning",
                            Message = lockError,
                            JobId = jobId
                        });

                        throw new IOException(lockError);
                    }
                }

                // File is ready, attempt conversion
                convertedFilePath = await Task.Run(() =>
                    _fileConverter.ConvertToPdf(filePath, cacheFolderPDFRepairPath));

                return convertedFilePath;
            }
            catch (Exception ex)
            {
                var errorMessage = $"Failed to convert file to PDF: {filePath} - {ex.Message}";
                _logger.LogError(ex, errorMessage);
                errorMessages.Add(errorMessage);

                await _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = errorMessage,
                    JobId = jobId
                });

                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                    var job = await dataAccess.GetJobByIdAsync(jobId);

                    if (job != null)
                    {
                        uniqueJobId = job.UniqueJobId;
                        await SaveErrorDetails(jobId, errorMessages, cancellationToken);
                        await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                    }
                    else
                    {
                        string warn = $"Job with ID {jobId} not found. Unable to update status to 'Error'.";
                        _logger.LogWarning(warn);
                        await _dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Warning",
                            Message = warn,
                            JobId = jobId
                        });
                    }
                }

                throw new Exception($"Failed to convert file to PDF: {filePath}", ex);
            }
        }

        /// <summary>
        /// Saves a list of error messages to a PDF file at the specified file path.
        /// </summary>
        /// <param name="filePath">The path where the error PDF will be saved.</param>
        /// <param name="errorMessages">The list of error messages to be included in the PDF.</param>
        private void SaveErrorPdf(string filePath, List<string> errorMessages)
        {
            try
            {
                // Construct the path for the error PDF file
                var errorFilePath = Path.Combine(Path.GetDirectoryName(filePath), $"{Path.GetFileNameWithoutExtension(filePath)}.pdf");

                // Check if the error file is locked before proceeding
                if (File.Exists(errorFilePath) && IsFileLocked(errorFilePath))
                {
                    var lockErrorMessage = $"Error PDF file is currently locked and cannot be updated: {errorFilePath}";
                    _logger.LogWarning(lockErrorMessage);

                    // Log the lock error to the database
                    _dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Warning",
                        Message = lockErrorMessage
                    }).Wait();

                    throw new IOException(lockErrorMessage);
                }

                // Add error messages to the PDF file
                AddErrorPdfMessages(errorFilePath, errorMessages);

                _logger.LogInformation($"Error PDF saved successfully: {errorFilePath}");

                // Log the success of saving the PDF to the database
                _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Information",
                    Message = $"Error PDF saved successfully: {errorFilePath}"
                }).Wait();
            }
            catch (Exception ex)
            {
                // Log any exceptions that occur during the process
                _logger.LogError(ex, $"Failed to save error PDF at {filePath}");

                // Log the exception to the database
                _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = $"Failed to save error PDF at {filePath}: {ex.Message}"
                }).Wait();

                // Re-throw the exception
                throw;
            }
        }
  
        private void AddPageNumbers(PdfSharpCore.Pdf.PdfDocument document, string jobId, string workorderId)
        {
          
            // Determine if this is a job-level or workorder-level concatenation for the footer text
            string footerPrefix = string.IsNullOrEmpty(workorderId) ? $"Job: {jobId}" : $"WO: {workorderId}";

            for (int i = 0; i < document.PageCount; i++)
            {
                PdfPage page = document.Pages[i];
      
                // ... (inside the loop for each page)
                using (XGraphics gfx = XGraphics.FromPdfPage(page))
                {
                    string pageNumberText = $"Page {i + 1} of {document.PageCount}";
                    XFont font = new XFont("Verdana", PageNumberFontSize, XFontStyle.Regular);
                    XBrush brush = XBrushes.Black; // Color of the page number text

                    double margin = 20;
                    double textWidth = gfx.MeasureString(pageNumberText, font).Width;
                    double textHeight = font.Height;

                    XRect rect = new XRect(
                        page.Width - textWidth - margin,  // X-position (right-aligned)
                        page.Height - textHeight - margin, // Y-position (bottom-aligned)
                        textWidth,                        // Width of the text box
                        textHeight                        // Height of the text box
                    );

                    // Correct usage of XStringFormats (plural) and the combined flag
                    gfx.DrawString(pageNumberText, font, brush, rect, XStringFormats.BottomRight);
                }
            }
        }

        private async Task ConcatenatePdfFiles(string[] sourceFiles, string outputFilePath, string jobId, string workorderId, CancellationToken cancellationToken)
        {
            var errorMessages = new List<string>();
            string uniqueJobId = null;
            string user = null;

            var lockEntry = _fileLocks.GetOrAdd(outputFilePath, _ => new FileLockEntry());
            await lockEntry.Semaphore.WaitAsync();

            try
            {
                using var innerScope = _serviceScopeFactory.CreateScope();
                var innerDataAccess = innerScope.ServiceProvider.GetRequiredService<IDataAccess>();

                string currentTimeUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                    var job = await dataAccess.GetJobByIdAsync(jobId);
                    if (job == null)
                    {
                        throw new Exception($"Job with ID {jobId} not found.");
                    }
                    uniqueJobId = job.UniqueJobId;
                    user = job.User;
                }

                try
                {
                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                        var job = await dataAccess.GetJobByIdAsync(jobId);
                        if (job == null)
                        {
                            var errorMessage = $"Job with ID {jobId} not found.";
                            _logger.LogError(null, errorMessage);
                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Error",
                                Message = errorMessage,
                                JobId = jobId
                            });
                            throw new Exception(errorMessage);
                        }

                        uniqueJobId = job.UniqueJobId;

                        // Ensure output file is not locked - retry loop
                        if (File.Exists(outputFilePath))
                        {
                            const int maxRetries = 5;
                            const int delayMs = 500;
                            bool deleted = false;

                            for (int attempt = 0; attempt < maxRetries; attempt++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                try
                                {
                                    using (var fs = File.Open(outputFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                                    File.Delete(outputFilePath);
                                    deleted = true;
                                    break;
                                }
                                catch (IOException ex)
                                {
                                    _logger.LogWarning($"Attempt {attempt + 1}: Output file {outputFilePath} is locked, retrying in {delayMs} ms.");
                                    if (attempt == maxRetries - 1)
                                    {
                                        var msg = $"Cannot overwrite {outputFilePath}. It is locked by another process after {maxRetries} attempts.";
                                        _logger.LogError(ex, msg);
                                        throw new IOException(msg, ex);
                                    }
                                    await Task.Delay(delayMs, cancellationToken);
                                }
                            }

                            if (!deleted)
                            {
                                throw new IOException($"Failed to delete locked output file {outputFilePath}");
                            }
                        }

                        using (var outputDocument = new PdfDocument())
                        {
                            bool blankPageInserted = false;
                            int fileIndex = 0;

                            foreach (var file in sourceFiles)
                            {
                                string fileToUse = file;
                                PdfDocument inputDocument = null;

                                if (File.Exists(fileToUse))
                                {
                                    try
                                    {
                                        inputDocument = PdfReader.Open(fileToUse, PdfDocumentOpenMode.Import);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning($"Error reading file: {file}. Attempting repair.");

                                        string repairedPath = Path.Combine(cacheFolderPDFRepairPath, Path.GetFileNameWithoutExtension(file) + "_" + Guid.NewGuid() + ".pdf");
                                        bool repaired = await Task.Run(() => _fileConverter.TryRepairPdfWithLibreOffice(fileToUse, repairedPath, cacheFolderPDFRepairPath));

                                        if (repaired && File.Exists(repairedPath))
                                        {
                                            try
                                            {
                                                inputDocument = PdfReader.Open(repairedPath, PdfDocumentOpenMode.Import);
                                                _logger.LogInformation($"Using repaired file: {repairedPath}");
                                            }
                                            catch (Exception repEx)
                                            {
                                                string repError = $"Repair failed to parse PDF {file}: {repEx.Message}";
                                                _logger.LogError(repEx, repError);
                                                errorMessages.Add(repError);
                                                var errorFilePath = Path.Combine(Path.GetDirectoryName(outputFilePath), $"Error_{Path.GetFileNameWithoutExtension(outputFilePath)}.pdf");
                                                AddErrorPdfMessages(errorFilePath, errorMessages);
                                                await dataAccess.UpdateWorkOrderStatusAsync(uniqueJobId, workorderId, "Error", repError, currentTimeUtc);
                                                await _dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", currentTimeUtc, repError);
                                                continue;
                                            }
                                        }
                                        else
                                        {
                                            string errorMessage = $"PDF appears corrupt and could not be repaired: {file}";
                                            _logger.LogError(ex, errorMessage);
                                            errorMessages.Add(errorMessage);

                                            await dataAccess.UpdateWorkOrderStatusAsync(uniqueJobId, workorderId, "Error", errorMessage, currentTimeUtc);
                                            await _dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", currentTimeUtc, errorMessage);
                                            continue;
                                        }
                                    }

                                    if (inputDocument != null)
                                    {
                                        if (ShouldInsertBlankPage(fileIndex, job, outputDocument, hasSharepointAttachment, ref blankPageInserted))
                                        {
                                            AddBlankPage(outputDocument, inputDocument.Pages[0]);
                                            blankPageInserted = true;
                                        }

                                        for (int i = 0; i < inputDocument.PageCount; i++)
                                        {
                                            outputDocument.AddPage(inputDocument.Pages[i]);
                                        }
                                    }

                                    fileIndex++;
                                }
                            }

                            if (outputDocument.PageCount > 0)
                            {
                                bool isMegaFile = false;
                                var pageNumberSetting = await innerDataAccess.GetConfigurationSettingAsync("AppSettings", "PageNumber");

                                // Do a check if the current file being processed is the mega file
                                isMegaFile = Path.GetFileName(outputFilePath).Equals($"{jobId}.pdf", StringComparison.OrdinalIgnoreCase);

                                // Add only page numbers to the mega file
                                if (isMegaFile && pageNumberSetting != null && bool.TryParse(pageNumberSetting.Value, out bool pageNumberEnabled) && pageNumberEnabled)
                                {
                                    AddPageNumbers(outputDocument, jobId, workorderId);
                                }

                                outputDocument.Save(outputFilePath);
                            }
                            else
                            {
                                string errorMessage = "Cannot save a PDF document with no pages.";
                                _logger.LogError(null, errorMessage);
                                throw new InvalidOperationException(errorMessage);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    var errorMessage = $"Error concatenating PDF files into {outputFilePath}: {ex.Message}";
                    _logger.LogError(ex, errorMessage);
                    errorMessages.Add(errorMessage);

                    await _dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Message = errorMessage,
                        JobId = jobId
                    });
                    throw;
                }
            }
            finally
            {
                lockEntry.Semaphore.Release();
            }
        }

        /// <summary>
        /// Sorts a collection of file paths based on a specific custom logic,
        /// prioritizing filenames ending with "Z" and then numerically by the last digits.
        /// </summary>
        /// <param name="filePaths">The collection of file paths to sort.</param>
        /// <returns>A new array of sorted file paths.</returns>
        public static string[] SortFiles(IEnumerable<string> filePaths)
        {
            if (filePaths == null)
            {
                throw new ArgumentNullException(nameof(filePaths));
            }

            var sortedFiles = filePaths
                .OrderBy(f =>
                {
                    string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(f);

                    // This regex captures the last sequence of digits followed by "Z" or just digits
                    // e.g., "203Z", "7482"
                    var match = Regex.Match(fileNameWithoutExtension, @"(\d+Z|\d+)$");

                    if (match.Success)
                    {
                        string sortKey = match.Groups[1].Value;

                        if (sortKey.EndsWith("Z"))
                        {
                            // To make "203Z" sort before pure numbers like "7482",
                            // prepend a character that ensures alphabetical sorting.
                            // "A203Z" will sort before "B..."
                            return "A" + sortKey;
                        }
                        else
                        {
                            // For purely numerical endings, prepend "B" and pad with leading zeros
                            // to ensure correct numerical string sorting (e.g., "00007482").
                            // A pad length of 10 should be sufficient for most common numerical sequences.
                            return "B" + sortKey.PadLeft(10, '0');
                        }
                    }

                    // Fallback: If no specific pattern is found, sort by the full filename without extension
                    // This ensures files that don't match the pattern are still sorted predictably.
                    return "C" + fileNameWithoutExtension;
                })
                .ToArray();

            return sortedFiles;
        }

        private bool ShouldInsertBlankPage(int fileIndex, Job job, PdfDocument outputDoc, bool hasSharepointAttachment, ref bool blankInserted)
        {
            return fileIndex == 1 &&
                   job.Duplex == "D" &&
                   hasSharepointAttachment &&
                   !blankInserted &&
                   outputDoc.PageCount % 2 == 1;
        }

        private void AddBlankPage(PdfDocument outputDoc, PdfPage referencePage)
        {
            var blank = outputDoc.AddPage();
            blank.Width = referencePage.Width;
            blank.Height = referencePage.Height;
        }

        /// <summary>
        /// Adds error messages to a PDF file and saves it to the specified file path.
        /// </summary>
        /// <param name="filePath">The file path to save the PDF.</param>
        /// <param name="errorMessages">The list of error messages to add to the PDF.</param>
        private void AddErrorPdfMessages(string filePath, List<string> errorMessages)
        {
            try
            {
                using (var pdfDocument = new PdfSharpCore.Pdf.PdfDocument())
                {
                    var page = pdfDocument.AddPage();
                    var gfx = XGraphics.FromPdfPage(page);
                    var headerFont = new XFont("Verdana", 18, XFontStyle.Bold);
                    var errorFont = new XFont("Verdana", 10, XFontStyle.Regular);
                    var brush = XBrushes.Black;
                    var redBrush = XBrushes.Red;

                    // Define margins and page layout variables
                    const int leftMargin = 18;
                    const int rightMargin = 18;
                    const int bottomMargin = 60;
                    const double headerHeight = 80;
                    var rectWidth = page.Width - leftMargin - rightMargin;

                    // Draw header
                    gfx.DrawString("Error Report", headerFont, brush, new XRect(leftMargin, 40, rectWidth, headerHeight), XStringFormats.TopCenter);

                    // Draw a line below the header
                    gfx.DrawLine(XPens.Black, leftMargin, 80, page.Width - rightMargin, 80);

                    var yPosition = 100;

                    // Create XTextFormatter for text alignment
                    var tf = new XTextFormatter(gfx)
                    {
                        Alignment = XParagraphAlignment.Left
                    };

                    // Loop through error messages and add them to the PDF
                    foreach (var message in errorMessages)
                    {
                        // Define a rectangle area for each message
                        var rect = new XRect(leftMargin, yPosition, rectWidth, page.Height - yPosition - bottomMargin);

                        // Check if there's enough space, otherwise create a new page
                        if (yPosition + 20 > page.Height - bottomMargin)
                        {
                            page = pdfDocument.AddPage();
                            gfx.Dispose();
                            gfx = XGraphics.FromPdfPage(page);
                            tf = new XTextFormatter(gfx)
                            {
                                Alignment = XParagraphAlignment.Left
                            };

                            // Reset yPosition for the new page
                            yPosition = 40;

                            // Draw header on the new page
                            gfx.DrawString("Error Report - Continued", headerFont, brush, new XRect(leftMargin, yPosition, rectWidth, headerHeight), XStringFormats.TopCenter);
                            yPosition += 60;
                        }

                        // Draw the error message within the rectangle
                        tf.DrawString($"Error: {message}", errorFont, redBrush, rect, XStringFormats.TopLeft);

                        // Move yPosition for the next message
                        yPosition += 20;

                        // Save the error message to the database
                        var workOrderId = ExtractWorkOrderIdFromFilePath(filePath);
                        SaveWorkOrderErrorAsync(workOrderId, message).GetAwaiter().GetResult();

                        // Log the entry in the database
                        _dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = $"PDF Error Report Entry: {message}",
                            WorkOrderId = workOrderId
                        }).GetAwaiter().GetResult();
                    }

                    // Draw footer line
                    gfx.DrawLine(XPens.Black, leftMargin, page.Height - bottomMargin, page.Width - rightMargin, page.Height - bottomMargin);

                    // Add date/time below the footer line
                    var dateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    gfx.DrawString(dateTime, errorFont, brush, new XRect(leftMargin, page.Height - 40, rectWidth, 0), XStringFormats.BottomLeft);

                    // Save the PDF document
                    pdfDocument.Save(filePath);
                    _logger.LogInformation($"Error PDF saved successfully: {filePath}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error adding error messages to PDF file: {filePath}");

                // Log to the database
                _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = $"Failed to add error messages to PDF: {ex.Message}",
                    //  JobId = ExtractJobIdFromFilePath(filePath)
                }).GetAwaiter().GetResult();

                throw;
            }
        }


        /// <summary>
        /// Extracts the WorkOrderId from the given file path.
        /// </summary>
        /// <param name="filePath">The file path to extract the WorkOrderId from.</param>
        /// <returns>The extracted WorkOrderId.</returns>
        private string ExtractWorkOrderIdFromFilePath(string filePath)
        {
            try
            {
                // Get the file name without extension
                var fileName = Path.GetFileNameWithoutExtension(filePath);

                // Check if the file name starts with "Error_"
                if (fileName.StartsWith("Error_"))
                {
                    // Remove the "Error_" prefix and return the WorkOrderId
                    return fileName.Substring(6); // "Error_" has 6 characters
                }

                // Return the file name as WorkOrderId if it doesn't start with "Error_"
                return fileName;
            }
            catch (Exception ex)
            {
                // Log any exception that occurs during the extraction process
                _logger.LogError(ex, $"Error extracting WorkOrderId from file path: {filePath}");

                // Log the exception details in the database
                _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = $"Exception while extracting WorkOrderId from file path {filePath}: {ex.Message}",
                    WorkOrderId = Path.GetFileNameWithoutExtension(filePath) // Use filename as WorkOrderId placeholder
                }).GetAwaiter().GetResult();

                throw;
            }
        }

        /// <summary>
        /// Sanitises the file name by replacing invalid characters with an underscore.
        /// </summary>
        /// <param name="fileName">The file name to be sanitised.</param>
        /// <returns>The sanitised file name combined with the original directory.</returns>
        private string SanitiseFileName(string fileName)
        {
            try
            {
                // Check if the file name is null or empty to avoid further processing errors
                if (string.IsNullOrEmpty(fileName))
                {
                    var errorMessage = "File name is null or empty.";
                    _logger.LogError(null, errorMessage);

                    // Log the error to the database
                    _dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Message = errorMessage
                    }).GetAwaiter().GetResult();

                    throw new ArgumentNullException(nameof(fileName), errorMessage);
                }

                // Get the file name without the directory path
                var sanitisedFileName = Path.GetFileName(fileName);

                // Replace each invalid character in the file name with an underscore
                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    sanitisedFileName = sanitisedFileName.Replace(c, '_');
                }

                // Get and sanitise the directory path
                var directoryPath = Path.GetDirectoryName(fileName);
                if (!string.IsNullOrEmpty(directoryPath))
                {
                    foreach (char c in Path.GetInvalidPathChars())
                    {
                        directoryPath = directoryPath.Replace(c, '_');
                    }
                }

                // Combine the sanitised file name with the original (or sanitized) directory path
                return Path.Combine(directoryPath, sanitisedFileName);
            }
            catch (ArgumentNullException ex)
            {
                _logger.LogError(ex, "File name cannot be null or empty.");

                // Log the error to the database
                _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = $"File name cannot be null or empty: {ex.Message}"
                }).GetAwaiter().GetResult();

                throw;
            }
            catch (Exception ex)
            {
                // Log any other exceptions that occur during the sanitisation process
                _logger.LogError(ex, $"Error sanitising file name: {fileName}");

                // Log the exception details in the database
                _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = $"Exception during file name sanitisation for '{fileName}': {ex.Message}"
                }).GetAwaiter().GetResult();

                throw;
            }
        }

        private string SanitizeFilePath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return string.Empty;

            // Look for either %3F or ?
            int queryIndex = rawPath.IndexOf("%3F", StringComparison.OrdinalIgnoreCase);
            if (queryIndex == -1)
            {
                queryIndex = rawPath.IndexOf("?");
            }

            string cleanPath = queryIndex >= 0
                ? rawPath.Substring(0, queryIndex)
                : rawPath;

            // Decode any URL-encoded characters
            return Uri.UnescapeDataString(cleanPath);
        }

       
        /// <summary>
        /// Saves the error message for a specified work order and updates its status to "Error".
        /// </summary>
        /// <param name="workOrderId">The ID of the work order.</param>
        /// <param name="errorMessage">The error message to be saved.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public async Task SaveWorkOrderErrorAsync(string workOrderId, string errorMessage)
        {
            _logger.LogInformation($"Saving error for WorkOrderId: {workOrderId}");

            try
            {
                // Create a new scope for the data access operations
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();

                    // Retrieve the work order by ID
                    var workOrder = await dataAccess.GetWorkOrderByIdAsync(workOrderId);
                    if (workOrder == null)
                    {
                        _logger.LogWarning($"WorkOrder with Id {workOrderId} not found.");
                        return; // Exit if the work order is not found
                    }

                    // Update the work order with the error message and status
                    workOrder.ErrorMessage = errorMessage;
                    workOrder.Status = "Error"; // Set the status to Error
                    workOrder.ProcessedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture); // Set the ProcessedAt field

                    // Save the changes to the work order
                    await dataAccess.UpdateWorkOrderAsync(workOrder);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"An error occurred while saving the error for WorkOrderId: {workOrderId}");
                throw; // Optionally re-throw or handle the exception as needed
            }
        }

        /// <summary>
        /// Extracts the JobID from the specified XML file.
        /// </summary>
        /// <param name="filePath">The path to the XML file.</param>
        /// <returns>The JobID extracted from the XML file.</returns>
        /// <exception cref="Exception">Thrown when the JobID element is not found or an error occurs while reading the file.</exception>
        private async Task<(string JobId, string User, string Printer, string PrintLocation, string Duplex)> GetJobIdAndUserFromXmlAsync(string filePath, string uniqueJobId, IDataAccess dataAccess, CancellationToken cancellationToken)
        {
            try
            {
                // Open the file for reading
                using (var reader = new StreamReader(filePath, Encoding.UTF8, true))
                {
                    // Load the XML document
                    var document = XDocument.Load(reader);

                    // Extract the JobID and User elements
                    var jobIdElement = document.Root.Element("JobID");
                    var userElement = document.Root.Element("User");
                    var printerElement = document.Root.Element("Printer");
                    var printLocationElement = document.Root.Element("Print");
                    var duplexElement = document.Root.Element("Duplex");

                    if (jobIdElement == null || string.IsNullOrWhiteSpace(jobIdElement.Value))
                    {
                        var errorMessage = $"JobID element is missing or empty in XML file: {filePath}. XML content: {document}";
                        _logger.LogError(null, errorMessage);

                        // Log to database
                        await dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = errorMessage,
                            // FilePath = filePath
                        });

                        // Stop further processing and exit immediately
                        throw new InvalidOperationException(errorMessage);
                    }

                    // Return both JobID and User as a tuple
                    return (jobIdElement.Value, userElement?.Value, printerElement?.Value, printLocationElement?.Value, duplexElement?.Value); // userElement?.Value returns null if User element is not found
                }
            }
            catch (FileNotFoundException ex)
            {
                var errorMessage = $"File not found: {filePath}.";
                _logger.LogError(ex, errorMessage);

                // Log to database
                await dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = errorMessage,
                    // FilePath = filePath
                });

                // Stop further processing by throwing an exception to exit
                throw new InvalidOperationException(errorMessage, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                var errorMessage = $"Unauthorized access to file: {filePath}.";
                _logger.LogError(ex, errorMessage);

                // Log to database
                await dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = errorMessage,
                    // FilePath = filePath
                });

                // Stop further processing by throwing an exception to exit
                throw new InvalidOperationException(errorMessage, ex);
            }
            catch (XmlException ex)
            {
                var errorMessage = $"XML parsing error in file: {filePath}. Error message: {ex.Message}.";
                _logger.LogError(ex, errorMessage);

                // Log to database
                await dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = errorMessage,
                    // FilePath = filePath
                });

                // Stop further processing by throwing an exception to exit
                throw new InvalidOperationException(errorMessage, ex);
            }
            catch (Exception ex)
            {
                var errorMessage = $"An unexpected error occurred while reading JobID and User from XML file: {filePath}. Error message: {ex.Message}.";
                _logger.LogError(ex, errorMessage);

                // Log to database
                await dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = errorMessage,
                    // FilePath = filePath
                });

                // Stop further processing by throwing an exception to exit
                throw new InvalidOperationException(errorMessage, ex);
            }
        }


        /// <summary>
        /// Moves the specified file to the error folder.
        /// </summary>
        /// <param name="filePath">The path of the file to move.</param>

        private async Task MoveFileToErrorFolderAsync(string filePath, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    var errorMessage = "File path cannot be null or empty.";
                    _logger.LogError(null, errorMessage);

                    // Log to database
                    await _dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Message = errorMessage,
                        // FilePath = filePath
                    });

                    throw new ArgumentNullException(nameof(filePath), errorMessage);
                }

                // Define the error folder path based on config setup
                var errorFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SAP", "Error");
                Directory.CreateDirectory(errorFolderPath);

                var fileName = Path.GetFileName(filePath);

                if (string.IsNullOrEmpty(fileName))
                {
                    var errorMessage = $"File name could not be extracted from path: {filePath}.";
                    _logger.LogError(null, errorMessage);

                    // Log to database
                    await _dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Message = errorMessage,
                        // FilePath = filePath
                    });

                    throw new ArgumentNullException(nameof(fileName), errorMessage);
                }

                var errorFilePath = Path.Combine(errorFolderPath, fileName);

                // Ensure the original file is available and not locked before moving it
                await WaitForFileExistsAsync(filePath, cancellationToken);

                // If a file with the same name exists in the error folder, delete it if not locked
                if (File.Exists(errorFilePath))
                {
                    await WaitForFileExistsAsync(errorFilePath, cancellationToken);

                    if (IsFileLocked(errorFilePath))
                    {
                        var lockErrorMessage = $"Error file is locked and cannot be deleted: {errorFilePath}";
                        _logger.LogWarning(lockErrorMessage);

                        // Log to database
                        await _dataAccess.AddLogEntryAsync(new LogEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Warning",
                            Message = lockErrorMessage,
                            // FilePath = errorFilePath
                        });

                        throw new IOException(lockErrorMessage);
                    }

                    File.Delete(errorFilePath);
                    _logger.LogInformation($"Deleted existing file in the error folder: {errorFilePath}");
                }

                // Double-check the original file isn't locked before moving it
                await WaitForFileExistsAsync(filePath, cancellationToken);

                if (IsFileLocked(filePath))
                {
                    var lockErrorMessage = $"File is locked and cannot be moved: {filePath}";
                    _logger.LogWarning(lockErrorMessage);

                    // Log to database
                    await _dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Warning",
                        Message = lockErrorMessage,
                        //  FilePath = filePath
                    });

                    throw new IOException(lockErrorMessage);
                }

                // Move the file to the error folder
                File.Move(filePath, errorFilePath);
                _logger.LogInformation($"Successfully moved file from {filePath} to {errorFilePath}");
            }
            catch (DirectoryNotFoundException ex)
            {
                var errorMessage = $"Directory not found: {filePath}";
                _logger.LogError(ex, errorMessage);

                // Log to database
                await _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = errorMessage,
                    // FilePath = filePath
                });

                throw new Exception(errorMessage, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                var errorMessage = $"Unauthorized access to file or directory: {filePath}";
                _logger.LogError(ex, errorMessage);

                // Log to database
                await _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = errorMessage,
                    // FilePath = filePath
                });

                throw new Exception(errorMessage, ex);
            }
            catch (IOException ex)
            {
                var errorMessage = $"IO error while moving file {filePath} to error folder: {ex.Message}";
                _logger.LogError(ex, errorMessage);

                // Log to database
                await _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = errorMessage,
                    //  FilePath = filePath
                });

                throw new Exception(errorMessage, ex);
            }
            catch (Exception ex)
            {
                var errorMessage = $"An unexpected error occurred while moving file {filePath} to error folder: {ex.Message}";
                _logger.LogError(ex, errorMessage);

                // Log to database
                await _dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = errorMessage,
                    // FilePath = filePath
                });

                throw new Exception(errorMessage, ex);
            }
        }



        /// <summary>
        /// Parses the specified XML file and creates work order files.
        /// </summary>
        /// <param name="filePath">The path of the XML file to parse.</param>
        /// <param name="cacheFolderPath">The path of the folder to cache the work order files.</param>
        /// <param name="jobId">The ID of the job associated with the work orders.</param>
        /// <returns>A list of paths to the created work order files.</returns>
        private async Task<List<string>> ParseAndCreateWorkOrderFiles(string filePath, string cacheFolderPath, string jobId, CancellationToken cancellationToken)
        {
            var workOrderFilePaths = new List<string>();
            var errorFilePaths = new List<string>();
            string uniqueJobId = null;

            try
            {
                using (var reader = new StreamReader(filePath, Encoding.UTF8, true))
                {
                    var document = XDocument.Load(reader);
                    var workOrderTasks = document.Descendants("WorkOrder").Select(async workOrderElement =>
                    {
                        var workOrderId = workOrderElement.Element("Id").Value;
                        var mainWorkOrderFilePath = workOrderElement.Element("File").Value;

                        // Ensure the main work order file is added to the workOrderFilePaths list
                        if (!string.IsNullOrEmpty(mainWorkOrderFilePath))
                        {
                            workOrderFilePaths.Add(mainWorkOrderFilePath);
                        }

                        var workOrderFilePath = Path.Combine(cacheFolderPath, $"{workOrderId}.xml");
                        workOrderFilePaths.Add(workOrderFilePath);

                        using (var scope = _serviceScopeFactory.CreateScope())
                        {
                            var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();

                            try
                            {
                                // Fetch the Job and its UniqueJobId
                                var job = await dataAccess.GetJobByIdAsync(jobId);
                                if (job == null)
                                {
                                    throw new Exception($"Job with ID {jobId} not found.");
                                }
                                uniqueJobId = job.UniqueJobId;
                               // _logger.LogInformation($"Using UniqueJobId: {uniqueJobId} for WorkOrderId: {workOrderId}");

                                // Add new work order regardless of previous existence
                                var workOrder = new WorkOrder
                                {
                                    JobId = jobId,
                                    UniqueJobId = uniqueJobId,
                                    WorkOrderId = workOrderId,
                                    Objtyp = workOrderElement.Element("Objtyp")?.Value,
                                    File = mainWorkOrderFilePath,
                                    Status = "Pending",
                                    CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                                    ProcessedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)

                                };

                                await dataAccess.AddWorkOrderAsync(workOrder);

                                // Save work order file
                                var workOrderXElement = new XElement("WorkOrder", workOrderElement.Elements());
                                var workOrderDocument = new XDocument(
                                    new XDeclaration("1.0", "utf-8", "yes"),
                                    workOrderXElement
                                );
                                workOrderDocument.Save(workOrderFilePath);
                                _logger.LogInformation($"Successfully created file: {workOrderFilePath}");

                                // Add work order parts
                                foreach (var part in workOrderElement.Descendants("WorkOrderPart"))
                                {
                                    var workOrderPartId = part.Element("Id")?.Value;
                                    try
                                    {
                                        var partFilePath = CleanUrl(part.Element("FilePath")?.Value);
                                        partFilePath = SanitizeFilePath(partFilePath);

                                        workOrderFilePaths.Add(partFilePath);

                                        var workOrderPart = new WorkOrderPart
                                        {
                                            UniqueJobId = uniqueJobId,
                                            WorkOrderId = workOrderId,
                                            PartId = workOrderPartId,
                                            SeqId = part.Element("SeqId")?.Value,
                                            Type = part.Element("Type")?.Value,
                                            FilePath = partFilePath,
                                            Status = "Pending",
                                            CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                                            ProcessedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                                        };

                                        using (var partScope = _serviceScopeFactory.CreateScope())
                                        {
                                            var partDataAccess = partScope.ServiceProvider.GetRequiredService<IDataAccess>();
                                            await partDataAccess.AddWorkOrderPartAsync(workOrderPart);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        var errorMessage = $"Error processing work order part: {part.Element("FilePath")?.Value} - {ex.Message}";
                                        _logger.LogError(ex, errorMessage);

                                        var errorFilePath = Path.Combine(cacheFolderPath, $"Error_{workOrderId}.pdf");
                                        AddErrorPdfMessages(errorFilePath, new List<string> { errorMessage });
                                        errorFilePaths.Add(errorFilePath);

                                        await SaveWorkOrderErrorAsync(workOrderId, errorMessage);
                                        await dataAccess.AddLogEntryAsync(new LogEntry
                                        {
                                            Timestamp = DateTime.UtcNow,
                                            Level = "Error",
                                            Message = errorMessage,
                                            JobId = uniqueJobId,
                                            WorkOrderId = workOrderId,
                                        });

                                        await dataAccess.UpdateWorkOrderPartStatusAsync(uniqueJobId, workOrderId, workOrderPartId, "Error", "", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                                    }
                                }

                                // Update the status of the work order to Processed if no error occurred
                                var partStatuses = await dataAccess.GetWorkOrderPartsAsync(uniqueJobId, workOrderId);
                                if (partStatuses.Any(p => p.Status == "Error"))
                                {
                                    await dataAccess.UpdateWorkOrderStatusAsync(uniqueJobId, workOrderId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                                }
                                else
                                {
                                    await dataAccess.UpdateWorkOrderStatusAsync(uniqueJobId, workOrderId, "Processed", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                                }

                                _logger.LogInformation($"Updated status for WorkOrder ID {workOrderId}");
                            }
                            catch (Exception ex)
                            {
                                var errorMessage = $"Error processing work order {workOrderId} - {ex.Message}";
                                _logger.LogError(ex, errorMessage);

                                // Path for the error PDF specific to this work order
                                var errorFilePath = Path.Combine(cacheFolderPath, $"Error_{workOrderId}.pdf");
                                AddErrorPdfMessages(errorFilePath, new List<string> { errorMessage });
                                errorFilePaths.Add(errorFilePath);

                                // Save detailed error message to the database
                                await SaveErrorDetails(jobId, new List<string> { ex.Message }, cancellationToken);

                                // Log to the database with job and work order information
                                await dataAccess.AddLogEntryAsync(new LogEntry
                                {
                                    Timestamp = DateTime.UtcNow,
                                    Level = "Error",
                                    Message = errorMessage,
                                    JobId = jobId,
                                    WorkOrderId = workOrderId
                                });

                                // Set the work order status to Error in the database
                                await dataAccess.UpdateWorkOrderStatusAsync(uniqueJobId, workOrderId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                            }

                        }
                    });

                    await Task.WhenAll(workOrderTasks);
                }
            }
            catch (Exception ex)
            {
                var errorMessage = $"Error parsing and creating work order files from {filePath} - {ex.Message}";
                _logger.LogError(ex, errorMessage);

                // Path for the error PDF specific to this job
                var errorFilePath = Path.Combine(cacheFolderPath, $"Error_{jobId}.pdf");
                AddErrorPdfMessages(errorFilePath, new List<string> { errorMessage });
                errorFilePaths.Add(errorFilePath);

                // Save detailed error message to the database
                await SaveErrorDetails(jobId, new List<string> { ex.Message }, cancellationToken);

                // Log to the database with job ID and error message
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();

                    // Add error log entry for tracking in database
                    await dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Message = errorMessage,
                        JobId = jobId
                    });

                    // Update job status to "Error"
                    await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                }

                throw;
            }


            return workOrderFilePaths.Concat(errorFilePaths).ToList();
        }

        /// <summary>
        /// Parses the specified XML file and creates work order files with operation splitting.
        /// </summary>
        /// <param name="filePath">The path of the XML file to parse.</param>
        /// <param name="cacheFolderPath">The path of the folder to cache the work order files.</param>
        /// <param name="jobId">The ID of the job associated with the work orders.</param>
        /// <returns>A list of paths to the created work order files.</returns>

        private async Task<List<string>> ParseAndCreateWorkOrderFilesWithOperationSplitting(
        string filePath,
        string cacheFolderPath,
        string jobId,
        CancellationToken cancellationToken)
        {
            var orderedPaths = new List<string>();
            var errorFilePaths = new List<string>();
            string uniqueJobId = null;

            try
            {
                using var reader = new StreamReader(filePath, Encoding.UTF8, true);
                var document = XDocument.Load(reader);
                var workOrderElements = document.Descendants("WorkOrder").ToList();

                using var scope = _serviceScopeFactory.CreateScope();
                var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                var job = await dataAccess.GetJobByIdAsync(jobId);
                if (job == null) throw new Exception($"Job with ID {jobId} not found.");

                uniqueJobId = job.UniqueJobId;

                for (int workOrderIndex = 0; workOrderIndex < workOrderElements.Count; workOrderIndex++)
                {
                    var workOrderElement = workOrderElements[workOrderIndex];
                    var workOrderId = workOrderElement.Element("Id")?.Value;
                    var mainWorkOrderFilePath = workOrderElement.Element("File")?.Value;

                    if (!string.IsNullOrEmpty(mainWorkOrderFilePath))
                        orderedPaths.Add(mainWorkOrderFilePath);

                    var workOrderFilePath = Path.Combine(cacheFolderPath, $"{workOrderId}.xml");
                    orderedPaths.Add(workOrderFilePath);

                    var existingWorkOrder = await dataAccess.GetWorkOrderByIdAsync(uniqueJobId, workOrderId);
                    if (existingWorkOrder != null)
                    {
                        _logger.LogWarning($"WorkOrder {workOrderId} already exists. Skipping.");
                        continue;
                    }

                    var workOrder = new WorkOrder
                    {
                        JobId = jobId,
                        UniqueJobId = uniqueJobId,
                        WorkOrderId = workOrderId,
                        Objtyp = workOrderElement.Element("Objtyp")?.Value,
                        File = mainWorkOrderFilePath,
                        Status = "Pending",
                        CreatedAt = DateTime.UtcNow.ToString("o"),
                        ProcessedAt = DateTime.UtcNow.ToString("o")
                    };

                    await dataAccess.AddWorkOrderAsync(workOrder);

                    var workOrderXElement = new XElement("WorkOrder", workOrderElement.Elements());
                    new XDocument(new XDeclaration("1.0", "utf-8", "yes"), workOrderXElement).Save(workOrderFilePath);
                    _logger.LogInformation($"Created file: {workOrderFilePath}");

                    var operationParts = new List<XElement>();
                    var partElements = workOrderElement.Descendants("WorkOrderPart").ToList();

                    for (int partIndex = 0; partIndex < partElements.Count; partIndex++)
                    {
                        var part = partElements[partIndex];
                        try
                        {
                            var partId = part.Element("Id")?.Value;
                            var partFilePath = SanitizeFilePath(CleanUrl(part.Element("FilePath")?.Value));
                            orderedPaths.Add(partFilePath);

                            var workOrderPart = new WorkOrderPart
                            {
                                WorkOrderId = workOrderId,
                                UniqueJobId = uniqueJobId,
                                PartId = partId,
                                SeqId = part.Element("SeqId")?.Value,
                                Type = part.Element("Type")?.Value,
                                FilePath = partFilePath,
                                Status = "Pending",
                                CreatedAt = DateTime.UtcNow.ToString("o"),
                                ProcessedAt = DateTime.UtcNow.ToString("o")
                            };

                            await dataAccess.AddWorkOrderPartAsync(workOrderPart);

                            if (IsHeader(part)) operationParts.Clear();
                                operationParts.Add(part);

                            if (IsOperation(part))
                            {
                                var operationId = part.Element("Id")?.Value;
                                var operationFilePath = Path.Combine(cacheFolderPath, $"{workOrderId}_{operationId}.xml");

                                var operationDocument = new XDocument(
                                    new XDeclaration("1.0", "utf-8", "yes"),
                                    new XElement("WorkOrder",
                                        new XElement("Id", workOrderId),
                                        new XElement("Objtyp", workOrderElement.Element("Objtyp")?.Value),
                                        new XElement("File", mainWorkOrderFilePath),
                                        new XElement("OperationId", operationId),
                                        operationParts)
                                );

                                operationDocument.Save(operationFilePath);
                                orderedPaths.Add(operationFilePath);

                                _logger.LogInformation($"Created operation file: {operationFilePath}");
                            }
                        }
                        catch (Exception partEx)
                        {
                            var partErrorMsg = $"Error processing part: {part.Element("FilePath")?.Value} - {partEx.Message}";
                            _logger.LogError(partEx, partErrorMsg);

                            var errorFile = Path.Combine(cacheFolderPath, $"Error_{workOrderId}.pdf");
                            AddErrorPdfMessages(errorFile, new List<string> { partErrorMsg });
                            errorFilePaths.Add(errorFile);

                            await dataAccess.AddLogEntryAsync(new LogEntry
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Error",
                                Message = partErrorMsg,
                                JobId = jobId,
                                WorkOrderId = workOrderId
                            });

                            await dataAccess.UpdateWorkOrderPartStatusAsync(uniqueJobId, workOrderId, part.Element("Id")?.Value, "Error", "", DateTime.UtcNow.ToString("o"));
                        }
                    }

                    var parts = await dataAccess.GetWorkOrderPartsAsync(uniqueJobId, workOrderId);

                    var status = parts.Any(p => p.Status == "Error") ? "Error" : "Processed";

                    await dataAccess.UpdateWorkOrderStatusAsync(uniqueJobId, workOrderId, status, DateTime.UtcNow.ToString("o"));

                    _logger.LogInformation($"Updated status for WorkOrder ID {workOrderId}");
                }
            }
            catch (Exception ex)
            {
                var msg = $"Error parsing and creating work orders from {filePath} - {ex.Message}";
                _logger.LogError(ex, msg);
                var errorFile = Path.Combine(cacheFolderPath, $"Error_{jobId}.pdf");
                AddErrorPdfMessages(errorFile, new List<string> { msg });
                errorFilePaths.Add(errorFile);

                using var scope = _serviceScopeFactory.CreateScope();
                var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                await dataAccess.AddLogEntryAsync(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = msg,
                    JobId = jobId
                });

                if (uniqueJobId != null)
                {
                    await dataAccess.UpdateJobStatusAsync(jobId, uniqueJobId, "Error", DateTime.UtcNow.ToString("o"));
                }

                throw;
            }

            return orderedPaths.Concat(errorFilePaths).ToList();
        }

        /// <summary>
        /// Determines whether the specified element represents a header.
        /// </summary>
        /// <param name="element">The XML element to check.</param>
        /// <returns><c>true</c> if the element represents a header; otherwise, <c>false</c>.</returns>
        private bool IsHeader(XElement element)
        {
            try
            {
                var headerKeywords = new Dictionary<string, string[]>
        {
            { "EN", new[] { "HEADER" } },
            { "ES", new[] { "ENCABEZADO" } },
            { "FR", new[] { "EN-TÊTE" } },
            { "PT", new[] { "CABEÇALHO" } },
            { "ZH", new[] { "标题" } },
            { "RU", new[] { "ЗАГОЛОВОК" } },
            { "SV", new[] { "HUVUD" } },
            { "ID", new[] { "HEADER" } } // Assuming "HEADER" is the same in Indonesian
        };

                foreach (var keywords in headerKeywords.Values)
                {
                    if (keywords.Any(k => element.Element("Type").Value.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error determining if element is a header.");
                throw;
            }

            return false;
        }


        /// <summary>
        /// Determines whether the specified element represents an operation.
        /// </summary>
        /// <param name="element">The XML element to check.</param>
        /// <returns><c>true</c> if the element represents an operation; otherwise, <c>false</c>.</returns>
        private bool IsOperation(XElement element)
        {
            try
            {
                var operationKeywords = new Dictionary<string, string[]>
        {
            { "EN", new[] { "OPERATION" } },
            { "ES", new[] { "OPERACIÓN" } },
            { "FR", new[] { "OPÉRATION" } },
            { "PT", new[] { "OPERAÇÃO" } },
            { "ZH", new[] { "操作" } },
            { "RU", new[] { "ОПЕРАЦИЯ" } },
            { "SV", new[] { "OPERATION" } },
            { "ID", new[] { "OPERASI" } }
        };

                foreach (var keywords in operationKeywords.Values)
                {
                    if (keywords.Any(k => element.Element("Type").Value.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error determining if element is an operation.");
                throw;
            }

            return false;
        }


        /// <summary>
        /// Cleans the specified URL by removing CDATA tags and replacing URL-encoded spaces with actual spaces.
        /// </summary>
        /// <param name="url">The URL to clean.</param>
        /// <returns>The cleaned URL.</returns>
        private string CleanUrl(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url))
                {
                    throw new ArgumentException("The URL cannot be null or empty.", nameof(url));
                }

                //return url.Replace("<![CDATA[", "").Replace("]]>", "").Replace("%20", " ");
                return url.Replace("<![CDATA[", "").Replace("]]>", "").Replace("%20", " ").Replace("%23", "#");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error cleaning URL: {url}");

                // Log the error to the database
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                    dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Message = $"Error cleaning URL: {url} - {ex.Message}",
                    }).Wait();
                }

                throw;
            }
        }

        /// <summary>
        /// Cleans a file name by replacing invalid characters with underscores and truncating
        /// the length to a maximum of 100 characters if necessary.
        /// </summary>
        /// <param name="fileName">The original file name.</param>
        /// <returns>The sanitized file name.</returns>
        private string CleanFileName(string fileName)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(c, '_');
            }
            // Limit filename length to 100 characters
            if (fileName.Length > 100)
            {
                fileName = fileName.Substring(0, 100);
            }
            return fileName;
        }

        /// <summary>
        /// Helper method to identify SAP DMS files based on a specific pattern.
        /// </summary>
        /// <param name="filePath">The file path to check.</param>
        /// <returns>True if the file path matches the SAP DMS pattern; otherwise, false.</returns>
        private bool IsSapDmsFile(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    throw new ArgumentException("The file path cannot be null or empty.", nameof(filePath));
                }

                // Example pattern: 'WIN','CTPS-TE-DP-E004','00','000','B92060CE57A11EEF8FEB51B64952BFF3','B92060CE57A11EEF8FEB51B64952FFF3','2014954','PMAUFK'
                return filePath.StartsWith("'") && filePath.Contains("','");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error identifying SAP DMS file: {filePath}");

                // Log the error to the database
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                    dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Message = $"Error identifying SAP DMS file: {filePath} - {ex.Message}",
                    }).Wait();
                }

                throw;
            }
        }

        private bool IsDropboxFile(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    throw new ArgumentException("The file path cannot be null or empty.", nameof(filePath));
                }

                // Example Dropbox file detection:
                // Assume Dropbox files look like normal paths: "/folder/file.pdf"
                return filePath.Contains("https://www.dropbox.com");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error identifying Dropbox file: {filePath}");

                // Log the error to the database
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var dataAccess = scope.ServiceProvider.GetRequiredService<IDataAccess>();
                    dataAccess.AddLogEntryAsync(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Message = $"Error identifying Dropbox file: {filePath} - {ex.Message}",
                    }).Wait();
                }

                throw;
            }
        }
        public bool IsValidDropboxPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            if (filePath.Length > 4096)
                return false;

            // Optionally, check for forbidden characters
            if (filePath.Any(c => char.IsControl(c)))
                return false;

            return true;
        }


        /// <summary>
        /// Deletes existing error PDF files for the given work orders.
        /// </summary>
        /// <param name="cacheFolderPath">The path to the cache folder where error PDFs are stored.</param>
        /// <param name="workOrderFilePaths">The list of work order file paths to check for corresponding error PDFs.</param>
        private void DeleteExistingErrorPdfs(string cacheFolderPath, List<string> workOrderFilePaths)
        {
            try
            {
                // Validate input parameters
                if (string.IsNullOrEmpty(cacheFolderPath))
                {
                    throw new ArgumentException("Cache folder path cannot be null or empty.", nameof(cacheFolderPath));
                }
                if (workOrderFilePaths == null || !workOrderFilePaths.Any())
                {
                    throw new ArgumentException("Work order file paths cannot be null or empty.", nameof(workOrderFilePaths));
                }

                foreach (var filePath in workOrderFilePaths)
                {
                    try
                    {
                        var workOrderId = Path.GetFileNameWithoutExtension(filePath);
                        var errorFilePath = Path.Combine(cacheFolderPath, $"Error_{workOrderId}.pdf");

                        if (File.Exists(errorFilePath))
                        {
                            // Check if the file is locked
                            if (IsFileLocked(errorFilePath))
                            {
                                _logger.LogWarning($"Error PDF is locked and cannot be deleted: {errorFilePath}");
                            }
                            else
                            {
                                File.Delete(errorFilePath);
                                _logger.LogInformation($"Deleted existing error PDF: {errorFilePath}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error deleting error PDF for file path: {filePath}");
                        // Optionally, you can choose to continue processing other files or re-throw the exception
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while deleting existing error PDFs.");
                throw; // Optionally re-throw or handle the exception as needed
            }
        }

        /// <summary>
        /// Asynchronously cleans up old files in specified folders that exceed the file retention period.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task CleanUpOldFilesAsync()
        {
            _logger.LogInformation("Starting clean-up of old files.");

            // Fetch the file retention period from the configuration settings
            var retentionDaysSetting = await _dataAccess.GetConfigurationSettingAsync("AppSettings", "FileRetentionDays");
            if (retentionDaysSetting == null || !int.TryParse(retentionDaysSetting.Value, out int retentionDays))
            {
                _logger.LogError(null, "File retention days setting is missing or invalid.");
                return;
            }

            _logger.LogInformation($"File retention period is set to {retentionDays} days.");

            // Calculate the retention period as a TimeSpan
            var retentionPeriod = TimeSpan.FromDays(retentionDays);

            // Fetch the environment setting
            var environmentSetting = await _dataAccess.GetConfigurationSettingAsync("AppSettings", "Environment");
            if (environmentSetting == null)
            {
                _logger.LogError(null, "Environment configuration not found.");
                return;
            }

            var environment = environmentSetting.Value;
           // _logger.LogInformation($"Environment: {environment}");

            // Fetch SAP folder settings
            var sapFolderSettings = await _dataAccess.GetSapFolderSettingsAsync();
            var sapServer = sapFolderSettings.FirstOrDefault(s => s.Name == environment);
            if (sapServer == null)
            {
                _logger.LogError(null, "SAP server configuration not found.");
                return;
            }

            // Define the folders to be cleaned up using the SAP server path
            var foldersToClean = new[] { "Cache", "Out", "Xml" };

            foreach (var folder in foldersToClean)
            {
                var directoryPath = Path.Combine(sapServer.Path, sapServer.Name, folder);

                // Check if the directory exists
                if (Directory.Exists(directoryPath))
                {
                    _logger.LogInformation($"Cleaning up folder: {directoryPath}");

                    // Get all files in the directory
                    var files = Directory.GetFiles(directoryPath);
                    foreach (var file in files)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(file);

                            // Delete files older than the retention period
                            if (DateTime.UtcNow - fileInfo.CreationTimeUtc > retentionPeriod)
                            {
                                File.Delete(file);
                                _logger.LogInformation($"Deleted file: {file}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error deleting file: {file}");
                        }
                    }
                }
                else
                {
                    _logger.LogWarning($"Directory does not exist: {directoryPath}");
                }
            }

            _logger.LogInformation("Clean-up of old files completed.");
        }


        /// <summary>
        /// Asynchronously cleans up old database records that exceed the retention period specified in the configuration.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task CleanUpDatabaseAsync()
        {
            // Fetch the database records retention period from the configuration settings
            var retentionSetting = await _dataAccess.GetConfigurationSettingAsync("AppSettings", "DBRecordsRetentionDays");
            if (retentionSetting == null)
            {
                throw new Exception("DBRecordsRetentionDays configuration not found.");
            }

            // Parse the retention days and calculate the threshold date
            int retentionDays = int.Parse(retentionSetting.Value);
            DateTime thresholdDate = DateTime.UtcNow.AddDays(-retentionDays);

            _logger.LogInformation($"Cleaning up database records older than {thresholdDate}");

            // Delete old work order parts, work orders, and jobs from the database
            await _dataAccess.DeleteOldWorkOrderPartsAsync(thresholdDate);
            await _dataAccess.DeleteOldWorkOrdersAsync(thresholdDate);
            await _dataAccess.DeleteOldJobsAsync(thresholdDate);
            await _dataAccess.DeleteOldLogsAsync(thresholdDate);

            _logger.LogInformation("Database cleanup completed.");
        }

        public bool NeedsPdfRepair(string filePath)
        {
            try
            {
                // Attempt to open the file with PdfSharpCore
                using var doc = PdfReader.Open(filePath, PdfDocumentOpenMode.Import);
                return false; // No error, PDF is fine
            }
            catch (PositionNotFoundException ex)
            {
                // PdfSharpCore couldn't find a valid object position
                Console.WriteLine($"Corrupt PDF detected: {ex.Message}");
                return true;
            }
            catch (InvalidOperationException ex)
            {
                // PdfSharpCore may also throw this for broken cross-references
                Console.WriteLine($"Likely corrupt PDF: {ex.Message}");
                return true;
            }
            catch (Exception ex)
            {
                // Unknown exception — treat as needing repair if it's not safe to process
                Console.WriteLine($"Unhandled PDF read error: {ex.Message}");
                return true;
            }

        }
    }

}