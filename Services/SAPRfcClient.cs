using CommonInterfaces;
using CommonInterfaces.Models;
using DocumentFormat.OpenXml.Drawing;
using Microsoft.Extensions.DependencyInjection;
using NwRfcNet;
using NwRfcNet.TypeMapper;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


/// <summary>
/// Initializes a new instance of the SAPRfcClient class.
/// Sets up logging, data access, and prepares a static RFC mapper for mapping SAP structures.
/// </summary>
/// <param name="logger">Service for logging information and errors.</param>
/// <param name="serviceScopeFactory">Factory for creating service scopes, if needed for dependency injection.</param>
/// <param name="dataAccess">Service for accessing database configurations and SAP settings.</param>


namespace PrintPlusService.Services
{
    public class SAPRfcClient : ISAPRfcClient
    {
        private readonly ILoggerService _logger;
        private readonly IDataAccess _dataAccess;
        private static readonly RfcMapper _mapper = new RfcMapper();

        /// <summary>
        /// Configures the static RfcMapper with mappings between application models and SAP RFC parameters.
        /// Ensures proper type, length, and parameter naming conventions for RFC communication.
        /// </summary>

        static SAPRfcClient()
        {
            _mapper.Parameter<ReceiveStatusIt>()
                .Property(x => x.ItReceiveStatus)
                .HasParameterName("IT_RECEIVE_STATUS")
                .HasParameterType(RfcFieldType.Table);

            _mapper.Parameter<ReceiveStatusIt>()
                .Property(x => x.IvWo2Pdf2Job)
                .HasParameterName("IV_WO2PDF2_JOB")
                .HasParameterType(RfcFieldType.Char)
                .MaxLength(10);

            _mapper.Parameter<ReceiveStatusIt>()
                .Property(x => x.IvPrint)
                .HasParameterName("IV_PRINT")
                .HasParameterType(RfcFieldType.Char)
                .MaxLength(1);

            _mapper.Parameter<Wo2Pdf2ReceiveStatusI>()
                .Property(x => x.Aufnr)
                .HasParameterName("AUFNR")
                .HasParameterType(RfcFieldType.Char)
                .MaxLength(20);

            _mapper.Parameter<Wo2Pdf2ReceiveStatusI>()
                .Property(x => x.Vornr)
                .HasParameterName("VORNR")
                .HasParameterType(RfcFieldType.Char)
                .MaxLength(4);

            _mapper.Parameter<Wo2Pdf2ReceiveStatusI>()
                .Property(x => x.Objtyp)
                .HasParameterName("OBJTYP")
                .HasParameterType(RfcFieldType.Char)
                .MaxLength(10);

            _mapper.Parameter<Wo2Pdf2ReceiveStatusI>()
                .Property(x => x.Wo2Pdf2Url)
                .HasParameterName("WO2PDF2_URL")
                .HasParameterType(RfcFieldType.String)
                .MaxLength(255);

            _mapper.Parameter<Wo2Pdf2ReceiveStatusI>()
                .Property(x => x.Msgty)
                .HasParameterName("MSGTY")
                .HasParameterType(RfcFieldType.Char)
                .MaxLength(1);

            _mapper.Parameter<Wo2Pdf2ReceiveStatusI>()
                .Property(x => x.Msgnr)
                .HasParameterName("MSGNR")
                .HasParameterType(RfcFieldType.Char)
                .MaxLength(4);

            // Add the parameter for the byte size (file length)
            _mapper.Parameter<Wo2Pdf2ReceiveStatusI>()
                .Property(x => x.Msgv1)
                .HasParameterName("MSGV1")
                .HasParameterType(RfcFieldType.Char)
                .MaxLength(50);
        }

        public SAPRfcClient(ILoggerService logger, IServiceScopeFactory serviceScopeFactory, IDataAccess dataAccess)
        {
            _logger = logger;
            _dataAccess = dataAccess;
        }

