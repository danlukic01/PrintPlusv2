using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PrintPlusService.Services
{
    public interface IBtpDMSService
    {
        /// <summary>
        /// Asynchronously downloads a file from BTP Document Management System.
        /// </summary>
        /// <param name="documentType">The document type.</param>
        /// <param name="documentNumber">The document number.</param>
        /// <param name="documentVersion">The document version.</param>
        /// <param name="documentPart">The document part.</param>
        /// <param name="logicalDocumentId">The logical document ID.</param>
        /// <param name="archiveDocumentId">The archive document ID.</param>
        /// <param name="linkedObjectKey">The linked business object key.</param>
        /// <param name="businessObjectType">The business object type name.</param>
        /// <param name="destinationPath">The destination path for the downloaded file.</param>
        /// <returns>A task representing the asynchronous operation, returning the file path of the downloaded document.</returns>
        Task<string> DownloadFileAsync(
            string documentType,
            string documentNumber,
            string documentVersion,
            string documentPart,
            string logicalDocumentId,
            string archiveDocumentId,
            string linkedObjectKey,
            string businessObjectType,
            string destinationPath
        );
    }
}
