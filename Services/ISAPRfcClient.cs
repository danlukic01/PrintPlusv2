using System.Threading;
using System.Threading.Tasks;

namespace PrintPlusService.Services
{
    /// <summary>
    /// Interface for SAP RFC client to update status in SAP.
    /// </summary>
    public interface ISAPRfcClient
    {
        /// <summary>
        /// Updates the status in SAP using the provided parameters asynchronously.
        /// </summary>
        /// <param name="msg">The message type.</param>
        /// <param name="msgValue">The message value.</param>
        /// <param name="jobId">The job ID associated with the status update.</param>
        /// <param name="printed">Indicates if the job has been printed.</param>
        /// <param name="wo">The work order number.</param>
        /// <param name="sequence">The sequence number.</param>
        /// <param name="objType">The object type.</param>
        /// <param name="file">The file path or URL associated with the job.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task UpdateStatusAsync(string msg, string msgValue, string jobId, string printed, string wo, string sequence, string objType, string file, CancellationToken cancellationToken);
      
        /// <summary>
        /// Updates the status in SAP using a JobInfo object asynchronously.
        /// </summary>
        /// <param name="jobInfo">The job information to be reported to SAP.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        //  Task UpdateStatusAsync(JobInfo jobInfo);
    }


}
