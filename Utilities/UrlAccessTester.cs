using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace PrintPlusService.Utilities
{
    // Utilities/UrlAccessTester.cs
    public class UrlAccessTester
    {
        private readonly string _apiBaseUrl;
        private readonly ILogger<Worker> _logger;

        public UrlAccessTester(string apiBaseUrl, ILogger<Worker> logger)
        {
            _apiBaseUrl = apiBaseUrl;
            _logger = logger;
        }

        public async Task<bool> TestUrlAccessAsync()
        {
            using (var client = new HttpClient())
            {
                try
                {
                    var response = await client.GetAsync(_apiBaseUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("Successfully connected to API at {BaseUrl}.", _apiBaseUrl);
                        return true;
                    }
                    else
                    {
                        _logger.LogError("Failed to connect to API at {BaseUrl}. Status code: {StatusCode}", _apiBaseUrl, response.StatusCode);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception occurred while connecting to API at {BaseUrl}", _apiBaseUrl);
                    return false;
                }
            }
        }
    }
}
