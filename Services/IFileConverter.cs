using System;
using System.Threading.Tasks;

namespace PrintPlusService.Services
{
    /// <summary>
    /// Interface for file conversion services.
    /// </summary>
    public interface IFileConverter
    {
        /// <summary>
        /// Converts the specified file to PDF format.
        /// </summary>
        /// <param name="filePath">The path of the file to be converted.</param>
        /// <returns>The path of the converted PDF file.</returns>
        Task<string> ConvertToPdf(string filePath, string cachePath);

        Task<bool> TryRepairPdfWithLibreOffice(string inputPath, string outputPath, string cachePath);
    }
}
