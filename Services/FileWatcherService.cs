using CommonInterfaces;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System;
using PrintPlusService.Services;
using System.Threading;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using PrintPlusService.Utilities;
using CommonInterfaces.Models;

/// <summary>
/// Service that watches an input folder for new XML files,
/// enqueues them for processing, and processes multiple files in parallel.
/// </summary>
public class FileWatcherService : IFileWatcherService
{
    // Logging abstraction for structured logging
    private readonly ILoggerService _logger;

    // Reference to file processor service
    private readonly IFileProcessor _fileProcessor;

    // Data access for DB operations if needed
    private readonly IDataAccess _dataAccess;

    // Used for sending HTTP notifications
    private readonly IHttpClientFactory _httpClientFactory;

    // API base URL (from config)
    private readonly string _apiBaseUrl;

    // Thread-safe queue of file paths to process
    private readonly ConcurrentQueue<string> _processingQueue;

    // Set of files currently being processed (avoids duplicate processing)
    private readonly HashSet<string> _currentlyProcessingFiles;

    // Used to lock access to _currentlyProcessingFiles for thread safety
    private readonly object _lockObject = new object();

    // Controls how many files can be processed at the same time
    private readonly int _maxParallelism;

    // FileSystemWatcher for detecting new/changed files
    private FileSystemWatcher _watcher;

    // Folder being watched
    private string _inFolderPath;

    /// <summary>
    /// Constructor injects dependencies and loads settings.
    /// </summary>
    public FileWatcherService(
        ILoggerService logger,
        IFileProcessor fileProcessor,
        IDataAccess dataAccess,
        IHttpClientFactory httpClientFactory,
        IOptions<ApiSettings> apiSettings,
        IOptions<FileWatcherSettings> fileWatcherSettings)
    {
        _logger = logger;
        _fileProcessor = fileProcessor;
        _dataAccess = dataAccess;
        _httpClientFactory = httpClientFactory;

        // Get the API base URL from settings and validate it
        _apiBaseUrl = apiSettings.Value.BaseUrl;
        if (string.IsNullOrEmpty(_apiBaseUrl) || !Uri.IsWellFormedUriString(_apiBaseUrl, UriKind.Absolute))
            throw new InvalidOperationException("The API base URL is not set or is not a valid absolute URI.");

        // Initialise concurrent structures
        _processingQueue = new ConcurrentQueue<string>();
        _currentlyProcessingFiles = new HashSet<string>();

        // Get parallelism setting or default to 4 if not set
        _maxParallelism = fileWatcherSettings?.Value?.MaxParallelFileProcessing ?? 4;
    }

    /// <summary>
    /// Start monitoring the folder for XML files.
    /// </summary>
    public void StartWatching(string inFolderPath)
    {
        _logger.LogInformation("Initialising FileWatcherService...");
        _inFolderPath = inFolderPath;

        // Process any XML files already in the folder before starting watcher
        ProcessExistingFiles();

        // Setup the FileSystemWatcher
        _watcher = new FileSystemWatcher(_inFolderPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            Filter = "*.xml",
            EnableRaisingEvents = true,
            IncludeSubdirectories = false
        };

        // Watch for new files or changes
        _watcher.Created += OnFileCreatedOrChanged;
        _watcher.Changed += OnFileCreatedOrChanged;
        _watcher.Error += OnError;

        _logger.LogInformation($"Started watching folder: {_inFolderPath} for XML files.");
    }

    /// <summary>
    /// Process all XML files that were already present in the folder at startup.
    /// </summary>
    private async void ProcessExistingFiles()
    {
        _logger.LogInformation("Processing existing XML files in the folder...");

        try
        {
            var existingFiles = Directory.GetFiles(_inFolderPath, "*.xml");
            int totalFiles = existingFiles.Length;
            int filesProcessed = 0;
            DateTime? lastJobRunStarted = DateTime.UtcNow;
            DateTime? lastJobRunCompleted = null;

            // Use parallel processing (limit: _maxParallelism)
            await Parallel.ForEachAsync(existingFiles, new ParallelOptions { MaxDegreeOfParallelism = _maxParallelism }, async (filePath, ct) =>
            {
                var jobId = Path.GetFileNameWithoutExtension(filePath);

                try
                {
                    // Report progress before processing
                    int progressPercentage = (int)(((double)Interlocked.Add(ref filesProcessed, 1) / totalFiles) * 100);
                    await SendProgressUpdateAsync(jobId, $"Processing file", progressPercentage, filesProcessed, lastJobRunStarted, null, CancellationToken.None);

                    // Process the file
                    await _fileProcessor.ProcessFile(filePath);

                    // Report progress after processing
                    progressPercentage = (int)(((double)filesProcessed / totalFiles) * 100);
                    await SendProgressUpdateAsync(jobId, "Completed (Existing File)", progressPercentage, filesProcessed, lastJobRunStarted, null, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // Log and report error for this file
                    await SendProgressUpdateAsync(jobId, "Error (Existing File)", 0, filesProcessed, lastJobRunStarted, null, CancellationToken.None);
                }
            });

            // All files done - send summary notification
            if (filesProcessed > 0)
            {
                lastJobRunCompleted = DateTime.UtcNow;
                await SendProgressUpdateAsync("Summary", "All files processed", 100, filesProcessed, lastJobRunStarted, lastJobRunCompleted, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing existing XML files.");
        }
    }

    /// <summary>
    /// Event handler: Enqueue the file for processing when it's created or changed.
    /// </summary>
    private void OnFileCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        EnqueueFileForProcessing(e.FullPath);
    }

    /// <summary>
    /// Enqueue a file for processing, ensuring it's not already being processed.
    /// </summary>
    private void EnqueueFileForProcessing(string filePath)
    {
        lock (_lockObject)
        {
            if (!_currentlyProcessingFiles.Contains(filePath))
            {
                _processingQueue.Enqueue(filePath);
                _currentlyProcessingFiles.Add(filePath);
                _logger.LogInformation($"File added to queue: {filePath}");
            }
        }
    }

    /// <summary>
    /// Handles errors from the FileSystemWatcher.
    /// </summary>
    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "File system watcher error detected.");
    }

