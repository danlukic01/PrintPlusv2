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
        private static int fileUniq = 1; // Ensuring a unique filename for each download

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
            try
            {
                // Clean the share link using the existing CleanURL method
                shareLink = CleanURL(shareLink);

                // Parse the URI instead of using Uri.IsWellFormedUriString
                Uri uriResult;
                if (!Uri.TryCreate(shareLink, UriKind.Absolute, out uriResult))
                {
                    _logger.LogWarning($"The share link '{shareLink}' is not a valid absolute URI, but attempting to proceed.");
                }

                _logger.LogInformation($"Attempting direct download using link: {shareLink}");

                // Skip Graph API if it's not a SharePoint link
                if (!shareLink.Contains("sharepoint.com"))
                {
                    // Mimic browser headers for direct download
                    var requestMessage = new HttpRequestMessage(HttpMethod.Get, shareLink);
                    requestMessage.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/89.0.4389.82 Safari/537.36");
                    requestMessage.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
                    requestMessage.Headers.Add("Accept-Language", "en-US,en;q=0.5");
                    requestMessage.Headers.Add("Referer", "https://www.google.com/");
                    requestMessage.Headers.Add("Connection", "keep-alive");

                    var directResponse = await _httpClient.SendAsync(requestMessage);
                    if (directResponse.IsSuccessStatusCode)
                    {
                        // Use your existing method to sanitize the file name
                        string fileName = SanitiseFileName(Path.GetFileName(shareLink));
                        string finalDestinationPath = Path.Combine(destinationPath, fileName);

                        await using (var fs = new FileStream(finalDestinationPath, FileMode.Create, FileAccess.Write))
                        {
                            await directResponse.Content.CopyToAsync(fs);
                        }
                        _logger.LogInformation($"Successfully downloaded file directly to: {finalDestinationPath}");
                        return finalDestinationPath;
                    }
                    else
                    {
                        _logger.LogWarning($"Direct download failed with status: {directResponse.StatusCode}, skipping Graph API.");
                        throw new Exception($"Direct download failed: {directResponse.StatusCode}");
                    }
                }

                // Fallback to Graph API (if it's a SharePoint link)
                _logger.LogInformation($"Attempting to acquire token for SharePoint download using link: {shareLink}");
                var scopes = new[] { $"{await _configService.GetSettingAsync("AzureAd", "ApiUrl")}.default" };
                var authResult = await AcquireTokenAsync(_app, scopes);
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authResult.AccessToken);
                _httpClient.DefaultRequestHeaders.Accept.Clear();
                _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                _logger.LogInformation("Acquired token successfully");

                // Retrieve the file name from the share link using the Graph API
                string originalFileName = await GetFileNameFromShareLinkAsync(shareLink, authResult.AccessToken);
                if (string.IsNullOrEmpty(originalFileName))
                {
                    throw new Exception("Unable to retrieve the file name from the share link.");
                }

                // Generate a unique and sanitised filename with the correct extension
                string originalFileExtension = Path.GetExtension(originalFileName);
                string fileNameGraph = SanitiseFileName(Path.GetFileNameWithoutExtension(originalFileName)) + originalFileExtension;
                string finalDestinationPathGraph = Path.Combine(destinationPath, fileNameGraph);

                // Encode the share link for the Graph API
                var base64Value = Convert.ToBase64String(Encoding.UTF8.GetBytes(shareLink));
                var encodedUrl = $"u!{base64Value.TrimEnd('=').Replace('/', '_').Replace('+', '-')}";

                _logger.LogInformation($"Encoded share link for Graph API: {encodedUrl}");

                // Get download URL using the encoded URL
                var graphApiUrl = $"{await _configService.GetSettingAsync("AzureAd", "ApiUrl")}/v1.0/shares/{encodedUrl}/driveItem";
                _logger.LogInformation($"Calling Graph API URL: {graphApiUrl}");

                string downloadUrl = await GetDownloadUrlAsync(graphApiUrl, authResult.AccessToken);
                if (downloadUrl == null)
                {
                    throw new Exception("Unable to retrieve download URL.");
                }

                // Download the file from the Graph API
                await DownloadFileFromUrlAsync(downloadUrl, finalDestinationPathGraph);
                _logger.LogInformation($"Successfully downloaded file to: {finalDestinationPathGraph}");
                return finalDestinationPathGraph;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, $"HTTP request error downloading file from {shareLink}: {ex.Message}");
                throw new Exception($"Error downloading file from {shareLink}: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"General error downloading file from {shareLink}");
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
                // Encode the share link for use with the Graph API
                var base64Value = Convert.ToBase64String(Encoding.UTF8.GetBytes(shareLink));
                var encodedUrl = $"u!{base64Value.TrimEnd('=').Replace('/', '_').Replace('+', '-')}";

                // Form the API URL to get the drive item metadata
                var graphApiUrl = $"{await _configService.GetSettingAsync("AzureAd", "ApiUrl")}/v1.0/shares/{encodedUrl}/driveItem";

                // Set up the HttpClient with authorization header
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // Make the API call to get the file metadata
                var response = await _httpClient.GetAsync(graphApiUrl);
                response.EnsureSuccessStatusCode();

                var responseBody = await response.Content.ReadAsStringAsync();
                dynamic metadata = JObject.Parse(responseBody);

                // Extract and return the file name from the metadata
                string fileName = metadata.name;
                return fileName;
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

                // Combine cleaned filename with the destination directory
                string validDestinationPath = Path.Combine(Path.GetDirectoryName(destinationPath), validFileName);

                // Ensure the path length is within the limit
                if (validDestinationPath.Length > 260)
                {
                    throw new PathTooLongException("The destination path is too long.");
                }

                using (HttpClient client = new HttpClient())
                {
                    var response = await client.GetAsync(downloadUrl);
                    response.EnsureSuccessStatusCode();
                    byte[] fileBytes = await response.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(validDestinationPath, fileBytes);
                }
                //_logger.LogInformation($"Successfully downloaded file to {validDestinationPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error downloading file from {downloadUrl}");
                throw;
            }
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
                _logger.LogInformation("Acquiring token...");
                var result = await app.AcquireTokenForClient(scopes).ExecuteAsync();
                _logger.LogInformation("Token acquired successfully.");
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

        private static string CleanURL(string url)
        {
            //// Remove CDATA tags
            //input = input.Replace("<![CDATA[", "").Replace("]]>", "");

            //// Replace encoded spaces with actual spaces
            //input = input.Replace("%20", " ");

            //// Trim any leading or trailing whitespaces that might cause issues
            //input = input.Trim();

            //return url;

            return url.Replace("<![CDATA[", "").Replace("]]>", "").Replace("%20", " ").Replace("%23", "#");

        }

        /// <summary>
        /// Encodes a URL for use with the Microsoft Graph API by converting it to a Base64-encoded format and replacing reserved characters.
        /// </summary>
        /// <param name="url">The original URL to encode.</param>
        /// <returns>The encoded URL in a format compatible with the Graph API.</returns>

        private static string EncodeUrlForGraphAPI(string url)
        {
            var base64Value = Convert.ToBase64String(Encoding.UTF8.GetBytes(url));
            return $"u!{base64Value.TrimEnd('=').Replace('/', '_').Replace('+', '-')}";
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
                uniqueFilename = baseFilename;
                fileUniq++;
            }
            return SanitiseFileName(uniqueFilename);
        }
    }
}
