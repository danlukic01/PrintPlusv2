using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using CommonInterfaces;

namespace PrintPlusService.Services
{
    public class BtpDMSService : IBtpDMSService
    {
        private readonly IDataAccess _dataAccess;
        private readonly ILoggerService _logger;
        private readonly HttpClient _httpClient;
        private string _csrfToken;

        public BtpDMSService(IDataAccess dataAccess, ILoggerService logger, HttpClient httpClient)
        {
            _dataAccess = dataAccess;
            _logger = logger;
            _httpClient = httpClient;
        }

        public async Task<string> DownloadFileAsync(
            string documentType,
            string documentNumber,
            string documentVersion,
            string documentPart,
            string logicalDocumentId,
            string archiveDocumentId,
            string linkedObjectKey,
            string businessObjectType,
            string destinationPath)
        {
            var baseUrlSetting = await _dataAccess.GetConfigurationSettingAsync("BtpDMS", "BTP_API");
            string base_url = baseUrlSetting.Value.Trim();

            string fetch_url = $"{base_url}DocumentContentSet(" +
                               $"DocumentType='{Uri.EscapeDataString(documentType)}'," +
                               $"DocumentNumber='{Uri.EscapeDataString(documentNumber)}'," +
                               $"DocumentVersion='{Uri.EscapeDataString(documentVersion)}'," +
                               $"DocumentPart='{Uri.EscapeDataString(documentPart)}'," +
                               $"LogicalDocumentId='{Uri.EscapeDataString(logicalDocumentId)}'," +
                               $"ArchiveDocumentId='{Uri.EscapeDataString(archiveDocumentId)}'," +
                               $"LinkedObjectKey='{Uri.EscapeDataString(linkedObjectKey)}'," +
                               $"BusinessObjectType='{Uri.EscapeDataString(businessObjectType)}')/$value";

            _logger.LogInformation($"Fetching file from URL: {fetch_url}");

            var userSetting = await _dataAccess.GetConfigurationSettingAsync("BtpDMS", "BTP_USER");
            var passSetting = await _dataAccess.GetConfigurationSettingAsync("BtpDMS", "BTP_PASS");
            string username = userSetting.Value.Trim();
            string password = passSetting.Value.Trim();

            await GetCsrfToken(base_url, username, password);

            using (var client = new HttpClient())
            {
                string svcCredentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", svcCredentials);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                client.DefaultRequestHeaders.Add("x-csrf-token", _csrfToken);

                HttpResponseMessage response = await client.GetAsync(fetch_url);

                if (!response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogError(null, $"Request error: {response.StatusCode}");
                    _logger.LogError(null, $"Response body: {responseBody}");
                    throw new HttpRequestException($"Request failed with status: {response.StatusCode}");
                }

                var responseData = await response.Content.ReadAsByteArrayAsync();
                var contentType = response.Content.Headers.ContentType.MediaType;
                var fileExtension = GetFileExtensionFromContentType(contentType);

                string destinationFilePath = Path.Combine(destinationPath, $"{documentType}_{documentNumber}_{documentVersion}.{fileExtension}");
                await File.WriteAllBytesAsync(destinationFilePath, responseData);

                return destinationFilePath;
            }
        }

        private async Task GetCsrfToken(string baseUrl, string username, string password)
        {
            string uri = $"{baseUrl}$metadata";
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("X-CSRF-Token", "Fetch");
                string authInfo = Convert.ToBase64String(Encoding.Default.GetBytes(username + ":" + password));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authInfo);

                var response = await client.GetAsync(uri);
                response.EnsureSuccessStatusCode();
                _csrfToken = response.Headers.GetValues("X-CSRF-Token").FirstOrDefault();
            }
        }

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
                _ => "bin"
            };
        }
    }
}
