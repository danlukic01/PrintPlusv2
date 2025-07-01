using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Security.Policy;
using System.Threading;
using System.Collections.Concurrent;

namespace PrintPlusService.Services
{
    /// <summary>
    /// Initializes a new instance of the SharePointService class.
    /// Configures logging, initializes the HTTP client, and sets up necessary configurations
    /// for interacting with SharePoint.
    /// </summary>
    /// <param name="configService">Service for retrieving configuration values, such as Azure AD credentials.</param>
    /// <param name="logger">Service for logging information, warnings, and errors.</param>

    public class SharePointService : ISharePointService
    {
        private readonly HttpClient _httpClient;
        private IConfidentialClientApplication _app;
        private readonly IConfigurationService _configService;
        private readonly ILogger<SharePointService> _logger;
        // Counter used to generate unique file names when downloading
        // attachments from SharePoint.  This prevents multiple documents with
        // the same original name from overwriting each other.
        private static int fileUniq = 1; // Ensuring a unique filename for each download
                                         // private static readonly SemaphoreSlim _downloadLock = new SemaphoreSlim(1, 1);

        private static readonly ConcurrentDictionary<string, Task<string>> _downloadedFileCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initialises a new instance of the <see cref="SharePointService"/> class.
        /// </summary>
        /// <param name="configService">The configuration service.</param>
        /// <param name="logger">The logger instance.</param>
        public SharePointService(IConfigurationService configService, ILogger<SharePointService> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient();
            _configService = configService;

            InitialiseAsync().GetAwaiter().GetResult();
        }


        /// <summary>
        /// Initializes the SharePoint service by loading configuration settings (e.g., client ID, client secret) 
        /// and setting up the confidential client application for Azure AD authentication.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown if any required configuration value is missing.</exception>

        private async Task InitialiseAsync()
        {
            var clientId = await _configService.GetSettingAsync("AzureAd", "ClientId");
            var clientSecret = await _configService.GetSettingAsync("AzureAd", "ClientSecret");
            var instance = await _configService.GetSettingAsync("AzureAd", "Instance");
            var tenant = await _configService.GetSettingAsync("AzureAd", "Tenant");

            ValidateConfigurationValue(clientId, nameof(clientId));
            ValidateConfigurationValue(clientSecret, nameof(clientSecret));
            ValidateConfigurationValue(instance, nameof(instance));
            ValidateConfigurationValue(tenant, nameof(tenant));

            var authority = new Uri(string.Format(instance, tenant));

            // Initialize confidential client application
            _app = ConfidentialClientApplicationBuilder.Create(clientId)
                .WithClientSecret(clientSecret)
                .WithAuthority(authority)
                .Build();
        }

        /// <summary>
        /// Validates that a configuration value is not null or empty. Logs an error and throws an exception if validation fails.
        /// </summary>
        /// <param name="value">The configuration value to validate.</param>
        /// <param name="name">The name of the configuration setting being validated.</param>
        /// <exception cref="ArgumentNullException">Thrown if the configuration value is null or empty.</exception>

        private void ValidateConfigurationValue(string value, string name)
        {
            if (string.IsNullOrEmpty(value))
            {
                _logger.LogError("{Name} configuration value is null or empty.", name);
                throw new ArgumentNullException(name, $"{name} configuration value is null or empty.");
            }
        }


        /// <summary>
        /// Downloads a file from a given share link to a specified destination path.
        /// Handles both direct download and download via the Microsoft Graph API, depending on the link type.
        /// </summary>
        /// <param name="shareLink">The share link URL to download the file from.</param>
        /// <param name="destinationPath">The directory path where the downloaded file will be saved.</param>
        /// <returns>The full path to the downloaded file.</returns>
        /// <exception cref="Exception">Thrown if the download process fails or the file cannot be retrieved.</exception>
        public async Task<string> DownloadFileFromShareLinkAsync(string shareLink, string destinationPath)
        {
            // Normalise URL for consistent caching
            shareLink = CleanURL(shareLink);

            var cacheKey = $"{shareLink}|{destinationPath}";
            var downloadTask = _downloadedFileCache.GetOrAdd(cacheKey, _ => DownloadFileFromShareLinkInternalAsync(shareLink, destinationPath));

            try
            {
                return await downloadTask;
            }
            catch
            {
                _downloadedFileCache.TryRemove(cacheKey, out _);
                throw;
            }
        }

