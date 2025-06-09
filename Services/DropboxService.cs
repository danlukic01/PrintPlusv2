using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Azure.Core;
using CommonInterfaces;
using DocumentFormat.OpenXml.Bibliography;
using Dropbox.Api;
using Dropbox.Api.Files;
using Dropbox.Api.Sharing;
using CommonInterfaces.Models;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Aspose.CAD.FileFormats.Collada.FileParser.Elements;
using System.Security.Policy;
using System.Net.NetworkInformation;

namespace PrintPlusService.Services
{
    /// <summary>
    /// Service for handling interactions with Dropbox.
    /// </summary>
    public class DropboxService : IDropboxService
    {
        private readonly IDataAccess _dataAccess;
        private readonly ILoggerService _logger;
        private DropboxClient _dropboxClient;
        //private readonly HttpClient _httpClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="DropboxService"/> class.
        /// </summary>
        /// <param name="dataAccess">Data access for configuration.</param>
        /// <param name="logger">Logger instance.</param>
        public DropboxService(IDataAccess dataAccess, ILoggerService logger)
        {
            _dataAccess = dataAccess;
            _logger = logger;
        }

        private async Task InitializeClientAsync()
        {
            var tokenSetting = await _dataAccess.GetConfigurationSettingAsync("Dropbox", "AccessToken");
            var appKeySetting = await _dataAccess.GetConfigurationSettingAsync("Dropbox", "AppKey");
            var appSecretSetting = await _dataAccess.GetConfigurationSettingAsync("Dropbox", "AppSecret");
            var refreshToken = await _dataAccess.GetConfigurationSettingAsync("Dropbox", "RefreshToken");

            var token = tokenSetting?.Value?.Trim();
            var appkey = appKeySetting?.Value?.Trim();
            var appsecret = appSecretSetting?.Value?.Trim();
            var refreshtoken = refreshToken?.Value?.Trim();

            if (string.IsNullOrEmpty(token))
            { 
                throw new InvalidOperationException("Dropbox access token is not configured.");
            }

            try
            {
                _dropboxClient = new DropboxClient(token);

                await EnsureDropboxClientValidAsync(refreshtoken, appkey, appsecret);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.InnerException + ex.Message);
                throw;
            }
        }

        private async Task EnsureDropboxClientValidAsync(string refreshToken, string appKey, string appSecret)
        {
            try
            {
                await _dropboxClient.Users.GetCurrentAccountAsync();
            }
            catch (Exception ex) when (ex.Message.Contains("expired_access_token"))
            {
                var freshToken = await RefreshAccessTokenAsync(refreshToken, appKey, appSecret);
                _dropboxClient = new DropboxClient(freshToken);

                var updatedConfigSetting = new ConfigurationSettings
                {
                    Id = 94, // Dropbox AccessToken Id=54 for dev, Set Id=94 for CoronadoGlobal
                    Category = "Dropbox",
                    Key = "AccessToken",
                    Value = freshToken
                };

                await _dataAccess.UpdateConfigurationSettingAsync(updatedConfigSetting);
                _logger.LogInformation("New access token saved to database.");
            }
        }

        public async Task<string> DownloadFileAsync(string dropboxPath, string destinationPath)
        {
            await InitializeClientAsync();

            try
            {
                _logger.LogInformation($"Downloading file from Dropbox: {dropboxPath}");

               // Use direct download
               using var response = await _dropboxClient.Sharing.GetSharedLinkFileAsync(dropboxPath);

                //using var response = await _dropboxClient.Files.DownloadAsync(dropboxPath);
                //string fileName = Path.GetFileName(dropboxPath);

                string fileName = Path.GetFileName(new Uri(dropboxPath).AbsolutePath);
                string destinationFilePath = Path.Combine(destinationPath, fileName);

                using var stream = await response.GetContentAsStreamAsync();
                using var fileStream = File.Create(destinationFilePath);
                await stream.CopyToAsync(fileStream);

                _logger.LogInformation($"Successfully downloaded file and saved to: {destinationFilePath}");

                return destinationFilePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error downloading file from Dropbox: {ex.Message}");
                throw;
            }
        }

