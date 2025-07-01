using System;
using System.Diagnostics;


namespace PrintPlusService.Services
{
    internal class Util
    {
        public static (int ExitCode, string Output, string Error) LaunchExe(string fileName, string arguments)
        {
            try
            {
                var processInfo = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = processInfo })
                {
                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    return (process.ExitCode, output, error);
                }
            }
            catch (Exception ex)
            {
                // Log or rethrow the exception
                return (-1, string.Empty, ex.Message);
            }
        }
    }
}
