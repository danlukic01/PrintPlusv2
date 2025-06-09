using CommonInterfaces;
using PrintPlusService.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

public class StatusReporter : IStatusReporter
{
    private readonly ISAPRfcClient _sapRfcClient;
    private readonly ILoggerService _logger;

    /// <summary>
    /// Initialises a new instance of the <see cref="StatusReporter"/> class.
    /// </summary>
    /// <param name="sapRfcClient">The SAP RFC client.</param>
    /// <param name="logger">The logger instance.</param>
    public StatusReporter(ISAPRfcClient sapRfcClient, ILoggerService logger)
    {
        _sapRfcClient = sapRfcClient;
        _logger = logger;
    }

    /// <summary>
    /// Reports the status to SAP.
    /// </summary>
    /// <param name="jobId">The job ID.</param>
    /// <param name="statusCode">The status code.</param>
    /// <param name="message">The message.</param>
    /// <param name="sequence">The sequence.</param>
    /// <param name="objtyp">The object type.</param>
    /// <param name="filePath">The file path.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ReportStatusAsync(int jobId, string workOrderId, string statusCode, string message, string sequence, string objtyp, string filePath, CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Reporting status to SAP: JobId={jobId}, WorkOrderId={(!string.IsNullOrEmpty(workOrderId) ? workOrderId.ToString() : "None")}, StatusCode={statusCode}, Message={message}, FilePath={filePath}");

        try
        {
            // Use the injected SAPRfcClient to report the status
            await _sapRfcClient.UpdateStatusAsync(
                msg: statusCode,
                msgValue: message,
                jobId: jobId.ToString(),
                printed: "1", // Assuming the value "1" for printed as per the original code
                wo: workOrderId?.ToString() ?? jobId.ToString(), // Use jobId if workOrderId is not provided
                sequence: sequence,
                objType: objtyp,
                file: filePath,
                cancellationToken: cancellationToken  // Pass the CancellationToken here
            );

           // _logger.LogInformation($"Successfully reported status to SAP for job {jobId}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error reporting status to SAP for job {jobId}: {ex.Message}");
        }
    }






}
