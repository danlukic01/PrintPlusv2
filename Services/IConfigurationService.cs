using CommonInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintPlusService.Services
{
    /// <summary>
    /// Interface for configuration service to retrieve settings.
    /// </summary>
    public interface IConfigurationService
    {
        /// <summary>
        /// Retrieves a specific setting value by category and key.
        /// </summary>
        /// <param name="category">The category of the setting.</param>
        /// <param name="key">The key of the setting.</param>
        /// <returns>The value of the setting.</returns>
        Task<string> GetSettingAsync(string category, string key);

        /// <summary>
        /// Retrieves all settings in a specific category.
        /// </summary>
        /// <param name="category">The category of the settings.</param>
        /// <returns>A dictionary of settings with keys and values.</returns>
        Task<Dictionary<string, string>> GetSettingsAsync(string category);
    }

    /// <summary>
    /// Implementation of IConfigurationService to manage configuration settings.
    /// </summary>
    public class ConfigurationService : IConfigurationService
    {
        private readonly IDataAccess _dataAccess;

        /// <summary>
        /// Initialises a new instance of the <see cref="ConfigurationService"/> class.
        /// </summary>
        /// <param name="dataAccess">The data access service to interact with the database.</param>
        public ConfigurationService(IDataAccess dataAccess)
        {
            _dataAccess = dataAccess;
        }

        /// <summary>
        /// Retrieves a specific setting value by category and key.
        /// </summary>
        /// <param name="category">The category of the setting.</param>
        /// <param name="key">The key of the setting.</param>
        /// <returns>The value of the setting.</returns>
        public async Task<string> GetSettingAsync(string category, string key)
        {
            try
            {
                var setting = await _dataAccess.GetConfigurationSettingAsync(category, key);
                return setting?.Value;
            }
            catch (Exception ex)
            {
                // Log and handle the exception as needed
                throw new Exception($"Error retrieving setting for category '{category}' and key '{key}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Retrieves all settings in a specific category.
        /// </summary>
        /// <param name="category">The category of the settings.</param>
        /// <returns>A dictionary of settings with keys and values.</returns>
        public async Task<Dictionary<string, string>> GetSettingsAsync(string category)
        {
            try
            {
                var settings = await _dataAccess.GetConfigurationSettingsAsync(category);
                return settings.ToDictionary(s => s.Key, s => s.Value);
            }
            catch (Exception ex)
            {
                // Log and handle the exception as needed
                throw new Exception($"Error retrieving settings for category '{category}': {ex.Message}", ex);
            }
        }
    }
}
