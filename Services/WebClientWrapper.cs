using System;
using System.Net;

namespace PrintPlusService.Services
{
    /// <summary>
    /// Wrapper class for the WebClient to provide additional functionalities.
    /// </summary>
    public class WebClientWrapper : IWebClientWrapper
    {
        private readonly WebClient _client = new WebClient();

        /// <summary>
        /// Downloads the specified resource as a string.
        /// </summary>
        /// <param name="address">The URI from which to download the resource.</param>
        /// <returns>The downloaded resource as a string.</returns>
        public string DownloadString(string address)
        {
            return _client.DownloadString(address);
        }

        /// <summary>
        /// Downloads the specified resource and saves it to the specified file.
        /// </summary>
        /// <param name="address">The URI from which to download the resource.</param>
        /// <param name="fileName">The name of the file to which to save the resource.</param>
        public void DownloadFile(Uri address, string fileName)
        {
            _client.DownloadFile(address, fileName);
        }

        /// <summary>
        /// Gets the collection of header name/value pairs associated with the request.
        /// </summary>
        public WebHeaderCollection Headers => _client.Headers;

        /// <summary>
        /// Gets the collection of header name/value pairs associated with the response.
        /// </summary>
        public WebHeaderCollection ResponseHeaders => _client.ResponseHeaders;

        /// <summary>
        /// Adds a header to the request.
        /// </summary>
        /// <param name="name">The name of the header to add.</param>
        /// <param name="value">The value of the header to add.</param>
        public void AddHeader(string name, string value)
        {
            _client.Headers.Add(name, value);
        }

        /// <summary>
        /// Sets the collection of header name/value pairs associated with the request.
        /// </summary>
        /// <param name="headers">The collection of header name/value pairs to set.</param>
        public void SetHeaders(WebHeaderCollection headers)
        {
            _client.Headers = headers;
        }

        /// <summary>
        /// Gets the value of the specified header.
        /// </summary>
        /// <param name="name">The name of the header to get.</param>
        /// <returns>The value of the specified header.</returns>
        public string GetHeader(string name)
        {
            return _client.Headers[name];
        }
    }
}