        /// <summary>
        /// Updates the status of a job or work order in SAP using the RFC interface.
        /// Constructs file paths, validates file readiness, and sends the status information to SAP.
        /// </summary>
        /// <param name="jobInfo">An object containing details about the job, such as Job ID, work order ID, and sequence number.</param>
        /// <param name="cancellationToken">Token for handling task cancellation requests.</param>
        /// <exception cref="Exception">Logs errors during file preparation or SAP RFC communication.</exception>
        public async Task UpdateStatusAsync(JobInfo jobInfo, CancellationToken cancellationToken)
        {
            //_logger.LogInformation($"Preparing to update SAP status for JobId={jobInfo.JobId}, Wo={jobInfo.Wo}");

            try
            {
                // Fetch all config/settings in parallel
                var rfcTask = _dataAccess.GetRFCSettingsAsync();
                var envTask = _dataAccess.GetConfigurationSettingAsync("AppSettings", "Environment");
                var folderTask = _dataAccess.GetSapFolderSettingsAsync();


                await Task.WhenAll(rfcTask, envTask, folderTask);

                var rfcSettings = rfcTask.Result;
                var environmentSetting = envTask.Result;
                var sapFolderSettings = folderTask.Result;

                if (rfcSettings == null || rfcSettings.Count == 0)
                {
                    _logger.LogError(null, "No RFC settings found in the database.");
                    return;
                }

                var settings = rfcSettings.FirstOrDefault();
                if (settings == null)
                {
                    _logger.LogError(null, "Could not retrieve valid RFC settings from the database.");
                    return;
                }

                if (environmentSetting == null)
                {
                    _logger.LogError(null, "Environment configuration not found.");
                    return;
                }

                var sapFolder = sapFolderSettings.FirstOrDefault(s => s.Name == environmentSetting.Value);
                if (sapFolder == null)
                {
                    _logger.LogError(null, $"No SAP folder settings found for environment {environmentSetting.Value}.");
                    return;
                }

                string paddedJobId = jobInfo.JobId.PadLeft(10, '0');
                string paddedWo = jobInfo.Wo.PadLeft(12, '0');
                string environmentFolder = environmentSetting.Value;
                string outPath = string.Empty;
               

                string effectiveStatus = jobInfo.Msg == "009"
                    ? (jobInfo.Wo.Length < 5 ? "007" : "037")
                    : jobInfo.Msg;

                // Determine file path
                string filePath = effectiveStatus switch
                {
                    "037" => System.IO.Path.Combine(sapFolder.Path, $"{environmentFolder}\\Out\\{paddedJobId}\\{paddedWo}.pdf"),
                    "007" => System.IO.Path.Combine(sapFolder.Path, $"{environmentFolder}\\Out\\{paddedJobId}\\{paddedJobId}.pdf"),
                    "009" => jobInfo.File,
                    _ => null
                };

                if (effectiveStatus == "037")
                    outPath = $"{sapFolder.SapPath}/{environmentFolder}/Out/{paddedJobId}/{paddedWo}.pdf";
                else if (effectiveStatus == "007")
                    outPath = $"{sapFolder.SapPath}/{environmentFolder}/Out/{paddedJobId}/{paddedJobId}.pdf";


                if (string.IsNullOrWhiteSpace(filePath))
                {
                    _logger.LogWarning($"Unknown status code {jobInfo.Msg} for JobId={jobInfo.JobId}. No valid file path generated.");
                    return;
                }

                if (!await WaitForFileExistsAsync(filePath, cancellationToken))
                {
                    _logger.LogError(null, $"File is not ready or locked after retries: {filePath}");
                    return;
                }

                var fileInfo = new FileInfo(filePath);
                File.SetAttributes(filePath, FileAttributes.Normal);
                long fileSizeBytes = fileInfo.Length;

                _logger.LogInformation($"File size (bytes): {fileSizeBytes} for {filePath}");

                using (var connection = new RfcConnection(new RfcConnectionParameterBuilder()
                    .UseConnectionHost(settings.MSHOST)
                    .UseLogonUserName(settings.User)
                    .UseLogonPassword(settings.Password)
                    .UseLogonClient(settings.CLIENT.ToString())
                    .UseLogonLanguage(settings.LogonLang)
                    .UseSystemNumber(settings.SYSNR.ToString())))
                {
                    connection.Open();
                    //_logger.LogInformation("Successfully connected to SAP.");

                    using (var rfcFunction = connection.CallRfcFunction("/ZPM/WO2PDF2_RECEIVE"))
                    {
                        string messageType = (effectiveStatus == "037" || effectiveStatus == "007") ? "I" : "E";

                        var receiveStatus = new ReceiveStatusIt
                        {
                            ItReceiveStatus = new[]
                            {
                        new Wo2Pdf2ReceiveStatusI
                        {
                            Aufnr = effectiveStatus == "037"
                                ? jobInfo.Wo?.PadLeft(20, '0') ?? string.Empty
                                : new string('0', 20),
                            Vornr = jobInfo.Sequence?.Length > 4
                                ? jobInfo.Sequence.Substring(jobInfo.Sequence.Length - 4)
                                : jobInfo.Sequence?.PadLeft(4, '0') ?? string.Empty,
                            Objtyp = "BUS2007",
                            Wo2Pdf2Url =  HasWindowsPath(outPath) ? filePath : outPath,
                            Msgty = messageType,
                            Msgnr = effectiveStatus,
                            Msgv1 = fileSizeBytes.ToString()
                        }
                    },
                            IvWo2Pdf2Job = jobInfo.JobId ?? string.Empty,
                            IvPrint = jobInfo.Printed ?? string.Empty
                        };

                        rfcFunction.Mapper = _mapper;
                        
                        try
                        {
                            rfcFunction.Invoke(receiveStatus);

                            _logger.LogInformation("RFC callback executed successfully.");
                            _logger.LogInformation($"Successfully reported status to SAP for job {jobInfo.JobId}.");
                        }
                        catch (RfcException ex)
                        {
                            _logger.LogInformation($"Communication error: {ex.Message}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogInformation($"Unexpected error: {ex.Message}");
                        }
                    }
                }
            }
            catch (RfcException ex)
            {
                _logger.LogError(ex, $"SAP RFC error for job {jobInfo.JobId}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"General error during SAP RFC call for job {jobInfo.JobId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if a file exists and is not locked by another process, retrying with exponential backoff.
        /// Ensures that the file is ready for processing before proceeding.
        /// </summary>
        /// <param name="filePath">The path to the file being checked.</param>
        /// <param name="cancellationToken">Token for handling task cancellation requests.</param>
        /// <returns>True if the file exists and is not locked; otherwise, false after retries are exhausted.</returns>
        /// <exception cref="Exception">Logs errors encountered during the file readiness check.</exception>

        private async Task<bool> WaitForFileExistsAsync(string path, CancellationToken cancellationToken)
        {
            const int maxRetries = 5;
            const int delayMs = 300;

            for (int i = 0; i < maxRetries; i++)
            {
                if (File.Exists(path))
                    return true;

                await Task.Delay(delayMs, cancellationToken);
            }

            return false;
        }


        //public async Task UpdateStatusAsync(JobInfo jobInfo, CancellationToken cancellationToken)
        //{
        //    _logger.LogInformation($"Preparing to update SAP status for JobId={jobInfo.JobId}, Wo={jobInfo.Wo}");

        //    try
        //    {
        //        // Retrieve RFC settings from the database
        //        var rfcSettings = await _dataAccess.GetRFCSettingsAsync();
        //        if (rfcSettings == null || rfcSettings.Count == 0)
        //        {
        //            _logger.LogError(null, "No RFC settings found in the database.");
        //            return;
        //        }

        //        var settings = rfcSettings.FirstOrDefault();
        //        if (settings == null)
        //        {
        //            _logger.LogError(null, "Could not retrieve valid RFC settings from the database.");
        //            return;
        //        }

        //        // Retrieve the current environment setting
        //        var environmentSetting = await _dataAccess.GetConfigurationSettingAsync("AppSettings", "Environment");
        //        if (environmentSetting == null)
        //        {
        //            _logger.LogError(null, "Environment configuration not found.");
        //            return;
        //        }

        //        // Retrieve SAP Folder Settings from the database
        //        var sapFolderSettings = await _dataAccess.GetSapFolderSettingsAsync();
        //        var sapFolder = sapFolderSettings.FirstOrDefault(s => s.Name == environmentSetting.Value);
        //        if (sapFolder == null)
        //        {
        //            _logger.LogError(null, $"No SAP folder settings found for environment {environmentSetting.Value}.");
        //            return;
        //        }

        //        // Ensure jobId is padded with leading zeros (assuming 10 digits)
        //        string paddedJobId = jobInfo.JobId.PadLeft(10, '0');

        //        // Construct file path based on the status type (037 for work order, 007 for job)
        //        string sapPath = sapFolder.SapPath;
        //        string environmentFolder = environmentSetting.Value;
        //        string outPath = string.Empty;
        //        string loutPath = string.Empty;
        //        string filePath = string.Empty;

        //        // Map status "009" to a valid SAP-compatible status
        //        string effectiveStatus = jobInfo.Msg == "009"
        //            ? (jobInfo.Wo.Length < 5 ? "007" : "037")
        //            : jobInfo.Msg;

        //        //if (jobInfo.Msg == "037")
        //        //{
        //        //    // Ensure Wo (work order) is padded with leading zeros (12 digits) for 037 status
        //        //    string paddedWo = jobInfo.Wo.PadLeft(12, '0');
        //        //    outPath = $"{sapPath}/{environmentFolder}/Out/{paddedJobId}/{paddedWo}.pdf";  // Work order file
        //        //    loutPath = $"{environmentFolder}\\Out\\{paddedJobId}\\{paddedWo}.pdf";  // Work order file'

        //        //}
        //        //else if (jobInfo.Msg == "007")  // Job complete
        //        //{
        //        //    // For job complete status, pad Aufnr with 20 zeros
        //        //    outPath = $"{sapPath}/{environmentFolder}/Out/{paddedJobId}/{paddedJobId}.pdf";  // Job file
        //        //    loutPath = $"{environmentFolder}\\Out\\{paddedJobId}\\{paddedJobId}.pdf";  // Job file
        //        //    filePath = System.IO.Path.Combine(sapFolder.Path, loutPath);
        //        //}
        //        //else if (jobInfo.Msg == "009")  // Printing disabled by configuration
        //        //{
        //        //    // for errors, use exactly the file the caller provided
        //        //    filePath = jobInfo.File;
        //        //}
        //        //else
        //        //{
        //        //    _logger.LogWarning($"Unknown status code {jobInfo.Msg} for JobId={jobInfo.JobId}. No valid file path generated.");
        //        //    return;
        //        //}

        //        if (effectiveStatus == "037")
        //        {
        //            string paddedWo = jobInfo.Wo.PadLeft(12, '0');
        //            outPath = $"{sapPath}/{environmentFolder}/Out/{paddedJobId}/{paddedWo}.pdf";
        //            loutPath = $"{environmentFolder}\\Out\\{paddedJobId}\\{paddedWo}.pdf";
        //            filePath = System.IO.Path.Combine(sapFolder.Path, loutPath);
        //        }
        //        else if (effectiveStatus == "007")
        //        {
        //            outPath = $"{sapPath}/{environmentFolder}/Out/{paddedJobId}/{paddedJobId}.pdf";
        //            loutPath = $"{environmentFolder}\\Out\\{paddedJobId}\\{paddedJobId}.pdf";
        //            filePath = System.IO.Path.Combine(sapFolder.Path, loutPath);
        //        }

        //        //if (jobInfo.Msg == "009")  // Printing disabled by configuration
        //        //{
        //        //    // for errors, use exactly the file the caller provided
        //        //    filePath = jobInfo.File;
        //        //}
        //        //else
        //        //{
        //        //    _logger.LogWarning($"Unknown status code {jobInfo.Msg} for JobId={jobInfo.JobId}. No valid file path generated.");
        //        //    return;
        //        //}


        //        // Use the local path and files for processing

        //        filePath = System.IO.Path.Combine(sapFolder.Path, loutPath);

        //        if (jobInfo.Msg == "009")  // Printing disabled by configuration
        //        {
        //            // for errors, use exactly the file the caller provided
        //            filePath = jobInfo.File;
        //        }


        //        // Ensure file is ready before proceeding
        //        if (!await WaitForFileExistsAsync(filePath, cancellationToken))
        //        {
        //            _logger.LogError(null, $"File is not ready or locked after retries: {filePath}");
        //            return;  // Exit if the file is not available
        //        }

        //        // Retrieve the file size in bytes
        //        var fileInfo = new FileInfo(filePath);
        //        File.SetAttributes(filePath, FileAttributes.Normal);
        //        long fileSizeBytes = fileInfo.Length;
        //        _logger.LogInformation($"File size (bytes): {fileSizeBytes} for {filePath}");


        //        // Proceed with SAP RFC call after file is ready
        //        using (var connection = new RfcConnection(new RfcConnectionParameterBuilder()
        //                .UseConnectionHost(settings.MSHOST)
        //                .UseLogonUserName(settings.User)
        //                .UseLogonPassword(settings.Password)
        //                .UseLogonClient(settings.CLIENT.ToString())
        //                .UseLogonLanguage(settings.LogonLang)
        //                .UseSystemNumber(settings.SYSNR.ToString())))
        //        {

        //            //_logger.LogInformation("Attempting to establish a connection to SAP RFC.");

        //            connection.Open();
        //            _logger.LogInformation("Successfully connected to SAP.");

        //            using (var rfcFunction = connection.CallRfcFunction("/ZPM/WO2PDF2_RECEIVE"))
        //            {
        //                // Determine message type based on the job status
        //                string messageType = jobInfo.Msg switch
        //                {
        //                    "037" => "I",  // Work order 
        //                    "007" => "I",  // Job 
        //                    "005" => "E",  // File to be printed doesn’t exist
        //                    "009" => "E",  // Printing disabled by configuration
        //                    _ => "E",      // Default to error
        //                };

        //                //Prepare the ReceiveStatusIt object
        //                //var receiveStatus = new ReceiveStatusIt
        //                //{
        //                //    ItReceiveStatus = new[]
        //                //    {
        //                //         new Wo2Pdf2ReceiveStatusI
        //                //         {
        //                //             //Aufnr = jobInfo.Msg == "037" || jobInfo.Msg == "009"
        //                //             Aufnr = jobInfo.Msg == "037"
        //                //                 ? jobInfo.Wo?.PadLeft(20, '0') ?? string.Empty  // For status 037, pad Wo to 20 digits
        //                //                 : new string('0', 20),                          // For status 007, send 20 zeros
        //                //             Vornr = jobInfo.Sequence?.Length > 4
        //                //                 ? jobInfo.Sequence.Substring(jobInfo.Sequence.Length - 4)  // Truncate to the last 4 characters
        //                //                 : jobInfo.Sequence?.PadLeft(4, '0') ?? string.Empty,  // Pad with leading zeros if less than 4 characters
        //                //             Objtyp = "BUS2007",
        //                //             Wo2Pdf2Url = HasWindowsPath(outPath) ? filePath : outPath,  // Use the sap path when reporting back status to SAP if applicable
        //                //             //Wo2Pdf2Url = filePath,
        //                //             Msgty = messageType,
        //                //             Msgnr = jobInfo.Msg ?? string.Empty,
        //                //             Msgv1 = fileSizeBytes.ToString()  // Include the byte count
        //                //         }
        //                //    },
        //                //    IvWo2Pdf2Job = jobInfo.JobId ?? string.Empty,
        //                //    IvPrint = jobInfo.Printed ?? string.Empty
        //                //};
        //                var receiveStatus = new ReceiveStatusIt
        //                {
        //                    ItReceiveStatus = new[]
        //                    {
        //                        new Wo2Pdf2ReceiveStatusI
        //                        {
        //                            Aufnr = effectiveStatus == "037"
        //                                ? jobInfo.Wo?.PadLeft(20, '0') ?? string.Empty
        //                                : new string('0', 20),
        //                            Vornr = jobInfo.Sequence?.Length > 4
        //                                ? jobInfo.Sequence.Substring(jobInfo.Sequence.Length - 4)
        //                                : jobInfo.Sequence?.PadLeft(4, '0') ?? string.Empty,
        //                            Objtyp = "BUS2007",
        //                           // Wo2Pdf2Url = HasWindowsPath(outPath) ? filePath : outPath,
        //                            Wo2Pdf2Url = filePath,
        //                            Msgty = (effectiveStatus == "037" || effectiveStatus == "007") ? "I" : "E",
        //                            Msgnr = effectiveStatus,
        //                            Msgv1 = fileSizeBytes.ToString() // Include the byte count
        //                        }
        //                    },
        //                    IvWo2Pdf2Job = jobInfo.JobId ?? string.Empty,
        //                    IvPrint = jobInfo.Printed ?? string.Empty
        //                };

        //                _logger.LogInformation($"ReceiveStatusIt object prepared: {receiveStatus}");

        //                // Set the mapper for the function call
        //                rfcFunction.Mapper = _mapper;

        //                try
        //                {
        //                    // Send the job status to SAP
        //                    rfcFunction.Invoke(receiveStatus);

        //                    _logger.LogInformation("RFC callback executed successfully.");
        //                    _logger.LogInformation($"Successfully reported status to SAP for job {jobInfo.JobId}.");
        //                }
        //                catch (RfcException ex) // Network-related errors
        //                {
        //                    _logger.LogInformation($"Communication error: {ex.Message}");
        //                }
        //                catch (Exception ex) // Any other unexpected errors
        //                {
        //                    _logger.LogInformation($"Unexpected error: {ex.Message}");
        //                }

        //            }
        //        }
        //    }
        //    catch (RfcException ex)
        //    {
        //        _logger.LogError(ex, $"SAP RFC error for job {jobInfo.JobId}: {ex.Message}");
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, $"General error during SAP RFC call for job {jobInfo.JobId}: {ex.Message}");
        //    }
        //}


        /// <summary>
        /// Overloaded method for updating the status of a job or work order in SAP using individual parameters.
        /// Constructs a JobInfo object and delegates to the main UpdateStatusAsync method.
        /// </summary>
        /// <param name="msg">The SAP message type indicating the job or work order status.</param>
        /// <param name="msgValue">The SAP message value, providing additional context about the status.</param>
        /// <param name="jobId">The unique identifier of the job being updated.</param>
        /// <param name="printed">Indicator if the job has been printed.</param>
        /// <param name="wo">Work order ID associated with the job.</param>
        /// <param name="sequence">Sequence number associated with the work order.</param>
        /// <param name="objType">SAP object type (e.g., "BUS2007").</param>
        /// <param name="file">Path to the file being referenced in the status update.</param>
        /// <param name="cancellationToken">Token for handling task cancellation requests.</param>

        public async Task UpdateStatusAsync(string msg, string msgValue, string jobId, string printed, string wo, string sequence, string objType, string file, CancellationToken cancellationToken)
        {
            var jobInfo = new JobInfo
            {
                Msg = msg,
                MsgValue = msgValue,
                JobId = jobId,
                Printed = printed,
                Wo = wo,
                Sequence = sequence,
                ObjType = objType,
                File = file
            };
            await UpdateStatusAsync(jobInfo, cancellationToken);
        }

        static bool HasWindowsPath(string path)
        {
            string root = System.IO.Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && root.Length > 1 && root[1] == ':'; // Checks "C:" format
        }


 

        // Helper method to check if the file is locked by another process
        private bool IsFileLocked(string filePath)
        {
            try
            {
                using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    // If we can open the file for reading with no sharing, it means it's not locked
                    _logger.LogInformation($"File is accessible: {filePath}");
                    return false;
                }
            }
            catch (IOException)
            {
                // The file is locked
                return true;
            }
        }
    }
}
