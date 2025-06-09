using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PrintPlusService.Services
{
    public interface IDropboxService
    {
        /// <summary>
        /// Asynchronously uploads a file to Dropbox.
        /// </summary>
        /// <param name="localFilePath">The local file path of the file to upload.</param>
        /// <param name="dropboxPath">The destination path in Dropbox (e.g., "/folder/file.txt").</param>
        /// <returns>A task representing the asynchronous upload operation, with a string result representing the uploaded Dropbox path.</returns>
        Task<string> UploadFileAsync(string localFilePath, string dropboxPath);

        /// <summary>
        /// Asynchronously downloads a file from Dropbox.
        /// </summary>
        /// <param name="dropboxPath">The path of the file in Dropbox (e.g., "/folder/file.txt").</param>
        /// <param name="destinationPath">The local path where the file will be saved.</param>
        /// <returns>A task representing the asynchronous download operation, with a string result representing the local file path.</returns>
        Task<string> DownloadFileAsync(string dropboxPath, string destinationPath);

        /// <summary>
        /// Asynchronously lists the contents of a Dropbox folder.
        /// </summary>
        /// <param name="folderPath">The path of the Dropbox folder (e.g., "/folder").</param>
        /// <returns>A task representing the asynchronous operation, with a list of file/folder names.</returns>
        Task<IEnumerable<string>> ListFolderAsync(string folderPath);
    }
}