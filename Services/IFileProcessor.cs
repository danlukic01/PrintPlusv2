using System.Threading;
using System.Threading.Tasks;

namespace PrintPlusService.Services
{
    /// <summary>
    /// Interface for file processing services.
    /// </summary>
    public interface IFileProcessor
    {
        /// <summary>
        /// Processes the specified file.
        /// </summary>
        /// <param name="filePath">The path of the file to be processed.</param>
        /// <param name="cancellationToken">Optional cancellation token to cancel the operation.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task ProcessFile(string filePath, CancellationToken cancellationToken = default);
    }
}
