using CommonInterfaces;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace PrintPlusService.Services
{
    /// <summary>
    /// Service for handling interactions with SAP DMS.
    /// </summary>
    public class SapDMSService : ISapDMSService
    {
        private readonly IDataAccess _dataAccess;
        private readonly ILoggerService _logger;
        private readonly HttpClient _httpClient;
        private readonly CookieContainer _cookieContainer;
        private string _csrfToken;

        /// <summary>
        /// Initialises a new instance of the <see cref="SapDMSService"/> class.
        /// </summary>
        /// <param name="dataAccess">Data access interface for database operations.</param>
        /// <param name="logger">Logger for logging information and errors.</param>
        /// <param name="httpClient">HTTP client for making HTTP requests.</param>
        public SapDMSService(IDataAccess dataAccess, ILoggerService logger, HttpClient httpClient)
        {
            _dataAccess = dataAccess;
            _logger = logger;
            _httpClient = httpClient;
            _cookieContainer = new CookieContainer();
        }

        /// <summary>
        /// Downloads a file from SAP DMS.
        /// </summary>
        /// <param name="documentInfoRecordDocType">Document type.</param>
        /// <param name="documentInfoRecordDocNumber">Document number.</param>
        /// <param name="documentInfoRecordDocVersion">Document version.</param>
        /// <param name="documentInfoRecordDocPart">Document part.</param>
        /// <param name="logicalDocument">Logical document.</param>
        /// <param name="archiveDocumentID">Archive document ID.</param>
        /// <param name="linkedSAPObjectKey">Linked SAP object key.</param>
        /// <param name="businessObjectTypeName">Business object type name.</param>
        /// <param name="destinationFolder">Destination folder for the downloaded file.</param>
        /// <returns>Path to the downloaded file.</returns>
        public async Task<string> DownloadFileAsync(string documentInfoRecordDocType, string documentInfoRecordDocNumber, string documentInfoRecordDocVersion, string documentInfoRecordDocPart, string logicalDocument, string archiveDocumentID, string linkedSAPObjectKey, string businessObjectTypeName, string destinationFolder)
        {
            var baseUrlSetting = await _dataAccess.GetConfigurationSettingAsync("SapDMS", "SAP_API");
            string base_url = baseUrlSetting.Value.Trim();

            string fetch_url = $"{base_url}AttachmentContentSet(" +
                               $"DocumentInfoRecordDocType='{Uri.EscapeDataString(documentInfoRecordDocType.Trim())}'," +
                               $"DocumentInfoRecordDocNumber='{Uri.EscapeDataString(documentInfoRecordDocNumber.Trim())}'," +
                               $"DocumentInfoRecordDocVersion='{Uri.EscapeDataString(documentInfoRecordDocVersion.Trim())}'," +
                               $"DocumentInfoRecordDocPart='{Uri.EscapeDataString(documentInfoRecordDocPart.Trim())}'," +
                               $"LogicalDocument='{Uri.EscapeDataString(logicalDocument.Trim())}'," +
                               $"ArchiveDocumentID='{Uri.EscapeDataString(archiveDocumentID.Trim())}'," +
                               $"LinkedSAPObjectKey='{Uri.EscapeDataString(linkedSAPObjectKey.Trim())}'," +
                               $"BusinessObjectTypeName='{Uri.EscapeDataString(businessObjectTypeName.Trim())}')/$value";

            _logger.LogInformation($"Fetching file from URL: {fetch_url}");

            // Check network availability
            if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
            {
                _logger.LogInformation("No network connection available.");
                await Task.Delay(1000);
                throw new Exception("No network connection.");
            }

            var userSetting = await _dataAccess.GetConfigurationSettingAsync("SapDMS", "SAP_USER");
            var passSetting = await _dataAccess.GetConfigurationSettingAsync("SapDMS", "SAP_PASS");
            string username = userSetting.Value.Trim();
            string password = passSetting.Value.Trim();

            _logger.LogInformation($"Using username: {username}");

            await GetCsrfTokenAndCookies(base_url, username, password);

            var handler = new HttpClientHandler { CookieContainer = _cookieContainer };
            using (var client = new HttpClient(handler))
            {
                string svcCredentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", svcCredentials);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                client.DefaultRequestHeaders.Add("x-csrf-token", _csrfToken);

                HttpResponseMessage response = null;
                try
                {
                    _logger.LogInformation($"Fetching file from URL: {fetch_url}");
                    response = await client.GetAsync(fetch_url);

                    if (!response.IsSuccessStatusCode)
                    {
                        string responseBody = await response.Content.ReadAsStringAsync();
                        _logger.LogError(null,$"Request error: {response.StatusCode}");
                        _logger.LogError(null,$"Response body: {responseBody}");
                        throw new HttpRequestException($"Request failed with status: {response.StatusCode}");
                    }

                    var responseData = await response.Content.ReadAsByteArrayAsync();

                    if (responseData.Length != response.Content.Headers.ContentLength)
                    {
                        _logger.LogError(null, $"Mismatch in content length. Expected: {response.Content.Headers.ContentLength}, Actual: {responseData.Length}");
                    }

                    var contentType = response.Content.Headers.ContentType.MediaType;
                    var fileExtension = GetFileExtensionFromContentType(contentType);

                    string destinationFilePath = Path.Combine(destinationFolder, $"{documentInfoRecordDocType}_{documentInfoRecordDocNumber}_{documentInfoRecordDocVersion}.{fileExtension}");

                    _logger.LogInformation($"Writing file to: {destinationFilePath}");
                    await File.WriteAllBytesAsync(destinationFilePath, responseData);

                    return destinationFilePath;
                }
                catch (HttpRequestException e)
                {
                    _logger.LogError(e, $"HttpRequestException Status: {e.Message}");
                    if (response != null)
                    {
                        string responseBody = await response.Content.ReadAsStringAsync();
                        _logger.LogError(null, $"Response: {responseBody}");
                    }
                    throw;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Exception @ Download: {e.Message}");
                    if (e.ToString().Contains("403"))
                    {
                        _logger.LogError(null,"Authorization failed with status 403");
                    }
                    throw;
                }
            }
        }

        /// <summary>
        /// Retrieves the CSRF token and cookies for authentication.
        /// </summary>
        /// <param name="baseUrl">Base URL of the SAP DMS API.</param>
        /// <param name="username">Username for authentication.</param>
        /// <param name="password">Password for authentication.</param>
        private async Task GetCsrfTokenAndCookies(string baseUrl, string username, string password)
        {
            string uri = $"{baseUrl}$metadata";
            var handler = new HttpClientHandler { CookieContainer = _cookieContainer };
            using (var client = new HttpClient(handler))
            {
                client.DefaultRequestHeaders.Add("X-CSRF-Token", "Fetch");
                string authInfo = Convert.ToBase64String(Encoding.Default.GetBytes(username + ":" + password));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authInfo);

                var response = await client.GetAsync(uri);
                response.EnsureSuccessStatusCode();

                _csrfToken = response.Headers.GetValues("X-CSRF-Token").FirstOrDefault();
                _logger.LogInformation("Successfully obtained CSRF token and cookies.");
                _logger.LogInformation("CSRF Token: " + _csrfToken);
            }
        }

        /// <summary>
        /// Gets the file extension based on the content type.
        /// </summary>
        /// <param name="contentType">Content type of the file.</param>
        /// <returns>File extension.</returns>
        private string GetFileExtensionFromContentType(string contentType)
        {
            return contentType switch
            {
                "application/pdf" => "pdf",
                "application/msword" => "doc",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx",
                "application/vnd.ms-excel" => "xls",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "xlsx",
                "image/jpeg" => "jpg",
                "image/png" => "png",
                _ => "bin", // Default to binary if unknown
            };
        }
    }
}
