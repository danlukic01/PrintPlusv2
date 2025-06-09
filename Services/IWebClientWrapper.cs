using System;
using System.Net;

namespace PrintPlusService.Services
{
    /// <summary>
    /// Interface for a web client wrapper to handle HTTP operations.
    /// </summary>
    public interface IWebClientWrapper
    {
        /// <summary>
        /// Downloads the specified resource as a string.
        /// </summary>
        /// <param name="address">The URL of the resource to download.</param>
        /// <returns>The downloaded string.</returns>
        string DownloadString(string address);

        /// <summary>
        /// Downloads the specified resource to a local file.
        /// </summary>
        /// <param name="address">The URL of the resource to download.</param>
        /// <param name="fileName">The name of the local file to save the resource to.</param>
        void DownloadFile(Uri address, string fileName);

        /// <summary>
        /// Gets the headers associated with the request.
        /// </summary>
        WebHeaderCollection Headers { get; }

        /// <summary>
        /// Gets the headers associated with the response.
        /// </summary>
        WebHeaderCollection ResponseHeaders { get; }

        /// <summary>
        /// Adds a header to the request.
        /// </summary>
        /// <param name="name">The name of the header.</param>
        /// <param name="value">The value of the header.</param>
        void AddHeader(string name, string value);

        /// <summary>
        /// Sets the headers for the request.
        /// </summary>
        /// <param name="headers">The headers to set.</param>
        void SetHeaders(WebHeaderCollection headers);

        /// <summary>
        /// Gets the value of a specific header from the response.
        /// </summary>
        /// <param name="name">The name of the header.</param>
        /// <returns>The value of the header.</returns>
        string GetHeader(string name);
    }
}