        private async Task<string> DownloadFileFromShareLinkInternalAsync(string shareLink, string destinationPath)
        {
            try
            {
                _logger.LogInformation($"Attempting direct download using link: {shareLink}");

                if (!shareLink.Contains("sharepoint.com"))
                {
                    var requestMessage = new HttpRequestMessage(HttpMethod.Get, shareLink);
                    requestMessage.Headers.Add("User-Agent", "Mozilla/5.0");
                    requestMessage.Headers.Add("Accept", "*/*");
                    requestMessage.Headers.Add("Referer", "https://www.google.com/");
                    requestMessage.Headers.Add("Connection", "keep-alive");

                    var directResponse = await _httpClient.SendAsync(requestMessage);
                    if (directResponse.IsSuccessStatusCode)
                    {
                        var fileName = SanitiseFileName(Path.GetFileName(shareLink));
                        // Ensure files with identical names do not overwrite each other
                        fileName = GenerateUniqueFilename(fileName, ref fileUniq);
                        var finalPath = Path.Combine(destinationPath, fileName);

                        await using var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write);
                        await directResponse.Content.CopyToAsync(fs);
                        _logger.LogInformation($"Successfully downloaded file directly to: {finalPath}");
                        return finalPath;
                    }
                    else
                    {
                        throw new Exception($"Direct download failed: {directResponse.StatusCode}");
                    }
                }

                var scopes = new[] { $"{await _configService.GetSettingAsync("AzureAd", "ApiUrl")}.default" };
                var authResult = await AcquireTokenAsync(_app, scopes);

                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authResult.AccessToken);
                _httpClient.DefaultRequestHeaders.Accept.Clear();
                _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var originalFileName = await GetFileNameFromLinkAsync(shareLink, authResult.AccessToken);
                if (string.IsNullOrEmpty(originalFileName))
                    throw new Exception("Unable to retrieve the file name from the share link.");

                var ext = Path.GetExtension(originalFileName);
                var fileNameGraph = SanitiseFileName(Path.GetFileNameWithoutExtension(originalFileName)) + ext;
                // Prevent name clashes when multiple attachments share the same name
                fileNameGraph = GenerateUniqueFilename(fileNameGraph, ref fileUniq);
                var finalGraphPath = Path.Combine(destinationPath, fileNameGraph);

                var encodedUrl = EncodeUrlForGraphAPI(shareLink);
                var graphApiUrl = $"{await _configService.GetSettingAsync("AzureAd", "ApiUrl")}/v1.0/shares/{encodedUrl}/driveItem";
                _logger.LogInformation($"Calling Graph API URL: {graphApiUrl}");

                var downloadUrl = await GetDownloadUrlAsync(graphApiUrl, authResult.AccessToken);
                if (downloadUrl == null)
                    throw new Exception("Unable to retrieve download URL.");

                if (File.Exists(finalGraphPath))
                {
                    try
                    {
                        var fileInfo = new FileInfo(finalGraphPath);

                        if (fileInfo.IsReadOnly)
                        {
                            fileInfo.IsReadOnly = false;
                        }

                        File.Delete(finalGraphPath);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _logger.LogError(ex, "Access denied while trying to delete file: {FilePath}", finalGraphPath);
                        throw;
                    }
                    catch (IOException ioEx)
                    {
                        _logger.LogWarning(ioEx, "IO error on first delete attempt for: {FilePath}. Retrying...", finalGraphPath);
                        await Task.Delay(200);

                        try
                        {
                            File.Delete(finalGraphPath);
                        }
                        catch (Exception retryEx)
                        {
                            _logger.LogError(retryEx, "Second delete attempt failed for: {FilePath}", finalGraphPath);
                            throw;
                        }
                    }
                }

