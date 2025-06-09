using System.Threading;
using System.Threading.Tasks;

public interface IStatusReporter
{
    /// <summary>
    /// Asynchronously reports the status of a job.
    /// </summary>
    /// <param name="jobId">The ID of the job to report the status for.</param>
    /// <param name="statusCode">The status code representing the current status of the job.</param>
    /// <param name="message">A message providing additional details about the status.</param>
    /// <param name="sequence">The sequence number associated with the status report.</param>
    /// <param name="objtyp">The object type associated with the job.</param>
    /// <param name="filePath">The file path related to the job, if applicable.</param>
    /// <returns>A task representing the asynchronous status reporting operation.</returns>
    public Task ReportStatusAsync(int jobId, string workOrderId, string statusCode, string message, string sequence, string objtyp, string filePath, CancellationToken cancellationToken);


}
