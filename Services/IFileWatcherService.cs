using System.Threading;
using System.Threading.Tasks;

public interface IFileWatcherService
{
    /// <summary>
    /// Starts watching the specified server's directory for new files.
    /// </summary>
    /// <param name="serverName">The name of the server directory to watch.</param>
    void StartWatching(string serverName);

    /// <summary>
    /// Processes files from the queue continuously until the cancellation token is triggered.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token to stop the processing loop.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ProcessFilesFromQueue(CancellationToken stoppingToken);
}
