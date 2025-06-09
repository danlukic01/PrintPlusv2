using System;

namespace PrintPlusService.Services
{
    /// <summary>
    /// Configuration settings for the FileWatcherService.
    /// This class allows us to specify how many files can be processed in parallel
    /// when monitoring and processing files in the watched directory.
    /// </summary>
    public class FileWatcherSettings
    {
        /// <summary>
        /// Gets or sets the maximum number of XML files that can be processed in parallel.
        /// This value is loaded from the application's appsettings.json file under the "FileWatcher" section.
        /// The default is 4 if not specified.
        /// </summary>
        public int MaxParallelFileProcessing { get; set; } = 4;
    }
}
