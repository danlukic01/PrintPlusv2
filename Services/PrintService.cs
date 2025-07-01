using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommonInterfaces.Models;
using CommonInterfaces;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Channels;

namespace PrintPlusService.Services
{
    /// <summary>
    /// Represents a single print job request.
    /// </summary>
    public class PrintJob
    {
        public string PrinterName { get; set; }
        public string FilePath { get; set; }
        public string JobId { get; set; }
        public string PrintLocation { get; set; }
        // Add these if needed for callback
        public string WorkOrderId { get; set; }
        public string Sequence { get; set; }
        public string Objtyp { get; set; }
        public int FileAcount { get; set; }

        public int RetryCount { get; set; } = 0;

    }

    /// <summary>
    /// Initializes a new instance of the PrinterService class with the required dependencies.
    /// Provides logging and data access for handling printing operations.
    /// </summary>
    /// <param name="dataAccess">Interface for interacting with the database, such as retrieving printer settings and job details.</param>
    /// <param name="logger">Interface for logging messages, warnings, and errors related to printing operations.</param>

    public class PrinterService : IPrinterService, IDisposable
    {
        // Interface for database operations (printer/job settings)
        private readonly IDataAccess _dataAccess;

        // Logger for error/info output
        private readonly ILogger<PrinterService> _logger;

        // Replace BlockingCollection-based queues with Channels
        private readonly ConcurrentDictionary<string, Channel<PrintJob>> _printerChannels = new();

        // Serialize print jobs per printer 
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _printerLocks = new();

        // Tracks all running background worker tasks (one per printer)
        private readonly List<Task> _workerTasks = new();

        // Token to signal shutdown
        private readonly CancellationTokenSource _shutdownTokenSource = new();

        // True if service is being shut down; prevents new jobs from being queued
        private volatile bool _isShutdownInitiated = false;

        private CancellationToken _cts;

        private readonly PrinterStatusChecker _printerStatusChecker;

        /// <summary>
        /// Initialises a new instance of the <see cref="PrinterService"/> class.
        /// </summary>
        /// <param name="dataAccess">Data access interface for database operations.</param>
        /// <param name="logger">Logger for logging information and errors.</param>
        public PrinterService(IDataAccess dataAccess, ILogger<PrinterService> logger, IStatusReporter statusReporter)
        {
            _dataAccess = dataAccess;
            _logger = logger;
            _cts = _cts = _shutdownTokenSource.Token;
        }

        private SemaphoreSlim GetPrinterLock(string printerName)
        {
            return _printerLocks.GetOrAdd(printerName, _ => new SemaphoreSlim(1, 1));
        }

        /// <summary>
        /// Handles the printing of a specified file using the given printer and job details.
        /// Determines the appropriate printing method based on the printer settings (e.g., direct printing, printing with stapling, or printing to a queue).
        /// </summary>
        /// <param name="printerName">The name of the printer to use for printing.</param>
        /// <param name="filePath">The full path of the file to be printed.</param>
        /// <param name="jobId">The unique identifier of the print job.</param>
        /// <exception cref="Exception">Thrown if the printer or job cannot be found or an error occurs during the printing process.</exception>
   
        // Refactored PrintFileAsync to enqueue to per-printer channel
        public void PrintFileAsync(string printerName, string filePath, string jobId, string printFlag, string workOrderId, string sequence, string objtyp)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_isShutdownInitiated)
                        throw new InvalidOperationException("PrinterService is shutting down.");

                    printerName = printerName.Trim();

                    var channel = _printerChannels.GetOrAdd(printerName, pn => StartPrinterWorker(pn));

                    var job = new PrintJob
                    {
                        PrinterName = printerName,
                        FilePath = filePath,
                        JobId = jobId,
                        PrintLocation = printFlag,
                        WorkOrderId = workOrderId,
                        Sequence = sequence,
                        Objtyp = objtyp
                    };

