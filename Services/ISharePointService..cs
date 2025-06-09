using System.Threading.Tasks;

namespace PrintPlusService.Services
{
    public interface ISharePointService
    {
        /// <summary>
        /// Asynchronously downloads a file from SharePoint.
        /// </summary>
        /// <param name="url">The URL of the file to download from SharePoint.</param>
        /// <param name="destinationPath">The destination path where the file will be saved.</param>
        /// <returns>A task representing the asynchronous download operation, with a string result representing the file path of the downloaded file.</returns>
       Task<string> DownloadFileAsync(string url, string destinationPath);
      // Task<string> DownloadFileFromShareLinkAsync(string shareLink, string destinationPath);
    }
}
