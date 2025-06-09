using System;
using System.Diagnostics;
using System.Drawing; // Reference to System.Drawing
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aspose.CAD.FileFormats.Cad;
using Aspose.CAD.ImageOptions;
using ImageMagick;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PrintPlusService.Services
{
    /// <summary>
    /// The FileConverter class provides methods to convert various file types to PDF.
    /// </summary>
    public class FileConverter : IFileConverter
    {
        private readonly string _libreOfficePath = @"C:\Program Files\LibreOffice\program\soffice.exe";
        private readonly ILogger<FileConverter> _logger;

        /// <summary>
        /// Initialises a new instance of the FileConverter class.
        /// </summary>
        /// <param name="logger">The logger instance to be used for logging information and errors.</param>
        public FileConverter(ILogger<FileConverter> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Converts a file to PDF based on its extension.
        /// </summary>
        /// <param name="filePath">The path to the file to be converted.</param>
        /// <returns>The path to the converted PDF file, or the original file path if no conversion was performed.</returns>
        public async Task<string> ConvertToPdf(string filePath, string cachePath)
        {
            var extension = Path.GetExtension(filePath).ToLower();

            if (extension == ".dwg")
            {
                return ConvertDwgToPdf(filePath);
            }
            else if (extension == ".heic" || extension == ".heif")
            {
                return ConvertHeicToPdf(filePath);
            }
            else if (IsLibreOfficeSupportedFile(extension))
            {
                string result = await ConvertUsingLibreOfficeAsync(filePath, cachePath);
                return result;
            }

            // If it's already a PDF or an unsupported file type
            return filePath;
        }

        /// <summary>
        /// Converts a file using LibreOffice to PDF format.
        /// </summary>
        /// <param name="filePath">The path to the file to be converted.</param>
        /// <returns>The path to the converted PDF file.</returns>
        private static readonly SemaphoreSlim _libreOfficeSemaphore = new(1, 1);

        private async Task<string> ConvertUsingLibreOfficeAsync(string filePath, string cachePath)
        {
            _logger.LogInformation($"Converting file using LibreOffice: {filePath}");

            if (!File.Exists(filePath))
            {
                var errorMessage = $"File does not exist: {filePath}";
                _logger.LogError(errorMessage);
                throw new FileNotFoundException(errorMessage);
            }

            string intermediatePath = null;
            string targetInputFile = filePath;

            // Handle CSV to XLS conversion first
            if (Path.GetExtension(filePath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation($"Detected CSV file. Converting to XLS first: {filePath}");

                var csvToXlsArgs = $"--headless --convert-to xls:\"MS Excel 97\":44,34,76,1 \"{filePath}\" --outdir \"{cachePath}\"";
                var csvToXlsProcess = new ProcessStartInfo
                {
                    FileName = _libreOfficePath,
                    Arguments = csvToXlsArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                await _libreOfficeSemaphore.WaitAsync();
                try
                {
                    using (var process = Process.Start(csvToXlsProcess))
                    {
                        if (!process.WaitForExit(30000))
                        {
                            process.Kill();
                            throw new TimeoutException($"LibreOffice CSV to XLS conversion timed out: {filePath}");
                        }

                        string stdOut = await process.StandardOutput.ReadToEndAsync();
                        string stdErr = await process.StandardError.ReadToEndAsync();

                        if (process.ExitCode != 0)
                        {
                            throw new Exception($"CSV to XLS conversion failed. Exit Code: {process.ExitCode}. Output: {stdOut}. Error: {stdErr}");
                        }

                        var xlsFileName = Path.GetFileNameWithoutExtension(filePath) + ".xls";
                        intermediatePath = Path.Combine(cachePath, xlsFileName);

                        if (!File.Exists(intermediatePath))
                        {
                            throw new FileNotFoundException($"Converted XLS file not found: {intermediatePath}");
                        }

                        targetInputFile = intermediatePath;
                    }
                }
                finally
                {
                    _libreOfficeSemaphore.Release();
                }
            }

            // Now convert to PDF
            string outputFileName = Path.GetFileNameWithoutExtension(targetInputFile) + ".pdf";
            string expectedOutputPath = Path.Combine(cachePath, outputFileName);
            var pdfArgs = $"--headless --convert-to pdf \"{targetInputFile}\" --outdir \"{cachePath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = _libreOfficePath,
                Arguments = pdfArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            await _libreOfficeSemaphore.WaitAsync();
            try
            {
                using (var process = Process.Start(startInfo))
                {
                    if (!process.WaitForExit(30000))
                    {
                        process.Kill();
                        throw new TimeoutException($"LibreOffice PDF conversion timed out: {targetInputFile}");
                    }

                    var output = await process.StandardOutput.ReadToEndAsync();
                    var error = await process.StandardError.ReadToEndAsync();

                    if (process.ExitCode != 0)
                    {
                        throw new Exception($"LibreOffice PDF conversion failed. Exit Code: {process.ExitCode}. Output: {output}, Error: {error}");
                    }
                }

                if (!File.Exists(expectedOutputPath))
                {
                    throw new FileNotFoundException($"Converted PDF not found: {expectedOutputPath}");
                }

                _logger.LogInformation($"Successfully converted file to PDF: {expectedOutputPath}");
                return expectedOutputPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"An error occurred during LibreOffice conversion: {filePath}");
                throw;
            }
            finally
            {
                _libreOfficeSemaphore.Release();

                // Clean up intermediate .xls
                if (intermediatePath != null && File.Exists(intermediatePath))
                {
                    try
                    {
                        File.Delete(intermediatePath);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogWarning(cleanupEx, $"Failed to delete temporary file: {intermediatePath}");
                    }
                }
            }
        }


        //private async Task<string> ConvertUsingLibreOfficeAsync(string filePath, string cachePath)
        //{
        //    _logger.LogInformation($"Converting file using LibreOffice: {filePath}");

        //    if (!File.Exists(filePath))
        //    {
        //        var errorMessage = $"File does not exist: {filePath}";
        //        _logger.LogError(errorMessage);
        //        throw new FileNotFoundException(errorMessage);
        //    }

        //    string intermediatePath = null;
        //    string targetInputFile = filePath;

        //    // Handle CSV conversion to XLS first
        //    if (Path.GetExtension(filePath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        //    {
        //        _logger.LogInformation($"Detected CSV file. Converting to XLS first: {filePath}");

        //        var csvToXlsArgs = $"--headless --convert-to xls:\"MS Excel 97\":44,34,76,1 \"{filePath}\" --outdir \"{cachePath}\"";

        //        var csvToXlsProcess = new ProcessStartInfo
        //        {
        //            FileName = _libreOfficePath,
        //            Arguments = csvToXlsArgs,
        //            RedirectStandardOutput = true,
        //            RedirectStandardError = true,
        //            UseShellExecute = false,
        //            CreateNoWindow = true
        //        };

        //        lock (_libreOfficeLock)
        //        {
        //            using (var process = Process.Start(csvToXlsProcess))
        //            {
        //                if (!process.WaitForExit(30000))
        //                {
        //                    process.Kill();
        //                    throw new TimeoutException($"LibreOffice CSV to XLS conversion timed out: {filePath}");
        //                }

        //                string stdOut = process.StandardOutput.ReadToEnd();
        //                string stdErr = process.StandardError.ReadToEnd();

        //                if (process.ExitCode != 0)
        //                {
        //                    throw new Exception($"CSV to XLS conversion failed. Exit Code: {process.ExitCode}. Output: {stdOut}. Error: {stdErr}");
        //                }

        //                var xlsFileName = Path.GetFileNameWithoutExtension(filePath) + ".xls";
        //                intermediatePath = Path.Combine(cachePath, xlsFileName);

        //                if (!File.Exists(intermediatePath))
        //                {
        //                    throw new FileNotFoundException($"Converted XLS file not found: {intermediatePath}");
        //                }

        //                targetInputFile = intermediatePath;
        //            }
        //        }
        //    }

        //    // Now convert the intermediate .xls (or original non-CSV) to PDF
        //    string outputFileName = Path.GetFileNameWithoutExtension(targetInputFile) + ".pdf";
        //    string expectedOutputPath = Path.Combine(cachePath, outputFileName);

        //    var pdfArgs = $"--headless --convert-to pdf \"{targetInputFile}\" --outdir \"{cachePath}\"";

        //    var startInfo = new ProcessStartInfo
        //    {
        //        FileName = _libreOfficePath,
        //        Arguments = pdfArgs,
        //        RedirectStandardOutput = true,
        //        RedirectStandardError = true,
        //        UseShellExecute = false,
        //        CreateNoWindow = true
        //    };

        //    try
        //    {
        //        lock (_libreOfficeLock)
        //        {
        //            using (var process = Process.Start(startInfo))
        //            {
        //                if (!process.WaitForExit(30000))
        //                {
        //                    process.Kill();
        //                    throw new TimeoutException($"LibreOffice PDF conversion timed out: {targetInputFile}");
        //                }

        //                var output = process.StandardOutput.ReadToEnd();
        //                var error = process.StandardError.ReadToEnd();

        //                if (process.ExitCode != 0)
        //                {
        //                    throw new Exception($"LibreOffice PDF conversion failed. Exit Code: {process.ExitCode}. Output: {output}, Error: {error}");
        //                }
        //            }
        //        }

        //        if (!File.Exists(expectedOutputPath))
        //        {
        //            throw new FileNotFoundException($"Converted PDF not found: {expectedOutputPath}");
        //        }

        //        _logger.LogInformation($"Successfully converted file to PDF: {expectedOutputPath}");
        //        return expectedOutputPath;
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, $"An error occurred during LibreOffice conversion: {filePath}");
        //        throw;
        //    }
        //    finally
        //    {
        //        // Clean up intermediate .xls file if we created one
        //        if (intermediatePath != null && File.Exists(intermediatePath))
        //        {
        //            try
        //            {
        //                File.Delete(intermediatePath);
        //            }
        //            catch (Exception cleanupEx)
        //            {
        //                _logger.LogWarning(cleanupEx, $"Failed to delete temporary file: {intermediatePath}");
        //            }
        //        }
        //    }
        //}

        public async Task<bool> TryRepairPdfWithLibreOffice(string inputPath, string outputPath, string cachePath)
        {
            string tempDir = null;

            await _libreOfficeSemaphore.WaitAsync();
            try
            {
                tempDir = Path.Combine(cachePath, Path.GetRandomFileName());
                Directory.CreateDirectory(tempDir);

                var outputFileName = Path.GetFileNameWithoutExtension(inputPath) + ".pdf";
                var outputFilePath = Path.Combine(tempDir, outputFileName);

                var startInfo = new ProcessStartInfo
                {
                    FileName = _libreOfficePath,
                    Arguments = $"--headless --convert-to pdf --outdir \"{tempDir}\" \"{inputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                    {
                        string output = await process.StandardOutput.ReadToEndAsync();
                        string error = await process.StandardError.ReadToEndAsync();
                        await process.WaitForExitAsync();

                        if (process.ExitCode != 0)
                        {
                            _logger.LogError($"LibreOffice exited with code {process.ExitCode}. Output: {output}, Error: {error}");
                            return false;
                        }
                    }
                    else
                    {
                        _logger.LogError("Failed to start LibreOffice process.");
                        return false;
                    }
                }

                var convertedFile = Directory.GetFiles(tempDir, "*.pdf").FirstOrDefault();
                if (convertedFile != null)
                {
                    File.Copy(convertedFile, outputPath, true);
                    return true;
                }

                _logger.LogError($"No PDF file found after LibreOffice conversion: {inputPath}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"LibreOffice PDF repair failed for {inputPath}");
                return false;
            }
            finally
            {
                _libreOfficeSemaphore.Release();

                if (tempDir != null && Directory.Exists(tempDir))
                {
                    try
                    {
                        Directory.Delete(tempDir, true);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogWarning(cleanupEx, $"Failed to delete temp directory: {tempDir}");
                    }
                }
            }
        }


        //public Task<bool> TryRepairPdfWithLibreOffice(string inputPath, string outputPath, string cachePath)
        //{
        //    string tempDir = null;

        //    try
        //    {
        //        tempDir = Path.Combine(cachePath, Path.GetRandomFileName());
        //        Directory.CreateDirectory(tempDir);

        //        var outputFileName = Path.GetFileNameWithoutExtension(inputPath) + ".pdf";
        //        var outputFilePath = Path.Combine(tempDir, outputFileName);

        //        var startInfo = new ProcessStartInfo
        //        {
        //            FileName = _libreOfficePath, // Ensure this is readonly or properly synchronized
        //            Arguments = $"--headless --convert-to pdf --outdir \"{tempDir}\" \"{inputPath}\"",
        //            RedirectStandardOutput = true,
        //            RedirectStandardError = true,
        //            UseShellExecute = false,
        //            CreateNoWindow = true
        //        };

        //        using (var process = Process.Start(startInfo))
        //        {
        //            if (process != null)
        //            {
        //                string output = process.StandardOutput.ReadToEnd();
        //                string error = process.StandardError.ReadToEnd();
        //                process.WaitForExit();

        //                if (process.ExitCode != 0)
        //                {
        //                    _logger.LogError($"LibreOffice exited with code {process.ExitCode}. Output: {output}, Error: {error}");
        //                    return Task.FromResult(false);
        //                }
        //            }
        //            else
        //            {
        //                _logger.LogError("Failed to start LibreOffice process.");
        //                return Task.FromResult(false);
        //            }
        //        }

        //        var convertedFile = Directory.GetFiles(tempDir, "*.pdf").FirstOrDefault();
        //        if (convertedFile != null)
        //        {
        //            File.Copy(convertedFile, outputPath, true);
        //            return Task.FromResult(true);
        //        }

        //        _logger.LogError($"No PDF file found after LibreOffice conversion: {inputPath}");
        //        return Task.FromResult(false);
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, $"LibreOffice PDF repair failed for {inputPath}");
        //        return Task.FromResult(false);
        //    }
        //    finally
        //    {
        //        if (tempDir != null && Directory.Exists(tempDir))
        //        {
        //            try
        //            {
        //                Directory.Delete(tempDir, true);
        //            }
        //            catch (Exception cleanupEx)
        //            {
        //                _logger.LogWarning(cleanupEx, $"Failed to delete temp directory: {tempDir}");
        //            }
        //        }
        //    }
        //}


        /// <summary>
        /// Converts a DWG file to PDF.
        /// </summary>
        /// <param name="filePath">The path to the DWG file to be converted.</param>
        /// <param name="paperSize">The paper size for the conversion (default is A3).</param>
        /// <returns>The path to the converted PDF file.</returns>
        private string ConvertDwgToPdf(string filePath, string paperSize = "A3")
        {
            _logger.LogInformation($"Converting DWG file to PDF: {filePath} with paper size {paperSize}");

            if (!File.Exists(filePath))
            {
                var errorMessage = $"File does not exist: {filePath}";
                _logger.LogError(errorMessage);
                throw new FileNotFoundException(errorMessage);
            }

            var outputDirectory = Path.GetDirectoryName(filePath);
            var outputFileName = Path.GetFileNameWithoutExtension(filePath) + ".pdf";
            var outputFilePath = Path.Combine(outputDirectory, outputFileName);

            try
            {
                using (var image = Aspose.CAD.Image.Load(filePath))
                {
                    if (image is CadImage cadImage)
                    {
                        float pageWidth = 210f; // Default to A4 width in mm
                        float pageHeight = 297f; // Default to A4 height in mm

                        if (paperSize.ToUpper() == "A3")
                        {
                            pageWidth = 297f;  // A3 width in mm
                            pageHeight = 420f; // A3 height in mm
                        }

                        var rasterizationOptions = new CadRasterizationOptions
                        {
                            PageWidth = pageWidth * 12f, // Scale up for better quality
                            PageHeight = pageHeight * 12f, // Scale up for better quality
                            Layouts = new[] { "Layout1" }, // Change "Model" to "Layout1" or the correct layout name
                            AutomaticLayoutsScaling = true,
                            DrawType = CadDrawTypeMode.UseObjectColor,
                            NoScaling = false
                        };

                        var pdfOptions = new PdfOptions
                        {
                            VectorRasterizationOptions = rasterizationOptions
                        };

                        // Retry mechanism for saving the document
                        const int maxRetries = 3;
                        const int delayMilliseconds = 1000; // 1 second delay between retries

                        for (int attempt = 1; attempt <= maxRetries; attempt++)
                        {
                            try
                            {
                                cadImage.Save(outputFilePath, pdfOptions);
                                _logger.LogInformation($"Successfully converted DWG to PDF: {outputFilePath}");
                                break; // Exit the loop if successful
                            }
                            catch (IOException ex)
                            {
                                if (attempt == maxRetries)
                                {
                                    _logger.LogError(ex, $"Failed to save PDF after {maxRetries} attempts: {outputFilePath}");
                                    throw;
                                }
                                _logger.LogWarning($"Attempt {attempt} failed to save PDF, file might be in use. Retrying in {delayMilliseconds}ms...");
                                Task.Delay(delayMilliseconds).Wait(); // Wait before retrying
                            }
                        }

                        return outputFilePath;
                    }
                    else
                    {
                        var errorMessage = $"The file '{filePath}' is not a valid DWG file.";
                        _logger.LogError(errorMessage);
                        throw new InvalidDataException(errorMessage);
                    }
                }
            }
            catch (Exception ex)
            {
                var errorMessage = $"An error occurred during DWG file conversion: {filePath}";
                _logger.LogError(ex, errorMessage);
                throw new Exception(errorMessage, ex);
            }
        }

        /// <summary>
        /// Converts a HEIC file to PDF.
        /// </summary>
        /// <param name="filePath">The path to the HEIC file to be converted.</param>
        /// <returns>The path to the converted PDF file.</returns>
        private string ConvertHeicToPdf(string filePath)
        {
            _logger.LogInformation($"Converting HEIC file to PDF: {filePath}");

            if (!File.Exists(filePath))
            {
                var errorMessage = $"File does not exist: {filePath}";
                _logger.LogError(errorMessage);
                throw new FileNotFoundException(errorMessage);
            }

            var outputDirectory = Path.GetDirectoryName(filePath);
            var outputFileName = Path.GetFileNameWithoutExtension(filePath) + ".pdf";
            var outputFilePath = Path.Combine(outputDirectory, outputFileName);

            try
            {
                // Convert HEIC to Image (using ImageSharp)
                var image = ConvertHEIC(filePath);

                if (image == null)
                {
                    throw new Exception("Failed to convert HEIC to image.");
                }

                using (var stream = new MemoryStream())
                {
                    image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    stream.Position = 0;

                    // Create PDF document
                    using (var document = new PdfDocument())
                    {
                        var page = document.AddPage();
                        using (var gfx = XGraphics.FromPdfPage(page))
                        {
                            // Load the image from the stream
                            using (var xImage = XImage.FromStream(stream))
                            {
                                // Calculate aspect ratio and scale
                                var ratioX = page.Width / xImage.PixelWidth;
                                var ratioY = page.Height / xImage.PixelHeight;
                                var ratio = Math.Min(ratioX, ratioY);

                                var scaledWidth = xImage.PixelWidth * ratio;
                                var scaledHeight = xImage.PixelHeight * ratio;

                                var offsetX = (page.Width - scaledWidth) / 2;
                                var offsetY = (page.Height - scaledHeight) / 2;

                                gfx.DrawImage(xImage, offsetX, offsetY, scaledWidth, scaledHeight);
                            }
                        }

                        // Retry mechanism for saving the document
                        const int maxRetries = 3;
                        const int delayMilliseconds = 1000; // 1 second delay between retries

                        for (int attempt = 1; attempt <= maxRetries; attempt++)
                        {
                            try
                            {
                                document.Save(outputFilePath);
                                _logger.LogInformation($"Successfully converted HEIC to PDF: {outputFilePath}");
                                break; // Exit the loop if successful
                            }
                            catch (IOException ex)
                            {
                                if (attempt == maxRetries)
                                {
                                    _logger.LogError(ex, $"Failed to save PDF after {maxRetries} attempts: {outputFilePath}");
                                    throw;
                                }
                                _logger.LogWarning($"Attempt {attempt} failed to save PDF, file might be in use. Retrying in {delayMilliseconds}ms...");
                                Task.Delay(delayMilliseconds).Wait(); // Wait before retrying
                            }
                        }
                    }
                }

                return outputFilePath;
            }
            catch (Exception ex)
            {
                var errorMessage = $"An error occurred during HEIC file conversion: {filePath}";
                _logger.LogError(ex, errorMessage);
                throw new Exception(errorMessage, ex);
            }
        }

        /// <summary>
        /// Converts a HEIC file to a System.Drawing.Image using Magick.NET.
        /// </summary>
        /// <param name="filename">The path to the HEIC file to be converted.</param>
        /// <returns>A System.Drawing.Image representing the converted HEIC file.</returns>
        private System.Drawing.Image ConvertHEIC(string filename)
        {
            try
            {
                using (var image = new MagickImage(filename))
                {
                    using (var memoryStream = new MemoryStream())
                    {
                        image.Format = MagickFormat.Png;
                        image.Write(memoryStream);
                        memoryStream.Seek(0, SeekOrigin.Begin);
                        return new Bitmap(memoryStream); // Returning System.Drawing.Image (Bitmap)
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error Converting HEIC file with Magick.NET");
                return null;
            }
        }
         
        /// <summary>
        /// Determines if the file extension is supported by LibreOffice for conversion to PDF.
        /// </summary>
        /// <param name="extension">The file extension to check.</param>
        /// <returns>True if the file extension is supported, false otherwise.</returns>
        private bool IsLibreOfficeSupportedFile(string extension)
        {
            return extension == ".doc" || extension == ".docx" || extension == ".rtf" || extension == ".txt" ||
                   extension == ".html" || extension == ".htm" || extension == ".ods" || extension == ".xls" ||
                   extension == ".xlsx" || extension == ".csv" || extension == ".odp" || extension == ".ppt" ||
                   extension == ".pptx" || extension == ".odg" || extension == ".svg" || extension == ".emf" ||
                   extension == ".wmf" || extension == ".bmp" || extension == ".gif" || extension == ".png" ||
                   extension == ".jpg" || extension == ".jpeg" || extension == ".tiff" || extension == ".tif" ||
                   extension == ".xml" || extension == ".odf" || extension == ".odc" || extension == ".xlsm" ||
                   extension == ".fodt" || extension == ".ott" || extension == ".sxw" || extension == ".fods" ||
                   extension == ".ots" || extension == ".sxc" || extension == ".fodp" || extension == ".otp" ||
                   extension == ".sxi" || extension == ".sxm" || extension == ".mml";

        }
    }
}
