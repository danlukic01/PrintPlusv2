using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommonInterfaces.Models;
using CommonInterfaces;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

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

        // Dictionary: one print job queue per printer name
        private readonly ConcurrentDictionary<string, BlockingCollection<PrintJob>> _printerQueues = new();

        // Tracks all running background worker tasks (one per printer)
        private readonly List<Task> _workerTasks = new();

        // Token to signal shutdown
        private readonly CancellationTokenSource _shutdownTokenSource = new();

        // True if service is being shut down; prevents new jobs from being queued
        private volatile bool _isShutdownInitiated = false;

        // Flag to where the file will be sent for printing L=Local N=Network printer
        private string _printLocation = string.Empty;

        //static PdfDocument pdfDocument;
        //static int currentPage = 0; // Track which page is being printed

        //static List<Image> pdfImages = new List<Image>();
        //static int currentPage = 0; // Track the current page

        /// <summary>
        /// Initialises a new instance of the <see cref="PrinterService"/> class.
        /// </summary>
        /// <param name="dataAccess">Data access interface for database operations.</param>
        /// <param name="logger">Logger for logging information and errors.</param>
        public PrinterService(IDataAccess dataAccess, ILogger<PrinterService> logger)
        {
            _dataAccess = dataAccess;
            _logger = logger;
        }

        /// <summary>
        /// Handles the printing of a specified file using the given printer and job details.
        /// Determines the appropriate printing method based on the printer settings (e.g., direct printing, printing with stapling, or printing to a queue).
        /// </summary>
        /// <param name="printerName">The name of the printer to use for printing.</param>
        /// <param name="filePath">The full path of the file to be printed.</param>
        /// <param name="jobId">The unique identifier of the print job.</param>
        /// <exception cref="Exception">Thrown if the printer or job cannot be found or an error occurs during the printing process.</exception>


        //   public async Task PrintFileAsync(
        //IEnumerable<string> printerNames,
        //string filePath,
        //string jobId)
        //   {
        //       var tasks = printerNames
        //           .Select(name => PrintFileAsync(name, filePath, jobId));
        //       await Task.WhenAll(tasks);
        //   }

        public async Task PrintFileAsync(string printerName, string filePath, string jobId, string printFlag)
        {
            try
            {
                // Fetch the printer settings

                if (_isShutdownInitiated)
                    throw new InvalidOperationException("PrinterService is shutting down. No new jobs can be queued.");

                // Get or create queue/worker for this printer
                var queue = _printerQueues.GetOrAdd(printerName, pn => StartPrinterWorker(pn));

                queue.Add(new PrintJob
                {
                    PrinterName = printerName,
                    FilePath = filePath,
                    JobId = jobId,
                    PrintLocation = printFlag
                });
        
                await Task.CompletedTask; // To preserve async signature

                //if (printFlag == "N")
                //{
                //    printer = await _dataAccess.GetPrinterSettingByShortNameAsync(printerName);

                //    if (printer == null)
                //    {
                //        _logger.LogError($"Printer with name {printerName} not found.");
                //        throw new Exception($"Printer with name {printerName} not found.");
                //    }
                //}

                //// Fetch the job details by jobId instead of filePath
                //var job = await _dataAccess.GetJobByIdAsync(jobId);  // Use jobId instead of filePath

                //if (job == null)
                //{
                //    _logger.LogError($"Job with ID {jobId} not found.");
                //    throw new Exception($"Job with ID {jobId} not found.");
                //}

                //// If the Print flag is L, do a local print and exit
                //if (string.Equals(printFlag, "L", StringComparison.OrdinalIgnoreCase))
                //{
                //    await LocalPrintAsync(printerName, filePath, job.Duplex);
                   
                //    return;
                //}

                //// Determine the print method based on the printer settings
                //if (await IsGeneratePdfOnlyAsync(printer.Id))
                //{
                //    _logger.LogInformation($"PDF already generated for file: {filePath}, no printing required.");
                //    return;
                //}
                //else if (await IsDirectPrintToIpAsync(printer.Id))
                //{
                //    await DirectPrintToIpAsync(printer, filePath, job.Duplex);
                //}
                //else if (await IsDirectPrintWithStaplingAsync(printer.Id))
                //{
                //    await DirectPrintWithStaplingAsync(printer, filePath, job.Duplex);
                //}
                //else if (await IsPrintToQueueAsync(printer.Id))
                //{
                //    await PrintToQueueAsync(printer, filePath, job.Print, job.Duplex);
                //}
                //else
                //{
                //    _logger.LogWarning($"No valid print method found for printer {printer.Name} and job {jobId}.");
                //}
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"An error occurred while printing file: {filePath} for job: {jobId}");
                throw;
            }
        }

        /// <summary>
        /// Processes and prints a single job.
        /// Determines print method based on printer settings.
        /// </summary>
        private async Task PrintFileInternalAsync(string printerName, string filePath, string jobId, string printLocation)
        {
            try
            {
                PrinterSetting printer = null;

                if (printLocation == "N")
                {
                    printer = await _dataAccess.GetPrinterSettingByShortNameAsync(printerName);

                    if (printer == null)
                    {
                        _logger.LogError($"Printer with name {printerName} not found.");
                        throw new Exception($"Printer with name {printerName} not found.");
                    }
                }

                // Fetch the job details by jobId instead of filePath
                var job = await _dataAccess.GetJobByIdAsync(jobId);  // Use jobId instead of filePath

                if (job == null)
                {
                    _logger.LogError($"Job with ID {jobId} not found.");
                    throw new Exception($"Job with ID {jobId} not found.");
                }

                // If the Print flag is L, do a local print and exit
                if (string.Equals(printLocation, "L", StringComparison.OrdinalIgnoreCase) || 
                    string.Equals(printLocation, "E", StringComparison.OrdinalIgnoreCase) || 
                    string.Equals(printLocation, "S", StringComparison.OrdinalIgnoreCase))
                {
                    await LocalPrintAsync(printerName, filePath, job.Duplex);

                    return;
                }

                // Determine the print method based on the printer settings
                if (await IsGeneratePdfOnlyAsync(printer.Id))
                {
                    _logger.LogInformation($"PDF already generated for file: {filePath}, no printing required.");
                    return;
                }
                else if (await IsDirectPrintToIpAsync(printer.Id))
                {
                    await DirectPrintToIpAsync(printer, filePath, job.Duplex);
                }
                else if (await IsDirectPrintWithStaplingAsync(printer.Id))
                {
                    await DirectPrintWithStaplingAsync(printer, filePath, job.Duplex);
                }
                else if (await IsPrintToQueueAsync(printer.Id))
                {
                    await PrintToQueueAsync(printer, filePath, job.Print, job.Duplex);
                }
                else
                {
                    _logger.LogWarning($"No valid print method found for printer {printer.Name} and job {jobId}.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"An error occurred while printing file: {filePath} for job: {jobId}");
                throw;
            }
        }

        /// <summary>
        /// Initiates graceful shutdown of all printer workers. Waits for jobs to finish.
        /// </summary>
        public async Task ShutdownAsync()
        {
            _isShutdownInitiated = true;
            _shutdownTokenSource.Cancel();

            // Mark all queues as "complete for adding", so worker tasks can finish
            foreach (var queue in _printerQueues.Values)
                queue.CompleteAdding();

            // Wait for all workers to finish
            try
            {
                await Task.WhenAll(_workerTasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while shutting down printer workers.");
            }
        }


        /// <summary>
        /// Starts a background worker for a printer's job queue.
        /// The worker will process jobs from the queue until shutdown is signaled.
        /// </summary>
        private BlockingCollection<PrintJob> StartPrinterWorker(string printerName)
        {
            var queue = new BlockingCollection<PrintJob>();
            var ct = _shutdownTokenSource.Token;

            // Worker task that runs as long as not cancelled
            var workerTask = Task.Run(async () =>
            {
                try
                {
                    // Take jobs from the queue and process them
                    foreach (var job in queue.GetConsumingEnumerable(ct))
                    {
                        try
                        {
                            await PrintFileInternalAsync(job.PrinterName, job.FilePath, job.JobId, job.PrintLocation);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error printing job {job.JobId} on printer {job.PrinterName}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown, ignore
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Background worker for printer {printerName} exited with error.");
                }
            }, ct);

            _workerTasks.Add(workerTask);
            return queue;
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
                _logger.LogInformation("Printing to the printer queue.");
                SendFileToPrinter(printer.PrintQueue, filePath, false, duplex);
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

        protected virtual void SendFileToPrinter(string printQueue, string filePath, bool staple, string duplex)
        {
            try
            {
                //#if WINDOWS
                if (OperatingSystem.IsWindows())
                    PrintFileOnWindows(printQueue, filePath, staple, duplex);
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
        private void PrintFileOnWindows(string printQueue, string filePath, bool staple, string duplex)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException("File not found", filePath);
                }

                //PrintDocument printDocument = new PrintDocument
                //{
                //    //PrinterSettings = { PrinterName = "PR02" }
                //    PrinterSettings = { PrinterName = printQueue }
                //};

                //printDocument.PrintPage += (sender, e) =>
                //{
                //    using (StreamReader reader = new StreamReader(filePath))
                //    {
                //        string fileContent = reader.ReadToEnd();
                //        e.Graphics.DrawString(fileContent, new Font("Arial", 10), Brushes.Black, new RectangleF(0, 0, printDocument.DefaultPageSettings.PrintableArea.Width, printDocument.DefaultPageSettings.PrintableArea.Height));
                //    }

                //    if (staple)
                //    {
                //        AddStaples(filePath, duplex == "D");
                //    }
                //};

                //printDocument.PrinterSettings.Duplex = duplex == "D" ? Duplex.Vertical : Duplex.Simplex;
                //printDocument.Print();

                //Load the PDF document
                //pdfDocument = PdfDocument.Load(filePath);

                //using (PrintDocument printDocument = new PrintDocument())
                //{
                //    printDocument.PrinterSettings = new PrinterSettings
                //    {
                //        PrinterName = printQueue,
                //    };

                //    if (staple)
                //    {
                //        AddStaples(filePath, duplex == "D");
                //    }

                //    printDocument.PrinterSettings.Duplex = duplex == "D" ? Duplex.Vertical : Duplex.Simplex;

                //    // Attach the PrintPage event handler
                //    printDocument.PrintPage += PrintDocument_PrintPage;

                //    // Start printing
                //    printDocument.Print();
                //}

                //pdfDocument.Dispose();

                if (staple)
                {
                    AddStaples(filePath, duplex == "D");
                }

                // Handle duplex mode
                string duplexOption = duplex == "D" ? "duplex" : "";
                string printerName = printQueue; // Set your printer name
                string pdfFilePath = filePath;

                
                //string arguments = $"-print-to \"{printerName}\" -silent -print-settings\"{duplexOption}\" -readonly \"{pdfFilePath}\"";
                string arguments = $"-print-to \"{printerName}\" -silent -print-settings \"{duplexOption}\" \"{pdfFilePath}\"";
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = @"C:\PM11\Tools\SumatraPDF.exe", // Path to SumatraPDF
                    Arguments = arguments,
                    UseShellExecute = true,
                    CreateNoWindow = true
                };

                try
                {
                    Task.Run(() =>
                    {
                        try
                        {
                            using (Process process = Process.Start(psi))
                            {
                                process.WaitForExitAsync();
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Silent Printing Error in background task: {ex.Message}");
                        }
                    });

                    // Continue to next operation without waiting
                    //_logger.LogInformation("Print command sent, continuing to next task.");
                }
                catch (System.Exception ex)
                {
                    _logger.LogError("Silent Printing Error: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in PrintFileOnWindows: {ex.Message}");
                throw;
            }
        }

        //[SupportedOSPlatform("windows")]
        //private static void PrintDocument_PrintPage(object sender, PrintPageEventArgs e)
        //{
        //    if (currentPage >= pdfDocument.Pages.Count)
        //    {
        //        e.HasMorePages = false;
        //        return;
        //    }

        //    //float scale = 100f / 72f; // Convert PDF points (72 DPI) to target DPI

        //    //// Calculate scaled dimensions
        //    //int width = (int)(e.PageBounds.Width * scale);
        //    //int height = (int)(e.PageBounds.Height * scale);

        //    // Create high-quality bitmap
        //    using (var bitmap = new Bitmap(e.PageBounds.Width, e.PageBounds.Height, PixelFormat.Format32bppArgb))
        //    {
        //        using (Graphics g = Graphics.FromImage(bitmap))
        //        {
        //            g.Clear(Color.White); // Set background color

        //            // Enable high-quality rendering settings
        //            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        //            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        //            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

        //            // Render PDF to Graphics (ensure HDC is released)
        //            IntPtr hdc = g.GetHdc();
        //            try
        //            {
        //                pdfDocument.Pages[currentPage].Render(hdc, 0, 0, e.PageBounds.Width, e.PageBounds.Height, PageRotate.Normal, 
        //                    RenderFlags.FPDF_NO_NATIVETEXT);
        //            }
        //            finally
        //            {
        //                g.ReleaseHdc(hdc);
        //            }
        //        }

        //        // Draw the high-quality bitmap onto the printer graphics
        //        e.Graphics.DrawImage(bitmap, new RectangleF(0, 0, e.PageBounds.Width, e.PageBounds.Height));
        //    }

        //    // Move to next page
        //    currentPage++;
        //    e.HasMorePages = currentPage < pdfDocument.Pages.Count;
        //}

   
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
        public void Dispose()
        {
            if (!_isShutdownInitiated)
                ShutdownAsync().GetAwaiter().GetResult();
            _shutdownTokenSource.Dispose();
        }

    }
}