                    await channel.Writer.WriteAsync(job);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error while enqueuing print job for {PrinterName}", printerName);
                }
            });
        }


        /// <summary>
        /// Processes and prints a single job.
        /// Determines print method based on printer settings.
        /// </summary>
        private async Task PrintFileInternalAsync(string printerName, string filePath, string jobId, string printLocation)
        {
            try
            {
                // Fetch the job early
                var job = await _dataAccess.GetJobByIdAsync(jobId);
                if (job == null)
                {
                    _logger.LogError($"Job with ID '{jobId}' not found.");
                    throw new Exception($"Job with ID '{jobId}' not found.");
                }

                // Early exit for local or special print types
                switch (printLocation?.ToUpperInvariant())
                {
                    case "L":
                    case "E":
                    case "S":
                        await LocalPrintAsync(printerName, filePath, job.Duplex);
                        return;
                }

                // Only fetch printer info if required
                var printer = await _dataAccess.GetPrinterSettingByShortNameAsync(printerName);
                if (printer == null)
                {
                    _logger.LogError($"Printer with name '{printerName}' not found.");
                    //throw new Exception($"Printer with name '{printerName}' not found.");
                }

                var printerId = printer.Id;

                if (await IsGeneratePdfOnlyAsync(printerId))
                {
                    _logger.LogInformation($"PDF only: skipping print for file '{filePath}'.");
                    return;
                }

                if (await IsDirectPrintToIpAsync(printerId))
                {
                    await DirectPrintToIpAsync(printer, filePath, job.Duplex);
                }
                else if (await IsDirectPrintWithStaplingAsync(printerId))
                {
                    await DirectPrintWithStaplingAsync(printer, filePath, job.Duplex);
                }
                else if (await IsPrintToQueueAsync(printerId))
                {
                    await PrintToQueueAsync(printer, filePath, job.Print, job.Duplex);
                }
                else
                {
                    _logger.LogWarning($"No matching print method for printer '{printer.Name}' and job '{jobId}'.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error printing '{filePath}' for job '{jobId}'.");
                throw;
            }
        }

        /// <summary>
        /// Initiates graceful shutdown of all printer workers. Waits for jobs to finish.
        /// </summary>
        public async Task ShutdownAsync()
        {
            _logger.LogInformation("Shutdown initiated...");

            _isShutdownInitiated = true;
            _shutdownTokenSource.Cancel();

            // Signal all channels to complete
            foreach (var kvp in _printerChannels)
            {
                var printerName = kvp.Key;
                _logger.LogInformation("Completing print channel for printer: {PrinterName}", printerName);
                kvp.Value.Writer.TryComplete();
            }

            // Wait for worker tasks to finish
            try
            {
                await Task.WhenAll(_workerTasks);
                _logger.LogInformation("All printer worker tasks have completed.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Shutdown was canceled during wait.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while shutting down printer workers.");
            }

            _logger.LogInformation("PrinterService shutdown complete.");
        }


        private Channel<PrintJob> StartPrinterWorker(string printerName)
        {
            var channel = Channel.CreateUnbounded<PrintJob>(new UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = true
            });

            var ct = _shutdownTokenSource.Token;

            var task = Task.Run(() => ProcessQueueAsync(channel, printerName, ct), ct);

            _workerTasks.Add(task);

            return channel;
        }

        // Adjusted ProcessQueueAsync signature
        private async Task ProcessQueueAsync(Channel<PrintJob> channel, string printerName, CancellationToken ct)
        {
            var printLock = _printerLocks.GetOrAdd(printerName, _ => new SemaphoreSlim(1, 1));

            try
            {
                await foreach (var job in channel.Reader.ReadAllAsync(ct))
                {
                    if (job == null)
                    {
                        _logger?.LogWarning("Received null job in channel for printer {PrinterName}", printerName);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(job.JobId) ||
                        string.IsNullOrWhiteSpace(job.WorkOrderId) ||
                        string.IsNullOrWhiteSpace(job.Sequence) ||
                        string.IsNullOrWhiteSpace(job.Objtyp))
                    {
                        _logger?.LogWarning("Invalid PrintJob: One or more required fields are missing. JobId={JobId}, WorkOrderId={WorkOrderId}, Sequence={Sequence}, Objtyp={Objtyp}",
                            job?.JobId, job?.WorkOrderId, job?.Sequence, job?.Objtyp);
                        continue;
                    }

                    await printLock.WaitAsync(ct);

                    try
                    {
                        // Check for printer issues before print
                        //PrinterIssue issue;

                        //int issueCheckAttempts = 0;
                        //const int maxIssueChecks = 20;

                        //do
                        //{
                        //    issue = _printerStatusChecker.CheckPrinterStatus(job.PrinterName);

                        //    if (issue == PrinterIssue.OutOfPaper || issue == PrinterIssue.Jammed)
                        //    {
                        //        _logger?.LogWarning("Printer {PrinterName} has issue: {Issue}. Pausing job {JobId} until resolved... (Attempt {Attempt})", job.PrinterName, issue, job.JobId, issueCheckAttempts + 1);
                        //        await Task.Delay(TimeSpan.FromSeconds(30), ct);
                        //        issueCheckAttempts++;
                        //    }

                        //    if (issueCheckAttempts >= maxIssueChecks)
                        //    {
                        //        _logger?.LogError("Printer {PrinterName} still has issue ({Issue}) after waiting too long. Skipping job {JobId}.", job.PrinterName, issue, job.JobId);
                        //        break;
                        //    }
                        //}
                        //while ((issue == PrinterIssue.OutOfPaper || issue == PrinterIssue.Jammed) && !ct.IsCancellationRequested);

                        //if (issue == PrinterIssue.OutOfPaper || issue == PrinterIssue.Jammed)
                        //{
                        //    continue; // skip this job for now
                        //}

                        const int maxImmediateRetries = 5;
                        const int delayBetweenRetriesMs = 5000;

                        bool success = false;

                        // Immediate retry loop
                        for (int attempt = 1; attempt <= maxImmediateRetries && !success; attempt++)
                        {
                            try
                            {
                                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                                await PrintFileInternalAsync(job.PrinterName, job.FilePath, job.JobId, job.PrintLocation);
                                success = true;

                               // _logger?.LogInformation("Print succeeded for WorkOrder {WorkOrderId} on attempt {Attempt}", job.WorkOrderId, attempt);
                            }
                            catch (OperationCanceledException ocex) when (!ct.IsCancellationRequested)
                            {
                                _logger?.LogWarning(ocex, "Print timeout on attempt {Attempt} for job {JobId} on printer {PrinterName}. Please check printer!", attempt, job.JobId, job.PrinterName);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Print failed on attempt {Attempt} for job {JobId} on printer {PrinterName}", attempt, job.JobId, job.PrinterName);
                            }

                            if (!success && attempt < maxImmediateRetries)
                            {
                                _logger?.LogInformation("Waiting {Delay}ms before retrying job {JobId} on printer {PrinterName}", delayBetweenRetriesMs, job.JobId, job.PrinterName);
                                await Task.Delay(delayBetweenRetriesMs, ct);
                            }
                        }

                        //                ////Re - enqueue happens here if still unsuccessful
                        //                //if (!success)
                        //                //{
                        //                //    job.RetryCount++;

                        //                //    if (job.RetryCount < maxTotalRetries)
                        //                //    {
                        //                //        _logger?.LogWarning("Re-enqueuing job {JobId} for retry #{RetryCount} on printer {PrinterName}", job.JobId, job.RetryCount, job.PrinterName);

                        //                //        await Task.Delay(2000, ct); // brief cooldown before requeue

                        //                //        var printerChannel = _printerChannels[job.PrinterName];
                        //                //        await printerChannel.Writer.WriteAsync(job, ct);
                        //                //    }
                        //                //    else
                        //                //    {
                        //                //        _logger?.LogError("Max retry limit reached. Giving up on job {JobId} on printer {PrinterName}", job.JobId, job.PrinterName);
                        //                //        // Optional: persist to DB or alert
                        //                //        // await _dataAccess.SaveFailedPrintJobAsync(job);
                        //                //    }
                        //                //}

                        if (!success)
                        {
                            _logger?.LogError("All retries failed for job {JobId} on printer {PrinterName}", job.JobId, job.PrinterName);
                        }
                    }
                    finally
                    {
                        printLock.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger?.LogInformation("Print channel processing was cancelled for printer {PrinterName}", printerName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error while processing the print job channel for printer {PrinterName}", printerName);
            }
        }

        private async Task LocalPrintAsync(string printer, string filePath, string duplex)
        {
            try
            {
                //_logger.LogInformation("Printing to the local printer queue.");
                SendFileToPrinter(printer, filePath, false, duplex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in PrintToQueueAsync: {ex.Message}");
                throw;
            }
            
        }

        /// <summary>
        /// Checks whether the printer settings indicate that only a PDF should be generated, without actual printing.
        /// </summary>
        /// <param name="printerId">The unique identifier of the printer.</param>
        /// <returns>True if only PDF generation is required; otherwise, false.</returns>

        private async Task<bool> IsGeneratePdfOnlyAsync(int printerId)
        {
            var printerSettings = await _dataAccess.GetPrinterSettingByIdAsync(printerId);
            return printerSettings.GenMF && !printerSettings.PrintMF && !printerSettings.DirectPrint;
        }

        /// <summary>
        /// Determines if the printer is configured for direct printing to its IP address.
        /// </summary>
        /// <param name="printerId">The unique identifier of the printer.</param>
        /// <returns>True if the printer supports direct IP printing; otherwise, false.</returns>

        private async Task<bool> IsDirectPrintToIpAsync(int printerId)
        {
            var printerSettings = await _dataAccess.GetPrinterSettingByIdAsync(printerId);
            return printerSettings.DirectPrint && printerSettings.GeneratePRN && printerSettings.GenMF && printerSettings.PrintMF;
        }

        /// <summary>
        /// Checks if the printer settings support direct IP printing with stapling enabled.
        /// </summary>
        /// <param name="printerId">The unique identifier of the printer.</param>
        /// <returns>True if the printer supports stapling during direct IP printing; otherwise, false.</returns>

        private async Task<bool> IsDirectPrintWithStaplingAsync(int printerId)
        {
            var printerSettings = await _dataAccess.GetPrinterSettingByIdAsync(printerId);
            return printerSettings.DirectPrint && printerSettings.GeneratePRN && printerSettings.GenMF && !printerSettings.PrintMF && printerSettings.Staple;
        }

        private async Task<bool> IsPrintToQueueAsync(int printerId)
        {
            var printerSettings = await _dataAccess.GetPrinterSettingByIdAsync(printerId);
            return printerSettings.PrintMF || !printerSettings.PrintMF;
        }

        /// <summary>
        /// Checks if the printer settings allow printing to a queue for further processing.
        /// </summary>
        /// <param name="printerId">The unique identifier of the printer.</param>
        /// <returns>True if printing to a queue is supported; otherwise, false.</returns>

        private async Task DirectPrintToIpAsync(PrinterSetting printer, string filePath, string duplex)
        {
            try
            {
                _logger.LogInformation("Printing directly to printer via IP.");
                if (printer.AltPrintPRN)
                {
                    await PrintWithAltPrintAsync(printer.PrintQueue, filePath);
                }
                else
                {
                    SendFileToPrinter(printer.PrintQueue, filePath, false, duplex);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in DirectPrintToIpAsync: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Prints a file directly to the printer with stapling enabled, using the specified duplex setting.
        /// Optionally supports alternative printing mechanisms based on printer configuration.
        /// </summary>
        /// <param name="printer">The printer settings retrieved from the database.</param>
        /// <param name="filePath">The path to the file to be printed.</param>
        /// <param name="duplex">The duplex setting for the print job (e.g., simplex or duplex).</param>
        /// <exception cref="Exception">Thrown if an error occurs during the direct printing process with stapling.</exception>
        private async Task DirectPrintWithStaplingAsync(PrinterSetting printer, string filePath, string duplex)
        {
            try
            {
                _logger.LogInformation("Printing directly to printer with stapling via IP.");
                if (printer.AltPrintPRN)
                {
                    await PrintWithAltPrintAsync(printer.PrintQueue, filePath);
                }
                else
                {
                    SendFileToPrinter(printer.PrintQueue, filePath, true, duplex);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in DirectPrintWithStaplingAsync: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Sends a file to the printer's queue for processing, using the specified print type and duplex setting.
        /// </summary>
        /// <param name="printer">The printer settings retrieved from the database.</param>
        /// <param name="filePath">The path to the file to be printed.</param>
        /// <param name="printType">The type of print job (e.g., standard or custom).</param>
        /// <param name="duplex">The duplex setting for the print job (e.g., simplex or duplex).</param>
        /// <exception cref="Exception">Thrown if an error occurs while adding the file to the print queue.</exception>

        private async Task PrintToQueueAsync(PrinterSetting printer, string filePath, string printType, string duplex)
        {
            try
            {
               // _logger.LogInformation("Sending print job to printer queue for processing.");
                await SendFileToPrinter(printer.PrintQueue, filePath, false, duplex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in PrintToQueueAsync: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Uses an alternate printing method (e.g., LibreOffice) to print a file to a specified print queue.
        /// Executes the printing process as a separate system command.
        /// </summary>
        /// <param name="printQueue">The name of the print queue to send the file to.</param>
        /// <param name="filePath">The path to the file to be printed.</param>
        /// <exception cref="Exception">Thrown if the alternate printing process fails.</exception>

        private async Task PrintWithAltPrintAsync(string printQueue, string filePath)
        {
            try
            {
                _logger.LogInformation("Using AltPrint for printing.");
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = @"C:\Program Files\LibreOffice\program\soffice.exe",
                    Arguments = $" \"-env:UserInstallation=file:///{Path.GetDirectoryName(filePath).Replace(@"\", @"/")}/p{new Random().Next(0, 50)}/\" --headless --pt {printQueue} {filePath}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = processStartInfo })
                {
                    process.Start();
                    string result = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        throw new Exception($"Error printing file: {error}");
                    }

                    _logger.LogInformation($"AltPrint command output: {result}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in PrintWithAltPrintAsync: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Sends a file to the specified printer using platform-specific printing methods (Windows or Linux).
        /// </summary>
        /// <param name="printQueue">The name of the print queue to send the file to.</param>
        /// <param name="filePath">The path to the file to be printed.</param>
        /// <param name="staple">Indicates whether stapling is required for the print job.</param>
        /// <param name="duplex">The duplex setting for the print job (e.g., simplex or duplex).</param>
        /// <exception cref="PlatformNotSupportedException">Thrown if the platform is not supported for printing.</exception>

        protected virtual async Task SendFileToPrinter(string printQueue, string filePath, bool staple, string duplex)
        {
            try
            {
                //#if WINDOWS
                if (OperatingSystem.IsWindows())
                    await PrintFileOnWindows(printQueue, filePath, staple, duplex);
                //#elif LINUX
                //else
                //    throw new PlatformNotSupportedException("Printing is only supported on Windows and Linux.");

                if (OperatingSystem.IsLinux())
                    PrintFileOnLinux(printQueue, filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending file to Printer: {ex.Message}");
                throw;
            }
            //else
            //    throw new PlatformNotSupportedException("Printing is only supported on Windows and Linux.");
            //#else
            //throw new PlatformNotSupportedException("Printing is only supported on Windows and Linux.");
            //#endif
        }

        /// <summary>
        /// Handles the printing of a file on a Windows system, with support for duplex and stapling settings.
        /// </summary>
        /// <param name="printQueue">The name of the print queue to send the file to.</param>
        /// <param name="filePath">The path to the file to be printed.</param>
        /// <param name="staple">Indicates whether stapling is required for the print job.</param>
        /// <param name="duplex">The duplex setting for the print job (e.g., simplex or duplex).</param>
        /// <exception cref="FileNotFoundException">Thrown if the specified file does not exist.</exception>
        /// <exception cref="Exception">Thrown if an error occurs during the printing process.</exception>

        [SupportedOSPlatform("windows")]
        private async Task PrintFileOnWindows(string printQueue, string filePath, bool staple, string duplex)
        {
            //var printLock = GetPrinterLock(printQueue);
            //await printLock.WaitAsync(_cts);

            try
            {
                if (!File.Exists(filePath))
                    throw new FileNotFoundException("File not found", filePath);

                if (staple)
                    AddStaples(filePath, duplex == "D");

                string duplexOption = duplex == "D" ? "duplex" : "";
                string arguments = $"-print-to \"{printQueue}\" -silent -print-settings \"{duplexOption}\" \"{filePath}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = @"C:\PM11\Tools\SumatraPDF.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);

                if (process == null)
                {
                    _logger?.LogError("Failed to start SumatraPDF for {FilePath}", filePath);
                    return;
                }

                await process.WaitForExitAsync(_cts);
   
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error printing file {filePath} to printer {printQueue}");
                throw;
            }
            finally
            {
                //printLock.Release();
            }
        }

        /// <summary>
        /// Handles the printing of a file on a Linux system, using the "lp" command to send the file to the printer.
        /// </summary>
        /// <param name="printQueue">The name of the print queue to send the file to.</param>
        /// <param name="filePath">The path to the file to be printed.</param>
        /// <exception cref="FileNotFoundException">Thrown if the specified file does not exist.</exception>
        /// <exception cref="Exception">Thrown if an error occurs during the printing process.</exception>
        private void PrintFileOnLinux(string printQueue, string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException("File not found", filePath);
                }

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = "lp",
                    Arguments = $"-d {printQueue} \"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = processStartInfo })
                {
                    process.Start();
                    string result = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        throw new Exception($"Error printing file: {error}");
                    }

                    _logger.LogInformation($"lp command output: {result}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in PrintFileOnLinux: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Adds stapling commands to a print job file, modifying the PCL or PJL data to enable stapling and duplex settings.
        /// </summary>
        /// <param name="prnfile">The path to the print job file.</param>
        /// <param name="duplex">Indicates whether duplex printing is enabled.</param>
        /// <exception cref="Exception">Thrown if an error occurs while modifying the print job file for stapling.</exception>

        private void AddStaples(string prnfile, bool duplex)
        {
            _logger.LogInformation("PR AddStaples: Started Staple Config");

            try
            {
                if (File.Exists(prnfile))
                {
                    string tempnm = $"{Path.GetDirectoryName(prnfile)}~{Path.GetFileName(prnfile)}";
                    string line;

                    if (File.Exists(tempnm))
                    {
                        File.Delete(tempnm);
                    }

                    File.Move(prnfile, tempnm);

                    byte[] binorg = File.ReadAllBytes(tempnm);
                    byte[] firstpart = null;
                    byte[] lastpart = null;
                    StringBuilder strbuild1 = new StringBuilder();
                    StringBuilder strbuild2 = new StringBuilder();
                    List<string> startPJL = new List<string>();
                    List<string> endPJL = new List<string>();

                    using (StreamReader reader = new StreamReader(tempnm, Encoding.Default))
                    {
                        bool first = true;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (first)
                            {
                                startPJL.Add(line);
                                strbuild1.AppendFormat($"{line}\n");
                            }
                            else
                            {
                                if (line.StartsWith("@PJL COMMENT FXJOBINFO") || line.StartsWith("@PJL EOJ"))
                                {
                                    endPJL.Add(line);
                                    strbuild2.Append(line);
                                    strbuild2.Append(Environment.NewLine);
                                }
                            }
                            if (line.StartsWith("@PJL ENTER LANGUAGE=PCLXL"))
                            {
                                first = false;
                            }
                        }
                        firstpart = Encoding.ASCII.GetBytes(strbuild1.ToString().TrimEnd(Environment.NewLine.ToCharArray()));
                        lastpart = Encoding.ASCII.GetBytes(strbuild2.ToString().TrimEnd(Environment.NewLine.ToCharArray()));
                    }

                    int midlen = binorg.Length - lastpart.Length - firstpart.Length - 10;
                    byte[] orgMid = new byte[midlen];
                    byte[] tester = new byte[6];

                    string teststr = "0";

                    while (teststr != "12345X")
                    {
                        orgMid = new byte[midlen];
                        Array.Copy(binorg, firstpart.Length, orgMid, 0, midlen);
                        Array.Copy(orgMid, orgMid.Length - 6, tester, 0, 6);
                        teststr = Encoding.ASCII.GetString(tester);
                        midlen++;
                    }

                    StringBuilder sfinal = new StringBuilder();
                    StringBuilder efinal = new StringBuilder();

                    foreach (string nline in startPJL)
                    {
                        if (nline.Contains("@PJL SET DUPLEX=OFF") && duplex)
                        {
                            _logger.LogInformation($"Duplex {duplex}");
                            sfinal.Append("@PJL SET DUPLEX=ON\n");
                        }
                        else if (nline.Contains("@PJL SET FINISH=NONE"))
                        {
                            _logger.LogInformation("Finisher On");
                            sfinal.Append("@PJL SET FINISH=ON\n");
                        }
                        else if (nline.Contains("@PJL SET STAPLE=NONE"))
                        {
                            _logger.LogInformation("Staple On");
                            sfinal.Append("@PJL SET STAPLE=TOPLEFT\n");
                            sfinal.Append("@PJL SET JOBATTR=@STLL=STAPLE\n");
                        }
                        else if (nline.Contains("@PJL SET JOBATTR=") && nline.Contains("@LUNA="))
                        {
                            sfinal.Append("@PJL SET JOBATTR=\"@LUNA=SAP Print Server\"\n");
                        }
                        else if (nline.Contains("@PJL SET JOBATTR=") && nline.Contains("@ACNA="))
                        {
                            sfinal.Append($"@PJL SET JOBATTR=\"@ACNA={Path.GetFileName(prnfile)}\"\n");
                        }
                        else if (nline.Contains("@PJL SET JOBATTR=") && nline.Contains("@BANR="))
                        {
                            sfinal.Append("@PJL SET JOBATTR=\"@BANR=OFF\"\n");
                        }
                        else
                        {
                            if (startPJL.IndexOf(nline) == startPJL.Count - 1)
                            {
                                sfinal.Append("@PJL COMMENT Prepared by PM11 Print Solution\n");

                                if (!sfinal.ToString().Contains("DUPLEX") && duplex)
                                {
                                    sfinal.Append("@PJL SET DUPLEX=ON\n");
                                    sfinal.Append("@PJL SET BINDING=LONGEDGE\n");
                                }

                                if (sfinal.ToString().Contains("FINISH=ON") && !sfinal.ToString().Contains("STAPLE"))
                                {
                                    sfinal.Append("@PJL SET STAPLE=TOPLEFT\n");
                                    sfinal.Append("@PJL SET JOBATTR=@STLL=STAPLE\n");
                                }
                            }
                            sfinal.AppendFormat($"{nline}\n");
                        }
                    }

                    foreach (string nline in endPJL)
                    {
                        if (nline.Contains("@PJL COMMENT FXJOBINFO DUPLEXTYPE=SIMPLEX") && duplex)
                        {
                            _logger.LogInformation($"Duplex {duplex}");
                            efinal.Append("@PJL COMMENT FXJOBINFO DUPLEXTYPE=DUPLEX\n");
                        }
                        else if (nline.Contains("@PJL COMMENT FXJOBINFO STAPLE=OFF"))
                        {
                            _logger.LogInformation("Staple On");
                            efinal.Append("@PJL COMMENT FXJOBINFO STAPLE=ON\n");
                            efinal.Append("@PJL COMMENT FXJOBINFO STAPLEEX=TOPLEFT\n");
                        }
                        else if (nline.Contains("DUPLEX[SIMPLEX]") && duplex)
                        {
                            efinal.AppendFormat($"{nline.Replace("SIMPLEX", "DUPLEX")}\n");
                        }
                        else
                        {
                            efinal.AppendFormat($"{nline}\n");
                        }
                    }

                    firstpart = Encoding.ASCII.GetBytes(sfinal.ToString().TrimEnd(Environment.NewLine.ToCharArray()));
                    lastpart = Encoding.ASCII.GetBytes(efinal.ToString().TrimEnd(Environment.NewLine.ToCharArray()));

                    using (BinaryWriter writer = new BinaryWriter(File.Open(prnfile, FileMode.Create)))
                    {
                        writer.Write(firstpart);
                        writer.Write(orgMid);
                        writer.Write(lastpart);
                    }

                    File.Delete(tempnm);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"PR AddStaples: Error processing staples for file: {prnfile}");
                throw;
            }
        }

        /// <summary>
        /// Dispose pattern implementation for releasing resources.
        /// Ensures all workers are stopped gracefully.
        /// </summary>
        /// </summary>
        public void Dispose()
        {
            if (!_isShutdownInitiated)
                ShutdownAsync().GetAwaiter().GetResult();
            _shutdownTokenSource.Dispose();
        }

    }
}
