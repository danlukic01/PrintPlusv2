using System.Threading.Tasks;

namespace PrintPlusService.Services
{
    public interface ISapDMSService
    {
        /// <summary>
        /// Asynchronously downloads a file from SAP DMS.
        /// </summary>
        /// <param name="documentInfoRecordDocType">The document type of the information record.</param>
        /// <param name="documentInfoRecordDocNumber">The document number of the information record.</param>
        /// <param name="documentInfoRecordDocVersion">The document version of the information record.</param>
        /// <param name="documentInfoRecordDocPart">The document part of the information record.</param>
        /// <param name="logicalDocument">The logical document ID.</param>
        /// <param name="archiveDocumentID">The archive document ID.</param>
        /// <param name="linkedSAPObjectKey">The linked SAP object key.</param>
        /// <param name="businessObjectTypeName">The business object type name.</param>
        /// <param name="destinationFolder">The destination folder where the file will be downloaded.</param>
        /// <returns>A task representing the asynchronous download operation, with a string result representing the file path of the downloaded file.</returns>
        Task<string> DownloadFileAsync(
            string documentInfoRecordDocType,
            string documentInfoRecordDocNumber,
            string documentInfoRecordDocVersion,
            string documentInfoRecordDocPart,
            string logicalDocument,
            string archiveDocumentID,
            string linkedSAPObjectKey,
            string businessObjectTypeName,
            string destinationFolder
        );
    }
}