                await DownloadFileFromUrlAsync(downloadUrl, finalGraphPath);
                _logger.LogInformation($"Successfully downloaded file to: {finalGraphPath}");
                return finalGraphPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error downloading file from {shareLink}");
                throw new Exception($"Error downloading file from {shareLink}", ex);
            }
        }

        /// <summary>
        /// Backward-compatible wrapper for the DownloadFileFromShareLinkAsync method.
        /// Allows existing code to call this method without modification.
        /// </summary>
        /// <param name="url">The URL of the file to download.</param>
        /// <param name="destinationPath">The directory path where the downloaded file will be saved.</param>
        /// <returns>The full path to the downloaded file.</returns>
        public async Task<string> DownloadFileAsync(string url, string destinationPath)
        {
            // Delegate to the new method to keep compatibility with existing code
            return await DownloadFileFromShareLinkAsync(url, destinationPath);
        }


        /// <summary>
        /// Retrieves the file name from a SharePoint share link by querying the Microsoft Graph API for metadata.
        /// </summary>
        /// <param name="shareLink">The share link URL.</param>
        /// <param name="accessToken">The access token for authenticating with the Graph API.</param>
        /// <returns>The file name extracted from the share link metadata.</returns>
        /// <exception cref="Exception">Thrown if the file name cannot be retrieved or the API call fails.</exception>
        private async Task<string> GetFileNameFromShareLinkAsync(string shareLink, string accessToken)
        {
            try
            {
                // Strip query parameters to get the clean base URL
                var uri = new Uri(shareLink);
                var cleanLink = uri.GetLeftPart(UriPartial.Path);

                // Use helper to encode the URL for Graph API
                var encodedUrl = EncodeUrlForGraphAPI(cleanLink);

                var graphApiBaseUrl = await _configService.GetSettingAsync("AzureAd", "ApiUrl");
                var graphApiUrl = $"{graphApiBaseUrl}/v1.0/shares/{encodedUrl}/driveItem";

                // Create request with appropriate headers
                using var request = new HttpRequestMessage(HttpMethod.Get, graphApiUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var responseBody = await response.Content.ReadAsStringAsync();
                dynamic metadata = JObject.Parse(responseBody);

                return metadata.name;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, $"HTTP request error while retrieving file name from {shareLink}: {ex.Message}");
                throw new Exception($"Error retrieving file name from {shareLink}: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"General error retrieving file name from {shareLink}");
                throw new Exception($"Error retrieving file name from {shareLink}", ex);
            }
        }

        public async Task<string> GetFileNameFromPathLinkAsync(string shareLink, string accessToken)
        {
            try
            {

                shareLink = CleanURL(shareLink);

                var uri = new Uri(shareLink);
                var host = uri.Host; // e.g., gm3global.sharepoint.com

                // Extract site and relative path dynamically
                string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

                // Find the index of "sites" and get site name
                int siteIndex = Array.IndexOf(segments, "sites");
                if (siteIndex == -1 || siteIndex + 1 >= segments.Length)
                    throw new Exception("Invalid SharePoint URL format: site name not found.");

                string siteName = segments[siteIndex + 1];
                string relativeFilePath = string.Join('/', segments.Skip(siteIndex + 2));

                // Get the site ID dynamically
                var siteApiUrl = $"https://graph.microsoft.com/v1.0/sites/{host}:/sites/{siteName}";
                var siteRequest = new HttpRequestMessage(HttpMethod.Get, siteApiUrl);
                siteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var siteResponse = await _httpClient.SendAsync(siteRequest);
                siteResponse.EnsureSuccessStatusCode();
                var siteContent = await siteResponse.Content.ReadAsStringAsync();
                dynamic siteMetadata = JObject.Parse(siteContent);
                string siteId = siteMetadata.id;

                // Get all document libraries (drives) in the site
                var drivesApiUrl = $"https://graph.microsoft.com/v1.0/sites/{siteId}/drives";
                var drivesRequest = new HttpRequestMessage(HttpMethod.Get, drivesApiUrl);
                drivesRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var drivesResponse = await _httpClient.SendAsync(drivesRequest);
                drivesResponse.EnsureSuccessStatusCode();
                var drivesContent = await drivesResponse.Content.ReadAsStringAsync();
                dynamic drivesData = JObject.Parse(drivesContent);

                // Find the first drive that matches the path prefix
                string driveId = null;
                foreach (var drive in drivesData.value)
                {
                    string driveName = drive.name;
                    if (relativeFilePath.StartsWith(driveName + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        driveId = drive.id;
                        relativeFilePath = relativeFilePath.Substring(driveName.Length).TrimStart('/');
                        break;
                    }
                }

                if (string.IsNullOrEmpty(driveId))
                    throw new Exception($"No matching document library found for path: {relativeFilePath}");

                // Now retrieve the file metadata
                var fileApiUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{relativeFilePath}";
                var fileRequest = new HttpRequestMessage(HttpMethod.Get, fileApiUrl);
                fileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                fileRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var fileResponse = await _httpClient.SendAsync(fileRequest);
                fileResponse.EnsureSuccessStatusCode();
                var fileContent = await fileResponse.Content.ReadAsStringAsync();
                dynamic fileMetadata = JObject.Parse(fileContent);

                return fileMetadata.name;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP request error while retrieving file name from {ShareLink}", shareLink);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "General error retrieving file name from {ShareLink}", shareLink);
                throw;
            }
        }


        private async Task<string> GetFileNameFromLinkAsync(string link, string accessToken)
        {
            if (link.Contains(":/s/") || link.Contains(":/b:/")) // It's a sharing link
            {
                return await GetFileNameFromShareLinkAsync(link, accessToken);
            }
            else
            {
                return await GetFileNameFromPathLinkAsync(link, accessToken); // direct path
            }
        }

        /// <summary>
        /// Retrieves the download URL for a file from the Microsoft Graph API using the provided Graph API URL and access token.
        /// </summary>
        /// <param name="graphApiUrl">The Graph API URL to query for the download URL.</param>
        /// <param name="accessToken">The access token for authenticating with the Graph API.</param>
        /// <returns>The direct download URL for the file.</returns>
        /// <exception cref="Exception">Thrown if the download URL cannot be retrieved or the API call fails.</exception>

        private async Task<string> GetDownloadUrlAsync(string graphApiUrl, string accessToken)
        {
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                HttpResponseMessage response = await httpClient.GetAsync(graphApiUrl);
                response.EnsureSuccessStatusCode();

                string jsonResponse = await response.Content.ReadAsStringAsync();
                var result = JObject.Parse(jsonResponse);

                if (result.ContainsKey("@microsoft.graph.downloadUrl"))
                {
                    return result["@microsoft.graph.downloadUrl"].ToString();
                }

                _logger.LogInformation("Unable to find @microsoft.graph.downloadUrl in the response.");
                return null;
            }
        }

        /// <summary>
        /// Downloads a file from a given URL to the specified destination path.
        /// Validates and sanitizes the file name before saving.
        /// </summary>
        /// <param name="downloadUrl">The URL of the file to download.</param>
        /// <param name="destinationPath">The directory path where the downloaded file will be saved.</param>
        /// <exception cref="Exception">Thrown if the download process fails.</exception>

        public async Task DownloadFileFromUrlAsync(string downloadUrl, string destinationPath)
        {
            try
            {
                // Clean up the filename
                string cleanedFileName = Path.GetFileNameWithoutExtension(destinationPath);
                cleanedFileName = CleanFileName(cleanedFileName);
                string fileExtension = Path.GetExtension(destinationPath);
                string validFileName = $"{cleanedFileName}{fileExtension}";

                string validDestinationPath = Path.Combine(Path.GetDirectoryName(destinationPath), validFileName);

                if (validDestinationPath.Length > 260)
                    return; // or log and return silently

                using (HttpClient client = new HttpClient())
                {
                    var response = await client.GetAsync(downloadUrl);
                    response.EnsureSuccessStatusCode();
                    byte[] fileBytes = await response.Content.ReadAsByteArrayAsync();

                    try
                    {
                        await File.WriteAllBytesAsync(validDestinationPath, fileBytes);
                    }
                    catch (IOException ioEx)
                    {
                        // File is likely locked — skip and continue
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
               // _logger?.LogWarning(ex, "Failed to download file: {Url}", downloadUrl);
                return;
            }
        }

        private static bool IsFileLocked(IOException ex)
        {
            // Windows error code for "The process cannot access the file because it is being used by another process."
            const int ERROR_SHARING_VIOLATION = 0x20;
            const int ERROR_LOCK_VIOLATION = 0x21;

            int hresult = ex.HResult & 0xFFFF; // Get the low 16 bits of the HResult
            return hresult == ERROR_SHARING_VIOLATION || hresult == ERROR_LOCK_VIOLATION;
        }


        /// <summary>
        /// Cleans a file name by replacing invalid characters with underscores and truncating
        /// the length to a maximum of 100 characters if necessary.
        /// </summary>
        /// <param name="fileName">The original file name.</param>
        /// <returns>The sanitized file name.</returns>

        private string CleanFileName(string fileName)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(c, '_');
            }
            // Limit filename length to 100 characters
            if (fileName.Length > 100)
            {
                fileName = fileName.Substring(0, 100);
            }
            return fileName;
        }

        /// <summary>
        /// Acquires an OAuth 2.0 token for accessing the SharePoint API via Azure AD authentication.
        /// </summary>
        /// <param name="app">The Azure AD confidential client application instance.</param>
        /// <param name="scopes">The scopes required for the API.</param>
        /// <returns>The authentication result containing the access token.</returns>
        /// <exception cref="Exception">Thrown if token acquisition fails.</exception>

        private async Task<AuthenticationResult> AcquireTokenAsync(IConfidentialClientApplication app, string[] scopes)
        {
            try
            {
               // _logger.LogInformation("Acquiring token...");
                var result = await app.AcquireTokenForClient(scopes).ExecuteAsync();
              //  _logger.LogInformation("Token acquired successfully.");
                return result;
            }
            catch (MsalServiceException ex)
            {
                _logger.LogError(ex, "Error acquiring token.");
                HandleTokenAcquisitionErrors(ex);
                throw;
            }
        }

        /// <summary>
        /// Handles errors encountered during token acquisition, providing specific error messages
        /// for common authentication issues (e.g., invalid client secret, invalid client ID).
        /// </summary>
        /// <param name="ex">The exception thrown during token acquisition.</param>
        /// <exception cref="Exception">Throws detailed exceptions for specific authentication errors.</exception>

        private void HandleTokenAcquisitionErrors(MsalServiceException ex)
        {
            if (ex.Message.Contains("AADSTS70011"))
            {
                throw new Exception("The scope provided is not supported.");
            }
            else if (ex.Message.Contains("AADSTS7000215"))
            {
                throw new Exception("The client secret is invalid.");
            }
            else if (ex.Message.Contains("AADSTS700016"))
            {
                throw new Exception("The client ID is invalid.");
            }
            else
            {
                throw new Exception("Error acquiring token.");
            }
        }

        /// <summary>
        /// Cleans a URL by removing unnecessary characters such as CDATA tags and replacing encoded spaces with actual spaces.
        /// </summary>
        /// <param name="input">The original URL.</param>
        /// <returns>The cleaned URL.</returns>
        public static string CleanURL(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;

            return Uri.UnescapeDataString(
                url.Replace("<![CDATA[", "")
                   .Replace("]]>", "")
                   .Replace('\\', '/') // <-- critical!
                   .Trim()
            );
        }


        /// <summary>
        /// Encodes a URL for use with the Microsoft Graph API by converting it to a Base64-encoded format and replacing reserved characters.
        /// </summary>
        /// <param name="url">The original URL to encode.</param>
        /// <returns>The encoded URL in a format compatible with the Graph API.</returns>

        public static string EncodeUrlForGraphAPI(string url)
        {
            var bytes = Encoding.UTF8.GetBytes(url);
            var base64 = Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

            return $"u!{base64}";
        }

        /// <summary>
        /// Extracts the file name from a given URL by parsing its local path.
        /// </summary>
        /// <param name="url">The URL of the file.</param>
        /// <returns>The file name extracted from the URL.</returns>

        private static string GetFileNameFromUrl(string url)
        {
            Uri uri = new Uri(url);
            return Path.GetFileName(uri.LocalPath);
        }

        /// <summary>
        /// Sanitizes a file name by replacing invalid characters with underscores and ensuring compatibility with file system restrictions.
        /// </summary>
        /// <param name="fileName">The original file name.</param>
        /// <returns>The sanitized file name.</returns>

        public static string SanitiseFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            return string.Concat(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
        }

        /// <summary>
        /// Generates a unique file name by appending an incrementing number to the base file name.
        /// Ensures thread safety by using a lock to synchronize access to the unique counter.
        /// </summary>
        /// <param name="baseFilename">The base file name.</param>
        /// <param name="fileUniq">A reference to the unique counter.</param>
        /// <returns>The generated unique file name.</returns>

        private string GenerateUniqueFilename(string baseFilename, ref int fileUniq)
        {
            string uniqueFilename;
            lock (this) // Use 'this' to ensure it's an instance lock
            {
                // Append an incrementing number before the extension so each
                // downloaded file path remains distinct.
                string name = Path.GetFileNameWithoutExtension(baseFilename);
                string ext = Path.GetExtension(baseFilename);
                uniqueFilename = $"{name}_{fileUniq}{ext}";
                fileUniq++;
            }
            return SanitiseFileName(uniqueFilename);
        }
    }
}
