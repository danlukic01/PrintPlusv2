using CommonInterfaces.Models;
using System.Threading.Tasks;

namespace PrintPlusService.Services
{
    public interface IPrinterService
    {
        /// <summary>
        /// Asynchronously prints a file to the specified printer.
        /// </summary>
        /// <param name="printerName">The name of the printer.</param>
        /// <param name="filePath">The path of the file to be printed.</param>
        /// <param name="jobId">The job ID associated with the print job.</param>
        /// <param name="printFlag">The location to send the print Local or Network.</param>
        /// <returns>A task representing the asynchronous print operation.</returns>
        void PrintFileAsync(string printerName, string filePath, string jobId, string printFlag, string workOrderId, string sequence, string objtyp);
        //    void CollectPrintJob(string printerName, string filePath, string jobId, string printFlag, string workOrderId, string sequence, string objtyp, int fileCount);
       
    }
}