        public async Task<string> UploadFileAsync(string localFilePath, string dropboxPath)
        {
            await InitializeClientAsync();

            try
            {
                using var fileStream = File.Open(localFilePath, FileMode.Open);
                _logger.LogInformation($"Uploading file to Dropbox: {dropboxPath}");

                var updated = await _dropboxClient.Files.UploadAsync(
                    dropboxPath,
                    WriteMode.Overwrite.Instance,
                    body: fileStream
                );

                _logger.LogInformation($"File uploaded to Dropbox path: {updated.PathDisplay}");
                return updated.PathDisplay;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error uploading file to Dropbox: {ex.Message}");
                throw;
            }
        }

        public async Task<IEnumerable<string>> ListFolderAsync(string folderPath)
        {
            await InitializeClientAsync();

            var results = new List<string>();

            try
            {
                var list = await _dropboxClient.Files.ListFolderAsync(folderPath);
                foreach (var item in list.Entries)
                {
                    results.Add(item.Name);
                    _logger.LogInformation($"Found item: {item.Name}");
                }
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error listing Dropbox folder: {ex.Message}");
                throw;
            }
        }

        // List all folders in a given Dropbox directory
        public async Task ListDropboxFolders(DropboxClient dbx, string path)
        {
            try
            {
                var result = await dbx.Files.ListFolderAsync(path);

                // Loop through the entries in the folder
                foreach (var item in result.Entries)
                {
                    // If it's a folder, print its name
                    if (item.IsFolder)
                    {
                        _logger.LogInformation($"Folder: {item.Name}");
                    }
                }

                // If there are more folders, list them as well (pagination)
                while (result.HasMore)
                {
                    result = await dbx.Files.ListFolderContinueAsync(result.Cursor);
                    foreach (var item in result.Entries)
                    {
                        if (item.IsFolder)
                        {
                            Console.WriteLine($"Folder: {item.Name}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"Error listing folders: {ex.Message}");
            }
        }

        //public async Task<string> RefreshAccessTokenAsync(string refreshToken, string clientId, string clientSecret)
        //{
        //    using var client = new HttpClient();

        //    var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/oauth2/token");
        //    request.Content = new FormUrlEncodedContent(new[]
        //    {
        //        new KeyValuePair<string, string>("grant_type", "refresh_token"),
        //        new KeyValuePair<string, string>("refresh_token", refreshToken),
        //        new KeyValuePair<string, string>("client_id", clientId),
        //        new KeyValuePair<string, string>("client_secret", clientSecret),
        //    });

        //    var response = await client.PostAsync(request, content);
        //    response.EnsureSuccessStatusCode();

        //    var body = await response.Content.ReadAsStringAsync();
        //    var json = System.Text.Json.JsonDocument.Parse(body);

        //    var newAccessToken = json.RootElement.GetProperty("access_token").GetString();
        //    return newAccessToken;
        //}
        public async Task<string> RefreshAccessTokenAsync(string refreshToken, string clientId, string clientSecret)
        {
            using var httpClient = new HttpClient();

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret)
            });

            var response = await httpClient.PostAsync("https://api.dropboxapi.com/oauth2/token", content);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<DropboxTokenResponse>(json);

            return tokenResponse.access_token;
        }

        public class DropboxTokenResponse
        {
            public string access_token { get; set; }
            public string token_type { get; set; }
            public int expires_in { get; set; }
        }

        private string CleanURL(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url))
                {
                    throw new ArgumentException("The URL cannot be null or empty.", nameof(url));
                }

                return url.Replace("<![CDATA[", "").Replace("]]>", "").Replace("%20", " ");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error cleaning URL: {url}");
                throw;
            }
        }

        public async Task<string> ConvertSharedLinkToPath(string sharedLink)
        {
            try
            {
                // Clean the share link using the existing CleanURL method
                sharedLink = CleanURL(sharedLink);

                var sharedLinkMetadata = await _dropboxClient.Sharing.GetSharedLinkMetadataAsync(new GetSharedLinkMetadataArg(sharedLink));

                if (sharedLinkMetadata.AsFile != null)
                {
                    return sharedLinkMetadata.AsFile.PathLower;
                }
                else if (sharedLinkMetadata.AsFolder != null)
                {
                    return sharedLinkMetadata.AsFolder.PathLower;
                }
                else
                {
                    _logger.LogInformation("Link did not resolve to a file or folder.");
                    throw new Exception("Link did not resolve to a file or folder.");
                }
            }
            catch (Exception ex)
            {
               _logger.LogError(ex, "Error: " + ex.Message);
                return null;
            }
        }
    }

}