    /// <summary>
    /// Processes files from the queue in parallel using a configurable max degree of parallelism.
    /// </summary>
    public async Task ProcessFilesFromQueue(CancellationToken stoppingToken)
    {
        int filesProcessed = 0;
        DateTime? lastJobRunStarted = DateTime.UtcNow;
        DateTime? lastJobRunCompleted = null;

        // Semaphore limits how many files process at once
        using var semaphore = new SemaphoreSlim(_maxParallelism);
        var runningTasks = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_processingQueue.TryDequeue(out var filePath))
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    _logger.LogWarning("File path is empty. Skipping.");
                    continue;
                }

                await semaphore.WaitAsync(stoppingToken);
                var jobId = Path.GetFileNameWithoutExtension(filePath);

                // Start processing in background
                var task = Task.Run(async () =>
                {
                    try
                    {
                        //_logger.LogInformation($"Processing file: {filePath}");
                        if (filesProcessed == 0) lastJobRunStarted = DateTime.UtcNow;

                        int progressPercentage = (int)(((double)filesProcessed / _processingQueue.Count + 1) * 100);

                        await SendProgressUpdateAsync(jobId, "Starting", progressPercentage, filesProcessed, lastJobRunStarted, null, stoppingToken);
                        await _fileProcessor.ProcessFile(filePath);
                        Interlocked.Increment(ref filesProcessed);

                        progressPercentage = (int)(((double)filesProcessed / (_processingQueue.Count + filesProcessed)) * 100);
                        await SendProgressUpdateAsync(jobId, "Completed", progressPercentage, filesProcessed, lastJobRunStarted, null, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error processing file: {filePath}");
                        await SendProgressUpdateAsync(jobId, "Error", 0, filesProcessed, lastJobRunStarted, null, stoppingToken);
                    }
                    finally
                    {
                        // Remove from currently processing, release slot
                        lock (_lockObject)
                        {
                            _currentlyProcessingFiles.Remove(filePath);
                        }
                        semaphore.Release();
                    }
                }, stoppingToken);

                runningTasks.Add(task);
            }
            else
            {
                // If done processing, send summary and wait for any remaining tasks to finish
                if (filesProcessed > 0 && lastJobRunCompleted == null && runningTasks.Count > 0)
                {
                    await Task.WhenAll(runningTasks);
                    lastJobRunCompleted = DateTime.UtcNow;
                    await SendProgressUpdateAsync("Summary", "All files processed", 100, filesProcessed, lastJobRunStarted, lastJobRunCompleted, stoppingToken);
                }

                await Task.Delay(100, stoppingToken); // Wait before checking the queue again
            }
        }

        // Final cleanup: wait for all running tasks to complete
        if (runningTasks.Count > 0)
        {
            await Task.WhenAll(runningTasks);
        }
    }

    /// <summary>
    /// Sends a progress update to the API endpoint (for UI/monitoring).
    /// </summary>
    private async Task SendProgressUpdateAsync(string jobId, string status, int progress, int filesProcessed, DateTime? lastJobRunStarted, DateTime? lastJobRunCompleted, CancellationToken stoppingToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var notificationMessage = new NotificationMessage
            {
                JobId = jobId,
                Status = status,
                Progress = progress,
                FilesProcessed = filesProcessed,
                StartDateTime = lastJobRunStarted?.ToUniversalTime(),
                CompletedDateTime = lastJobRunCompleted?.ToUniversalTime()
            };

            var content = new StringContent(JsonSerializer.Serialize(notificationMessage), Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{_apiBaseUrl}/api/notification/send", content, stoppingToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation($"Successfully sent progress update: {status} for Job ID: {jobId}");
            }
            else
            {
                //_logger.LogError(null, $"Failed to send progress update. Status code: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while sending progress update.");
        }
    }
}
