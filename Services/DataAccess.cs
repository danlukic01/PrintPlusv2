using Microsoft.Extensions.Logging;
using CommonInterfaces.Models;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using CommonInterfaces;
using PrintPlusService.Services;
using System.Management;
using Serilog.Core;
using System.Text;
using System.ServiceProcess;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using Microsoft.Extensions.Configuration;
using Serilog;
using Aspose.Pdf.Forms;
using Microsoft.EntityFrameworkCore.Internal;

public class DataAccess : IDataAccess
{
   // private readonly PrintPlusContext _context;
    private readonly IDbContextFactory<PrintPlusContext> _context;
    private readonly ILogger<DataAccess> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly bool _useSqlServer;


    /// <summary>
    /// Initialises a new instance of the <see cref="DataAccess"/> class.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="logger">The logger instance.</param>
    public DataAccess(
      IDbContextFactory<PrintPlusContext> context,
      ILogger<DataAccess> logger,
      IConfiguration configuration)
    {
        _context = context;
        _logger = logger;
        _useSqlServer = configuration.GetValue<bool>("UseSqlServer");
    }


    /// <summary>
    /// Gets the pending work orders asynchronously.
    /// </summary>
    /// <returns>A list of pending work orders.</returns>
    public async Task<IEnumerable<WorkOrder>> GetPendingWorkOrdersAsync()
    {
        using var context = _context.CreateDbContext();
        //var _context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();
        //using var context = _context.CreateDbContext();

        return await context.WorkOrders
                             .Where(wo => wo.Status == "Pending")
                             .ToListAsync();
    }

    /// <summary>
    /// Updates the status of a work order part asynchronously.
    /// </summary>
    /// <param name="uniqueJobId">The unique job ID.</param>
    /// <param name="workOrderId">The work order ID.</param>
    /// <param name="partId">The part ID.</param>
    /// <param name="status">The new status.</param>
    /// <param name="errorMessage">The error message (if any).</param>
    /// <param name="processedAt">The processed at timestamp (optional).</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the work order part is not found.</exception>
    public async Task UpdateWorkOrderPartStatusAsync(string uniqueJobId, string workOrderId, string partId, string status, string errorMessage = null, string processedAt = null)
     {
        using var context = _context.CreateDbContext();
        //var _context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        _logger.LogInformation($"Updating status for WorkOrderPart with WorkOrderId: {workOrderId}, PartId: {partId} to {status}");
         var workOrderParts = await context.WorkOrderParts
             .Where(part => part.UniqueJobId == uniqueJobId && part.WorkOrderId == workOrderId && part.PartId == partId)
             .ToListAsync();

         if (workOrderParts.Any())
         {
             foreach (var workOrderPart in workOrderParts)
             {
                 workOrderPart.Status = status;
                 workOrderPart.ProcessedAt = processedAt ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

                 if (status == "Error")
                 {
                     workOrderPart.ErrorMessage = errorMessage;
                 }

                 context.WorkOrderParts.Update(workOrderPart);
             }

            if (_useSqlServer)
            {
                await context.SaveChangesAsync();
            }
            else
            {
                await SqliteWriteLock.RunWithWriteLock(async () =>
                {
                    await context.SaveChangesAsync();
                });
            }
         }
         else
         {
             throw new InvalidOperationException($"WorkOrderPart with PartId {partId}, WorkOrderId {workOrderId}, and UniqueJobId {uniqueJobId} not found.");
         }
     }

   


    /// <summary>
    /// Gets the work orders by unique job ID asynchronously.
    /// </summary>
    /// <param name="uniqueJobId">The unique job ID.</param>
    /// <returns>A list of work orders associated with the unique job ID.</returns>
    public async Task<IEnumerable<WorkOrder>> GetWorkOrdersByUniqueJobIdAsync(string uniqueJobId)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        var workOrders = await context.WorkOrders
            .Where(wo => wo.UniqueJobId == uniqueJobId)
            .ToListAsync();

        return workOrders;
    }

    /// <summary>
    /// Gets the work order parts by unique job ID asynchronously.
    /// </summary>
    /// <param name="uniqueJobId">The unique job ID.</param>
    /// <returns>A list of work order parts associated with the unique job ID.</returns>
    public async Task<IEnumerable<WorkOrderPart>> GetWorkOrderPartsAsync(string uniqueJobId)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        var workOrderParts = await context.WorkOrderParts
            .Where(part => part.UniqueJobId == uniqueJobId)
            .ToListAsync();

        return workOrderParts;
    }

    /// <summary>
    /// Gets the work order parts by unique job ID and work order ID asynchronously.
    /// </summary>
    /// <param name="uniqueJobId">The unique job ID.</param>
    /// <param name="workOrderId">The work order ID.</param>
    /// <returns>A list of work order parts associated with the unique job ID and work order ID.</returns>
    public async Task<IEnumerable<WorkOrderPart>> GetWorkOrderPartsAsync(string uniqueJobId, string workOrderId)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        var workOrderParts = await context.WorkOrderParts
            .Where(part => part.UniqueJobId == uniqueJobId && part.WorkOrderId == workOrderId)
            .ToListAsync();

        return workOrderParts;
    }

    /// <summary>
    /// Adds a new job to the database asynchronously.
    /// </summary>
    /// <param name="job">The job entity to add.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AddJobAsync(Job job)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        job.UniqueJobId = Guid.NewGuid().ToString(); // Generate a new unique identifier
        context.Jobs.Add(job);

        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }

        context.Entry(job).State = EntityState.Detached; // Detach entity after adding
        _logger.LogInformation($"Job with Id {job.Id} added to the database.");
    }

    /// <summary>
    /// Adds a new work order to the database or updates it if it already exists.
    /// </summary>
    /// <param name="workOrder">The work order entity to add or update.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AddWorkOrderAsync(WorkOrder workOrder)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        if (string.IsNullOrEmpty(workOrder.JobId))
        {
            throw new Exception("JobId cannot be null or empty. Cannot add WorkOrder.");
        }

        // Fetch the Job associated with the UniqueJobId
        var job = await context.Jobs.FirstOrDefaultAsync(j => j.UniqueJobId == workOrder.UniqueJobId);
        if (job == null)
        {
            throw new Exception($"Job with UniqueJobId {workOrder.UniqueJobId} does not exist. Cannot add WorkOrder.");
        }

        // Check if the WorkOrder already exists
        var existingWorkOrder = await context.WorkOrders
            .FirstOrDefaultAsync(wo => wo.JobId == workOrder.JobId && wo.WorkOrderId == workOrder.WorkOrderId && wo.UniqueJobId == workOrder.UniqueJobId);

        if (existingWorkOrder != null)
        {
            // Update existing work order
            _logger.LogWarning($"WorkOrder with ID {workOrder.WorkOrderId} for Job ID {workOrder.JobId} already exists. Updating existing WorkOrder.");
            existingWorkOrder.Objtyp = workOrder.Objtyp;
            existingWorkOrder.File = workOrder.File;
            existingWorkOrder.Status = workOrder.Status;
            existingWorkOrder.CreatedAt = workOrder.CreatedAt;
            existingWorkOrder.ProcessedAt = workOrder.ProcessedAt;
            context.WorkOrders.Update(existingWorkOrder);
        }
        else
        {
            // Add new work order
            workOrder.UniqueJobId = job.UniqueJobId; // Set UniqueJobId from the job
            context.WorkOrders.Add(workOrder);
        }

        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }

        context.Entry(workOrder).State = EntityState.Detached; // Detach entity after adding/updating
        _logger.LogInformation($"WorkOrder with ID {workOrder.WorkOrderId} for Job ID {workOrder.JobId} added/updated in the database.");
    }

    /// <summary>
    /// Adds a new work order part to the database.
    /// </summary>
    /// <param name="workOrderPart">The work order part entity to add.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AddWorkOrderPartAsync(WorkOrderPart workOrderPart)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        var workOrder = await context.WorkOrders
            .FirstOrDefaultAsync(wo => wo.WorkOrderId == workOrderPart.WorkOrderId && wo.UniqueJobId == workOrderPart.UniqueJobId);

        if (workOrder != null)
        {
            // Set UniqueJobId and add work order part
            workOrderPart.UniqueJobId = workOrder.UniqueJobId; // Set UniqueJobId
            context.WorkOrderParts.Add(workOrderPart);
            if (_useSqlServer)
            {
                await context.SaveChangesAsync();
            }
            else
            {
                await SqliteWriteLock.RunWithWriteLock(async () =>
                {
                    await context.SaveChangesAsync();
                });
            }
            context.Entry(workOrderPart).State = EntityState.Detached; // Detach entity after adding
            _logger.LogInformation($"WorkOrderPart with ID {workOrderPart.PartId} for WorkOrder ID {workOrderPart.WorkOrderId} and Unique jobID {workOrder.UniqueJobId} added to the database.");
        }
        else
        {
            _logger.LogError($"WorkOrder with ID {workOrderPart.WorkOrderId} and UniqueJobId {workOrderPart.UniqueJobId} not found.");
        }
    }


    /// <summary>
    /// Gets a job by its ID asynchronously, always retrieving the most up-to-date record.
    /// </summary>
    /// <param name="jobId">The job ID.</param>
    /// <returns>The job entity if found, otherwise null.</returns>
    public async Task<Job> GetJobByIdAsync(string jobId)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        return await context.Jobs
                             .AsNoTracking()  // This forces EF to fetch the latest data from the database.
                             .Where(j => j.JobId == jobId)
                             .OrderByDescending(j => j.CreatedAt)
                             .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Gets a configuration setting by its category and key asynchronously.
    /// </summary>
    /// <param name="category">The category of the setting.</param>
    /// <param name="key">The key of the setting.</param>
    /// <returns>The configuration setting if found, otherwise null.</returns>
    public async Task<ConfigurationSettings> GetConfigurationSettingAsync(string category, string key)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        var setting = await context.ConfigurationSettings
                                    .FirstOrDefaultAsync(c => c.Category == category && c.Key == key);

        if (setting != null)
        {
            setting.Value = setting.Value.Trim(); // Trim the value to remove \r\n and other whitespace
        }

        return setting;
    }

    /// <summary>
    /// Gets all configuration settings for a specific category asynchronously.
    /// </summary>
    /// <param name="category">The category of the settings.</param>
    /// <returns>A list of configuration settings.</returns>
    public async Task<IEnumerable<ConfigurationSettings>> GetConfigurationSettingsAsync(string category)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        return await context.ConfigurationSettings
                             .Where(c => c.Category == category)
                             .ToListAsync();
    }

    /// <summary>
    /// Updates or adds a configuration setting asynchronously.
    /// </summary>
    /// <param name="config">The configuration setting to update or add.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UpdateConfigurationAsync(ConfigurationSettings config)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        var existingConfig = await context.ConfigurationSettings
                                           .FirstOrDefaultAsync(c => c.Key == config.Key);
        if (existingConfig != null)
        {
            // Update the existing configuration
            existingConfig.Value = config.Value;
            if (_useSqlServer)
            {
                await context.SaveChangesAsync();
            }
            else
            {
                await SqliteWriteLock.RunWithWriteLock(async () =>
                {
                    await context.SaveChangesAsync();
                });
            }
        }
        else
        {
            if (_useSqlServer)
            {
                // Add new configuration setting
                context.ConfigurationSettings.Add(config);
                await context.SaveChangesAsync();
            }
            else
            {
                await SqliteWriteLock.RunWithWriteLock(async () =>
                {
                    // Add new configuration setting
                    context.ConfigurationSettings.Add(config);
                    await context.SaveChangesAsync();
                });
            }
        }
    }

    /// <summary>
    /// Retrieves a specific work order by its unique job ID and work order ID asynchronously.
    /// </summary>
    /// <param name="uniqueJobId">The unique job ID.</param>
    /// <param name="workOrderId">The work order ID.</param>
    /// <returns>The work order entity if found, otherwise null.</returns>
    public async Task<WorkOrder> GetWorkOrderByIdAsync(string uniqueJobId, string workOrderId)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        return await context.WorkOrders.FirstOrDefaultAsync(wo => wo.UniqueJobId == uniqueJobId && wo.WorkOrderId == workOrderId);
    }

    /// <summary>
    /// Retrieves a specific work order by its work order ID asynchronously.
    /// </summary>
    /// <param name="workOrderId">The work order ID.</param>
    /// <returns>The work order entity if found, otherwise null.</returns>
    public async Task<WorkOrder> GetWorkOrderByIdAsync(string workOrderId)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        return await context.WorkOrders.FirstOrDefaultAsync(wo => wo.WorkOrderId == workOrderId);
    }

    /// <summary>
    /// Retrieves SAP folder settings asynchronously.
    /// </summary>
    /// <returns>A list of SAP folder settings.</returns>
    public async Task<IEnumerable<SapFolderSetting>> GetSapFolderSettingsAsync()
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        return await context.SapFolderSettings.ToListAsync();
    }

    /// <summary>
    /// Retrieves a specific work order by its work order ID and unique job ID asynchronously.
    /// </summary>
    /// <param name="workOrderId">The work order ID.</param>
    /// <param name="uniqueJobId">The unique job ID.</param>
    /// <returns>The work order entity if found, otherwise null.</returns>
    public async Task<WorkOrder> GetWorkOrderByJobIdAndWorkOrderIdAsync(string workOrderId, string uniqueJobId)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        return await context.WorkOrders
            .FirstOrDefaultAsync(wo => wo.WorkOrderId == workOrderId && wo.UniqueJobId == uniqueJobId);
    }


    /// <summary>
    /// Checks if operation splitting is enabled by retrieving the setting from the configuration.
    /// </summary>
    /// <returns>True if operation splitting is enabled, otherwise false.</returns>
    public async Task<bool> GetEnableOperationSplittingAsync()
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        var setting = await context.ConfigurationSettings
            .FirstOrDefaultAsync(c => c.Key == "EnableOperationSplitting" && c.Category == "AppSettings");

        return setting != null && bool.Parse(setting.Value);
    }

    /// <summary>
    /// Retrieves a printer setting by its name asynchronously.
    /// </summary>
    /// <param name="name">The name of the printer.</param>
    /// <returns>The printer setting entity if found, otherwise null.</returns>
    public async Task<PrinterSetting> GetPrinterSettingByNameAsync(string printerName)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        return await context.PrinterSettings
            .Where(p => p.Name == printerName)
            .FirstOrDefaultAsync();
    }

    public async Task<PrinterSetting> GetPrinterSettingByShortNameAsync(string printerShortName)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        return await context.PrinterSettings
            .Where(p => p.ShortName == printerShortName)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Updates the status of a job asynchronously.
    /// </summary>
    /// <param name="jobId">The job ID.</param>
    /// <param name="uniqueJobId">The unique job ID.</param>
    /// <param name="status">The new status of the job.</param>
    /// <param name="processedAt">Optional processed time. If not provided, the current UTC time will be used.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /* public async Task UpdateJobStatusAsync(string jobId, string uniqueJobId, string status, string processedAt = null)
     {
         _logger.LogInformation($"Updating status for Job with JobId: {jobId} to {status}");

         var job = await context.Jobs
                                 .Where(j => j.JobId == jobId && j.UniqueJobId == uniqueJobId)
                                 .OrderByDescending(j => j.CreatedAt)
                                 .FirstOrDefaultAsync();

         if (job != null)
         {
             job.Status = status;
             if (status == "Processed" || status == "Error")
             {
                 job.ProcessedAt = processedAt ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
             }
             context.Jobs.Update(job);
             await context.SaveChangesAsync();
         }
         else
         {
             _logger.LogWarning($"Job with JobId {jobId} and UniqueJobId {uniqueJobId} not found.");
             throw new InvalidOperationException($"Job with JobId {jobId} and UniqueJobId {uniqueJobId} not found.");
         }
     }
    */
    public async Task UpdateJobStatusAsync(string jobId, string uniqueJobId, string status, string processedAt = null, string errorMessage = null)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        _logger.LogInformation($"Updating status for Job with JobId: {jobId} to {status}");

        var job = await context.Jobs
                                .Where(j => j.JobId == jobId && j.UniqueJobId == uniqueJobId)
                                .OrderByDescending(j => j.CreatedAt)
                                .FirstOrDefaultAsync();

        if (job != null)
        {
            job.Status = status;

            // Update ProcessedAt if status is "Processed" or "Error"
            if (status == "Processed" || status == "Error")
            {
                job.ProcessedAt = processedAt ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            }

            // Only update the error message if a non-null value is provided
            if (errorMessage != null)
            {
                job.ErrorMessage = errorMessage;
            }

            context.Jobs.Update(job);

            if (_useSqlServer)
            {
                await context.SaveChangesAsync();
            }
            else
            {
                await SqliteWriteLock.RunWithWriteLock(async () =>
                {
                    await context.SaveChangesAsync();
                });
            }
        }
        else
        {
            _logger.LogWarning($"Job with JobId {jobId} and UniqueJobId {uniqueJobId} not found.");
            throw new InvalidOperationException($"Job with JobId {jobId} and UniqueJobId {uniqueJobId} not found.");
        }
    }




    /// <summary>
    /// Updates the error message and status of a job asynchronously.
    /// </summary>
    /// <param name="jobId">The ID of the job to update.</param>
    /// <param name="errorMessage">The error message to set.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UpdateJobErrorMessageAsync(string jobId, string errorMessage)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        var job = await context.Jobs
                                .Where(j => j.JobId == jobId)
                                .OrderByDescending(j => j.CreatedAt) // Get the most recent job entry
                                .FirstOrDefaultAsync();

        if (job != null)
        {
            job.ErrorMessage = errorMessage;
            job.Status = "Error"; // Set the status to Error
            job.ProcessedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"); // Set the ProcessedAt field
            context.Jobs.Update(job);
            if (_useSqlServer)
            {
                await context.SaveChangesAsync();
            }
            else
            {
                await SqliteWriteLock.RunWithWriteLock(async () =>
                {
                    await context.SaveChangesAsync();
                });
            }
            _logger.LogInformation($"Updated error message for Job ID {jobId}");
        }
        else
        {
            _logger.LogError($"Job with Id {jobId} not found.");
            throw new InvalidOperationException($"Job with Id {jobId} not found.");
        }
    }

    /// <summary>
    /// Updates the status and optional error message of a work order asynchronously.
    /// </summary>
    /// <param name="uniqueJobId">The unique job ID associated with the work order.</param>
    /// <param name="workOrderId">The ID of the work order to update.</param>
    /// <param name="status">The new status to set for the work order.</param>
    /// <param name="errorMessage">Optional error message to set if the status is "Error".</param>
    /// <param name="processedAt">Optional processed time. If not provided, the current UTC time will be used.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UpdateWorkOrderStatusAsync(string uniqueJobId, string workOrderId, string status, string errorMessage = null, string processedAt = null)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        _logger.LogInformation($"Updating status for WorkOrder with WorkOrderId: {workOrderId} to {status}");

        var workOrder = await context.WorkOrders
                                      .Where(wo => wo.UniqueJobId == uniqueJobId && wo.WorkOrderId == workOrderId)
                                      .FirstOrDefaultAsync();

        if (workOrder != null)
        {
            workOrder.Status = status;

            if (status == "Processed" || status == "Error")
            {
                workOrder.ProcessedAt = processedAt ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            }

            if (status == "Error" && errorMessage != null)
            {
                workOrder.ErrorMessage = errorMessage;
            }
            if (_useSqlServer)
            {
                await context.SaveChangesAsync();
               
            }
            else
            {
                await SqliteWriteLock.RunWithWriteLock(async () =>
                {
                    await context.SaveChangesAsync();
                    
                });
            }
            _logger.LogInformation($"Updated WorkOrder status to {status} for WorkOrderId {workOrderId}");
        }
        else
        {
            _logger.LogWarning($"WorkOrder with Id {workOrderId} and UniqueJobId {uniqueJobId} not found. Check shoppaper file exist!");
            throw new InvalidOperationException($"WorkOrder with Id {workOrderId} and UniqueJobId {uniqueJobId} not found. Check shoppaper file exist!");
        }
    }


    public async Task<string> GetWorkOrderIdFromFilePathAsync(string filePath, string uniqueJobId)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Try to find a work order by both File and UniqueJobId
        var workOrder = await context.WorkOrders
            .SingleOrDefaultAsync(wo => wo.File == filePath && wo.UniqueJobId == uniqueJobId);

        // If the filePath is not yet in the database, find a work order by uniqueJobId only
        if (workOrder == null)
        {
            workOrder = await context.WorkOrders
                .FirstOrDefaultAsync(wo => wo.UniqueJobId == uniqueJobId);

            if (workOrder == null)
            {
                var errorMessage = $"No work order found with UniqueJobId {uniqueJobId} (and FilePath {filePath}).";
                _logger.LogError(errorMessage);
                throw new InvalidOperationException(errorMessage);
            }

            // Optionally log that the work order was found by UniqueJobId, but not file path
            _logger.LogWarning($"WorkOrder found using UniqueJobId {uniqueJobId} but no file path was associated yet.");
        }

        return workOrder.WorkOrderId;
    }


    /// <summary>
    /// Retrieves the unique job ID associated with a given job ID asynchronously.
    /// </summary>
    /// <param name="jobId">The job ID to look up.</param>
    /// <returns>The unique job ID if found, otherwise null.</returns>
    public async Task<string> GetUniqueJobIdByJobIdAsync(string jobId)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        var job = await context.Jobs
            .Where(j => j.JobId == jobId)
            .FirstOrDefaultAsync();

        if (job != null)
        {
            return job.UniqueJobId;
        }
        else
        {
            _logger.LogWarning($"Job with Id {jobId} not found in GetUniqueJobIdByJobIdAsync.");
            return null; // Return null to indicate the job was not found
        }
    }

    /// <summary>
    /// Retrieves a work order part by its work order ID and part ID asynchronously.
    /// </summary>
    /// <param name="workOrderId">The ID of the work order.</param>
    /// <param name="partId">The ID of the work order part.</param>
    /// <returns>The work order part entity if found, otherwise null.</returns>
    public async Task<WorkOrderPart> GetWorkOrderPartByIdAsync(string workOrderId, string partId)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        return await context.WorkOrderParts
                             .FirstOrDefaultAsync(wop => wop.WorkOrderId == workOrderId && wop.PartId == partId);
    }

    /// <summary>
    /// Updates a work order entity asynchronously.
    /// </summary>
    /// <param name="workOrder">The work order entity to update.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UpdateWorkOrderAsync(WorkOrder workOrder)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        context.WorkOrders.Update(workOrder);

        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
            _logger.LogInformation($"Updated WorkOrder with ID {workOrder.WorkOrderId} in the database.");
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
                _logger.LogInformation($"Updated WorkOrder with ID {workOrder.WorkOrderId} in the database.");
            });
        }
    }



    /// <summary>
    /// Retrieves all jobs from the database asynchronously in descending order.
    /// </summary>
    /// <returns>A list of Job entities in descending order.</returns>
    public async Task<List<Job>> GetAllJobsAsync()
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        _logger.LogInformation("Retrieving all jobs from the database in descending order.");

        // Assuming you want to order by JobId in descending order
        return await context.Jobs
            .OrderByDescending(job => job.JobId) // Replace JobId with the field you want to sort by
            .ToListAsync();
    }

    /// <summary>
    /// Retrieves all work orders associated with a specific unique job ID asynchronously.
    /// </summary>
    /// <param name="uniqueJobId">The unique job ID.</param>
    /// <returns>An enumerable of WorkOrder entities.</returns>
    public async Task<IEnumerable<WorkOrder>> GetWorkOrderByJobIdAsync(string uniqueJobId)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        _logger.LogInformation($"Retrieving work orders for unique job ID: {uniqueJobId}");
        return await context.WorkOrders
                             .Where(wo => wo.UniqueJobId == uniqueJobId)
                             .ToListAsync();
    }

    /// <summary>
    /// Retrieves all configuration settings from the database asynchronously.
    /// </summary>
    /// <returns>An enumerable of ConfigurationSettings entities.</returns>
    public async Task<IEnumerable<ConfigurationSettings>> GetConfigurationSettingsAsync()
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        _logger.LogInformation("Retrieving all configuration settings from the database.");
        return await context.ConfigurationSettings.ToListAsync();
    }

    /// <summary>
    /// Retrieves a configuration setting by its ID asynchronously.
    /// </summary>
    /// <param name="id">The ID of the configuration setting.</param>
    /// <returns>The ConfigurationSettings entity if found, otherwise null.</returns>
    public async Task<ConfigurationSettings> GetConfigurationSettingByIdAsync(int id)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        _logger.LogInformation($"Retrieving configuration setting with ID: {id}");
        return await context.ConfigurationSettings.FindAsync(id);
    }

    /// <summary>
    /// Updates an existing configuration setting in the database asynchronously.
    /// </summary>
    /// <param name="configurationSetting">The configuration setting entity to update.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UpdateConfigurationSettingAsync(ConfigurationSettings configurationSetting)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();
        //context.ConfigurationSettings.Update(configurationSetting);
        //var result = await context.SaveChangesAsync();
        //_logger.LogInformation($"{result} configuration setting(s) updated in the database.");

        var setting = await context.ConfigurationSettings
       .FirstOrDefaultAsync(c => c.Id == configurationSetting.Id);

        if (setting != null)
        {
            // Update the fields of the tracked entity
            setting.Category = configurationSetting.Category;
            setting.Key = configurationSetting.Key;
            setting.Value = configurationSetting.Value;

            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                var result = await context.SaveChangesAsync();
                _logger.LogInformation($"{result} configuration setting(s) updated in the database.");
            });

            if (_useSqlServer)
            {
                var result = await context.SaveChangesAsync();
                _logger.LogInformation($"{result} configuration setting(s) updated in the database.");
            }
            else
            {
                await SqliteWriteLock.RunWithWriteLock(async () =>
                {
                    await context.SaveChangesAsync();
                });
            }


        }
        else
        {
            // Not in DB — attach as modified
            context.ConfigurationSettings.Attach(configurationSetting);
            context.Entry(configurationSetting).State = EntityState.Modified;

            if (_useSqlServer)
            {
                var result = await context.SaveChangesAsync();
                _logger.LogInformation($"{result} configuration setting(s) updated in the database.");
            }
            else
            {
                await SqliteWriteLock.RunWithWriteLock(async () =>
                {
                    await context.SaveChangesAsync();
                });
            }

        }
        
    }

    /// <summary>
    /// Adds a new configuration setting to the database asynchronously.
    /// </summary>
    /// <param name="configurationSetting">The configuration setting entity to add.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AddConfigurationSettingAsync(ConfigurationSettings configurationSetting)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        context.ConfigurationSettings.Add(configurationSetting);

        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }

        _logger.LogInformation("New configuration setting added to the database.");

    }

    /// <summary>
    /// Deletes a configuration setting by its ID asynchronously.
    /// </summary>
    /// <param name="id">The ID of the configuration setting to delete.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task DeleteConfigurationSettingAsync(int id)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        var setting = await context.ConfigurationSettings.FindAsync(id);
        if (setting != null)
        {
            context.ConfigurationSettings.Remove(setting);

            if (_useSqlServer)
            {
                await context.SaveChangesAsync();
                _logger.LogInformation($"Configuration setting with ID {id} deleted from the database.");
            }
            else
            {
                await SqliteWriteLock.RunWithWriteLock(async () =>
                {
                    await context.SaveChangesAsync();
                    _logger.LogInformation($"Configuration setting with ID {id} deleted from the database.");
                });
            }

        }
        else
        {
            _logger.LogWarning($"Configuration setting with ID {id} not found.");
        }
    }

    /// <summary>
    /// Retrieves all printer settings from the database asynchronously.
    /// </summary>
    /// <returns>An enumerable of PrinterSetting entities.</returns>
    public async Task<IEnumerable<PrinterSetting>> GetPrinterSettingsAsync()
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        _logger.LogInformation("Retrieving all printer settings from the database.");
        return await context.PrinterSettings.ToListAsync();
    }

    /// <summary>
    /// Retrieves a printer setting by its ID asynchronously.
    /// </summary>
    /// <param name="id">The ID of the printer setting.</param>
    /// <returns>The PrinterSetting entity if found, otherwise null.</returns>
    public async Task<PrinterSetting> GetPrinterSettingByIdAsync(int id)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        //_logger.LogInformation($"Retrieving printer setting with ID: {id}");
        return await context.PrinterSettings.FindAsync(id);
    }

    /// <summary>
    /// Updates an existing printer setting in the database asynchronously.
    /// </summary>
    /// <param name="printerSetting">The printer setting entity to update.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UpdatePrinterSettingAsync(PrinterSetting printerSetting)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        context.PrinterSettings.Update(printerSetting);

        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }

        _logger.LogInformation("Printer setting(s) updated in the database.");  
   
    }

    /// <summary>
    /// Adds a new printer setting to the database asynchronously.
    /// </summary>
    /// <param name="printerSetting">The printer setting entity to add.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AddPrinterSettingAsync(PrinterSetting printerSetting)
    {
        
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        context.PrinterSettings.Add(printerSetting);

        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }

        _logger.LogInformation("New printer setting added to the database.");
    }

    /// <summary>
    /// Deletes a printer setting by its ID asynchronously.
    /// </summary>
    /// <param name="id">The ID of the printer setting to delete.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task DeletePrinterSettingAsync(int id)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        _logger.LogInformation($"Attempting to delete printer setting with ID: {id}");
        var setting = await context.PrinterSettings.FindAsync(id);
        if (setting != null)
        {
            context.PrinterSettings.Remove(setting);
            if (_useSqlServer)
            {
                await context.SaveChangesAsync();
            }
            else
            {
                await SqliteWriteLock.RunWithWriteLock(async () =>
                {
                    await context.SaveChangesAsync();
                });
            }
            _logger.LogInformation($"Printer setting with ID {id} deleted successfully.");
        }
        else
        {
            _logger.LogWarning($"Printer setting with ID {id} not found.");
        }
    }

    /// <summary>
    /// Retrieves all RFC settings from the database asynchronously.
    /// </summary>
    /// <returns>A list of RFCSettings entities.</returns>
    public async Task<List<RFCSettings>> GetRFCSettingsAsync()
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        _logger.LogInformation("Retrieving all RFC settings from the database.");
        return await context.RFCSettings.ToListAsync();
    }

    /// <summary>
    /// Retrieves an RFC setting by its ID asynchronously.
    /// </summary>
    /// <param name="id">The ID of the RFC setting.</param>
    /// <returns>The RFCSettings entity if found, otherwise null.</returns>
    public async Task<RFCSettings> GetRFCSettingByIdAsync(int id)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        _logger.LogInformation($"Retrieving RFC setting with ID: {id}");
        return await context.RFCSettings.FindAsync(id);
    }

    /// <summary>
    /// Updates an existing RFC setting in the database asynchronously.
    /// </summary>
    /// <param name="rfcSetting">The RFC setting entity to update.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UpdateRFCSettingAsync(RFCSettings rfcSetting)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        context.RFCSettings.Update(rfcSetting);
    
        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }

        _logger.LogInformation("RFC setting(s) updated in the database.");
    }

    /// <summary>
    /// Adds a new RFC setting to the database asynchronously.
    /// </summary>
    /// <param name="rfcSetting">The RFC setting entity to add.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AddRFCSettingAsync(RFCSettings rfcSetting)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        context.RFCSettings.Add(rfcSetting);

        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }

        _logger.LogInformation("New RFC setting added to the database.");
    }

    /// <summary>
    /// Deletes an RFC setting by its ID asynchronously.
    /// </summary>
    /// <param name="id">The ID of the RFC setting to delete.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task DeleteRFCSettingAsync(int id)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        _logger.LogInformation($"Attempting to delete RFC setting with ID: {id}");
        var setting = await context.RFCSettings.FindAsync(id);
        if (setting != null)
        {
            context.RFCSettings.Remove(setting);
            if (_useSqlServer)
            {
                await context.SaveChangesAsync();
            }
            else
            {
                await SqliteWriteLock.RunWithWriteLock(async () =>
                {
                    await context.SaveChangesAsync();
                });
            }
            _logger.LogInformation($"RFC setting with ID {id} deleted successfully.");
        }
        else
        {
            _logger.LogWarning($"RFC setting with ID {id} not found.");
        }
    }

    /// <summary>
    /// Retrieves a SAP folder setting by its ID asynchronously.
    /// </summary>
    /// <param name="id">The ID of the SAP folder setting.</param>
    /// <returns>The SapFolderSetting entity if found, otherwise null.</returns>
    public async Task<SapFolderSetting> GetSapFolderSettingByIdAsync(int id)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        _logger.LogInformation($"Retrieving SAP folder setting with ID: {id}");
        return await context.SapFolderSettings.FindAsync(id);
    }

    /// <summary>
    /// Updates an existing SAP folder setting in the database asynchronously.
    /// </summary>
    /// <param name="sapFolderSetting">The SAP folder setting entity to update.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task UpdateSapFolderSettingAsync(SapFolderSetting sapFolderSetting)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        context.SapFolderSettings.Update(sapFolderSetting);

        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }

        _logger.LogInformation("SAP folder setting updated successfully.");
    }

    /// <summary>
    /// Adds a new SAP folder setting to the database asynchronously.
    /// </summary>
    /// <param name="sapFolderSetting">The SAP folder setting entity to add.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AddSapFolderSettingAsync(SapFolderSetting sapFolderSetting)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        context.SapFolderSettings.Add(sapFolderSetting);

        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }

        _logger.LogInformation("New SAP folder setting added to the database.");
    }

    /// <summary>
    /// Deletes a SAP folder setting by its ID asynchronously.
    /// </summary>
    /// <param name="id">The ID of the SAP folder setting to delete.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task DeleteSapFolderSettingAsync(int id)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        _logger.LogInformation($"Attempting to delete SAP folder setting with ID: {id}");
        var setting = await context.SapFolderSettings.FindAsync(id);
        if (setting != null)
        {
            context.SapFolderSettings.Remove(setting);
            if (_useSqlServer)
            {
                await context.SaveChangesAsync();
            }
            else
            {
                await SqliteWriteLock.RunWithWriteLock(async () =>
                {
                    await context.SaveChangesAsync();
                });
            }
            _logger.LogInformation($"SAP folder setting with ID {id} deleted successfully.");
        }
        else
        {
            _logger.LogWarning($"SAP folder setting with ID {id} not found.");
        }
    }

    /// <summary>
    /// Retrieves a job by its file path asynchronously.
    /// </summary>
    /// <param name="filePath">The file path associated with the job.</param>
    /// <returns>The Job entity if found, otherwise null.</returns>
    public async Task<Job> GetJobByFilePathAsync(string filePath)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        _logger.LogInformation($"Retrieving job associated with file path: {filePath}");
        return await context.Jobs.FirstOrDefaultAsync(j => j.XmlFilePath == filePath);
    }

    /// <summary>
    /// Retrieves a user by their username.
    /// </summary>
    /// <param name="username">The username of the user to retrieve.</param>
    /// <returns>A task that represents the asynchronous operation, containing the user if found, otherwise null.</returns>
    public async Task<User> GetUserByUsernameAsync(string username)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Queries the Users table to find a user with the specified username.
        // Returns a single User object if found, otherwise returns null.
        return await context.Users.SingleOrDefaultAsync(u => u.Username == username);
    }

    /// <summary>
    /// Retrieves a user by their password.
    /// </summary>
    /// <param name="username">The username of the user to retrieve by password.</param>
    /// <returns>A task that represents the asynchronous operation, containing the user if found, otherwise null.</returns>
    public async Task<User> GetUserByPasswordAsync(string username)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Queries the Users table to find a user with the specified password.
        // Returns a single User object if found, otherwise returns null.
        // Note: This method name implies it searches by password, but it seems to actually search by username.
        return await context.Users.SingleOrDefaultAsync(u => u.Username == username);
    }

    /// <summary>
    /// Adds a new user to the Users table.
    /// </summary>
    /// <param name="user">The User object to add to the database.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task AddUserAsync(User user)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Adds the new User object to the Users table.
        context.Users.Add(user);
        // Saves the changes to the database.
        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }

        _logger.LogInformation("User added to the database.");
    }

    /// <summary>
    /// Retrieves all users from the Users table.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation, containing a list of all users.</returns>
    public async Task<List<User>> GetAllUsersAsync()
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Retrieves all user records from the Users table and returns them as a list.
        return await context.Users.ToListAsync();
    }

    /// <summary>
    /// Deletes a user from the Users table by their user ID.
    /// </summary>
    /// <param name="userId">The ID of the user to delete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task DeleteUserAsync(int userId)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Finds the user in the Users table by their ID.
        var user = await context.Users.FindAsync(userId);
        if (user != null)
        {
            // If the user exists, remove them from the Users table.
            context.Users.Remove(user);
            // Save the changes to the database.
            if (_useSqlServer)
            {
                await context.SaveChangesAsync();
            }
            else
            {
                await SqliteWriteLock.RunWithWriteLock(async () =>
                {
                    await context.SaveChangesAsync();
                });
            }
        }
    }

    // Method to get key metrics
    public async Task<KeyMetrics> GetKeyMetricsAsync()
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Calculate the total number of files processed by counting the records in the WorkOrderParts table.
        var totalFilesProcessed = await context.WorkOrderParts.CountAsync();

        // Fetch work orders with a status of "Processed" and valid CreatedAt and ProcessedAt timestamps.
        var workOrders = await context.WorkOrders
            .Where(wo => wo.Status == "Processed" && !string.IsNullOrEmpty(wo.CreatedAt) && !string.IsNullOrEmpty(wo.ProcessedAt))
            .ToListAsync();  // Fetch data asynchronously from the database.

        // Perform in-memory processing to calculate the average processing time for the valid work orders.
        var validWorkOrders = workOrders
            .Where(wo => DateTime.TryParse(wo.CreatedAt, out _) && DateTime.TryParse(wo.ProcessedAt, out _)) // Ensure CreatedAt and ProcessedAt can be parsed into DateTime objects.
            .Select(wo => new
            {
                // Parse the CreatedAt and ProcessedAt strings into DateTime objects, assuming they are in UTC.
                ProcessedAt = DateTime.Parse(wo.ProcessedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal),
                CreatedAt = DateTime.Parse(wo.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)
            })
            // Calculate the processing time in seconds for each work order.
            .Select(wo => (wo.ProcessedAt - wo.CreatedAt).TotalSeconds)
            .ToList();

        // Calculate the average processing time if there are valid work orders, otherwise default to 0.
        double averageProcessingTime = validWorkOrders.Any() ? validWorkOrders.Average() : 0;

        // Count the number of successful jobs based on the "Processed" status in the WorkOrders table.
        var successfulJobs = await context.WorkOrders.CountAsync(wo => wo.Status == "Processed");

        // Count the number of failed jobs based on the "Failed" status in the WorkOrders table.
        var failedJobs = await context.WorkOrders.CountAsync(wo => wo.Status == "Failed");

        // Return the key metrics including total files processed, average processing time, successful jobs, and failed jobs.
        return new KeyMetrics
        {
            TotalFilesProcessed = totalFilesProcessed,
            AvgProcessingTime = averageProcessingTime,
            SuccessfulJobs = successfulJobs,
            FailedJobs = failedJobs
        };
    }


    // Method to get recent activity
    public async Task<IEnumerable<ActivityLog>> GetRecentActivityAsync()
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Fetch recent jobs from the database where ProcessedAt or CreatedAt timestamps are not null or empty.
        var recentJobs = await context.Jobs
            .Where(j => !string.IsNullOrEmpty(j.ProcessedAt) || !string.IsNullOrEmpty(j.CreatedAt))
            .Select(j => new
            {
                JobId = j.JobId,          // Select the Job ID
                Status = j.Status,        // Select the Job Status
                ProcessedAt = j.ProcessedAt, // Select the ProcessedAt timestamp
                CreatedAt = j.CreatedAt   // Select the CreatedAt timestamp
            })
            .ToListAsync();  // Fetch data asynchronously from the database

        // Fetch recent work orders from the database where ProcessedAt or CreatedAt timestamps are not null or empty.
        var recentWorkOrders = await context.WorkOrders
            .Where(wo => !string.IsNullOrEmpty(wo.ProcessedAt) || !string.IsNullOrEmpty(wo.CreatedAt))
            .Select(wo => new
            {
                JobId = wo.UniqueJobId,   // Select the UniqueJobId as the Job ID
                WorkOrderId = wo.WorkOrderId, // Select the Work Order ID
                Status = wo.Status,       // Select the Work Order Status
                ProcessedAt = wo.ProcessedAt, // Select the ProcessedAt timestamp
                CreatedAt = wo.CreatedAt  // Select the CreatedAt timestamp
            })
            .ToListAsync();  // Fetch data asynchronously from the database

        // Process the fetched job data in memory and convert it to ActivityLog objects
        var recentActivity = recentJobs.Select(j => new ActivityLog
        {
            JobId = j.JobId,  // Set the Job ID
            Message = $"Job {j.JobId} - {j.Status}",  // Create a message string for the job
            Timestamp = DateTime.Parse(j.ProcessedAt ?? j.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal) // Parse the date as UTC
        })
        .Concat(recentWorkOrders.Select(wo => new ActivityLog
        {
            JobId = wo.JobId,  // Set the Job ID from the work order
            Message = $"Work Order {wo.WorkOrderId} - {wo.Status}",  // Create a message string for the work order
            Timestamp = DateTime.Parse(wo.ProcessedAt ?? wo.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal) // Parse the date as UTC
        }))
        .OrderByDescending(a => a.Timestamp)  // Order the combined results by the Timestamp in descending order
        .Take(10)  // Limit the results to the top 10 most recent entries
        .ToList();  // Convert the final result to a list

        // Return the list of recent activities
        return recentActivity;
    }



    // Method to get job trends based on a specified period (daily, weekly, monthly)
    public async Task<IEnumerable<JobTrend>> GetJobTrendsAsync(string period)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Fetch recent jobs from the database where ProcessedAt or CreatedAt timestamps are not null or empty
        var recentJobs = await context.Jobs
            .Where(j => !string.IsNullOrEmpty(j.ProcessedAt) || !string.IsNullOrEmpty(j.CreatedAt))
            .Select(j => new
            {
                Status = j.Status,         // Select the Job Status
                ProcessedAt = j.ProcessedAt, // Select the ProcessedAt timestamp
                CreatedAt = j.CreatedAt    // Select the CreatedAt timestamp
            })
            .ToListAsync();  // Fetch data asynchronously from the database

        // Group the jobs by the specified period (daily, weekly, monthly) and their status
        var jobTrends = recentJobs
            .GroupBy(j => new
            {
                Date = period == "daily"
                    ? DateTime.Parse(j.ProcessedAt ?? j.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal).Date  // Group by day
                    : period == "weekly"
                        ? GetWeekStartDate(DateTime.Parse(j.ProcessedAt ?? j.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)) // Group by start of the week
                        : period == "monthly"
                            ? new DateTime(
                                DateTime.Parse(j.ProcessedAt ?? j.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal).Year,
                                DateTime.Parse(j.ProcessedAt ?? j.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal).Month, 1) // Group by start of the month
                            : DateTime.Parse(j.ProcessedAt ?? j.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal).Date, // Default to grouping by day if period is not specified
                j.Status  // Group by status
            })
            .Select(g => new JobTrend
            {
                Date = g.Key.Date,     // Set the grouped date
                Status = g.Key.Status, // Set the status for the group
                Count = g.Count()      // Count the number of jobs in this group
            })
            .ToList();  // Convert the grouped results to a list

        // Return the list of job trends
        return jobTrends;
    }



    // Method to calculate the start date of the week for a given date
    private DateTime GetWeekStartDate(DateTime date)
    {
        // Calculate the difference between the provided date's day of the week and Monday
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;

        // Subtract the difference from the provided date to get the Monday of the same week
        return date.AddDays(-1 * diff).Date;
    }



    // Method to get processing time trends based on a specified period (daily, weekly, monthly)
    public async Task<IEnumerable<ProcessingTimeTrend>> GetProcessingTimeTrendsAsync(string period)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Fetch work orders that have been processed and have valid CreatedAt and ProcessedAt timestamps
        var recentWorkOrders = await context.WorkOrders
            .Where(wo => wo.Status == "Processed" && !string.IsNullOrEmpty(wo.ProcessedAt) && !string.IsNullOrEmpty(wo.CreatedAt))
            .Select(wo => new
            {
                ProcessedAt = wo.ProcessedAt,
                CreatedAt = wo.CreatedAt
            })
            .ToListAsync();

        // Group the work orders by the specified period (daily, weekly, monthly) and calculate the average processing time
        var processingTimeTrends = recentWorkOrders
            .GroupBy(wo => new
            {
                Date = period == "daily"
                    ? DateTime.Parse(wo.ProcessedAt ?? wo.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal).Date
                    : period == "weekly"
                    ? GetWeekStartDate(DateTime.Parse(wo.ProcessedAt ?? wo.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal))
                    : period == "monthly"
                    ? new DateTime(DateTime.Parse(wo.ProcessedAt ?? wo.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal).Year, DateTime.Parse(wo.ProcessedAt ?? wo.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal).Month, 1)
                    : DateTime.Parse(wo.ProcessedAt ?? wo.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal).Date
            })
            .Select(g => new ProcessingTimeTrend
            {
                // Set the Date property to the group's key date
                Date = g.Key.Date,

                // Calculate the average processing time for the group
                AverageProcessingTime = g.Average(wo =>
                    (DateTime.Parse(wo.ProcessedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal) -
                     DateTime.Parse(wo.CreatedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)).TotalSeconds)
            })
            .ToList();

        // Return the list of processing time trends
        return processingTimeTrends;
    }


    // Method to get the system's uptime as a formatted string
    public string GetSystemUptime()
    {
        try
        {
            // Create a ManagementObjectSearcher to query the system's last boot-up time
            using (var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem WHERE Primary='true'"))
            {
                // Execute the query and get the first result
                var query = searcher.Get().OfType<ManagementObject>().FirstOrDefault();

                if (query != null)
                {
                    // Convert the last boot-up time from WMI format to a DateTime object
                    DateTime lastBootUp = ManagementDateTimeConverter.ToDateTime(query["LastBootUpTime"].ToString());

                    // Calculate the time span representing the system's uptime
                    TimeSpan uptimeSpan = DateTime.Now - lastBootUp;

                    // Format the uptime as a string in the format "XX days XX hours XX minutes XX seconds"
                    return string.Format("{0:D2} days {1:D2} hours {2:D2} minutes {3:D2} seconds",
                                         uptimeSpan.Days, uptimeSpan.Hours, uptimeSpan.Minutes, uptimeSpan.Seconds);
                }
            }

            // If the query did not return a result, return "Unknown uptime"
            return "Unknown uptime";
        }
        catch (Exception ex)
        {
            // If an error occurs, throw an exception with a custom message
            throw new Exception("Error retrieving system uptime.", ex);
        }
    }


    // Method to get the average CPU usage across all processors/cores as a float percentage
    public float GetCpuUsage()
    {
        try
        {
            // Create a ManagementObjectSearcher to query the CPU load percentage
            using (var searcher = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor"))
            {
                // Execute the query and get the results
                var queryResults = searcher.Get().OfType<ManagementObject>();

                // Check if any results were returned
                if (queryResults != null && queryResults.Any())
                {
                    // Variables to accumulate the total CPU load and count the processors
                    float totalLoad = 0;
                    int processorCount = 0;

                    // Iterate through the results to calculate the total load and count the processors
                    foreach (var query in queryResults)
                    {
                        totalLoad += Convert.ToSingle(query["LoadPercentage"]);
                        processorCount++;
                    }

                    // Calculate and return the average CPU load across all processors
                    return processorCount > 0 ? totalLoad / processorCount : -1;
                }
            }

            // Return -1 to indicate an error if no results were found
            return -1;
        }
        catch (Exception ex)
        {
            // If an error occurs, throw an exception with a custom message
            throw new Exception("Error retrieving CPU usage.", ex);
        }
    }



    // Method to get the memory usage as a percentage of total physical memory
    public float GetMemoryUsage()
    {
        try
        {
            // Create a ManagementObjectSearcher to query the free and total physical memory
            using (var searcher = new ManagementObjectSearcher("SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem"))
            {
                // Execute the query and get the first result
                var query = searcher.Get().OfType<ManagementObject>().FirstOrDefault();

                // Check if the query returned a result
                if (query != null)
                {
                    // Retrieve the free and total physical memory values from the query result
                    float freeMemory = Convert.ToSingle(query["FreePhysicalMemory"]);
                    float totalMemory = Convert.ToSingle(query["TotalVisibleMemorySize"]);

                    // Calculate the memory usage as a percentage of total memory
                    return (totalMemory - freeMemory) / totalMemory * 100;
                }
            }

            // Return -1 to indicate an error if no results were found
            return -1;
        }
        catch (Exception ex)
        {
            // If an error occurs, throw an exception with a custom message
            throw new Exception("Error retrieving memory usage.", ex);
        }
    }


    // Method to get the disk usage of the C: drive as a percentage of total disk space
    public float GetDiskUsage()
    {
        try
        {
            // Create a ManagementObjectSearcher to query the free and total disk space for the C: drive
            using (var searcher = new ManagementObjectSearcher("SELECT FreeSpace, Size FROM Win32_LogicalDisk WHERE DeviceID='C:'"))
            {
                // Execute the query and get the first result
                var query = searcher.Get().OfType<ManagementObject>().FirstOrDefault();

                // Check if the query returned a result
                if (query != null)
                {
                    // Retrieve the free and total disk space values from the query result
                    float freeSpace = Convert.ToSingle(query["FreeSpace"]);
                    float totalSpace = Convert.ToSingle(query["Size"]);

                    // Calculate the disk usage as a percentage of total disk space
                    return (totalSpace - freeSpace) / totalSpace * 100;
                }
            }

            // Return -1 to indicate an error if no results were found
            return -1;
        }
        catch (Exception ex)
        {
            // If an error occurs, throw an exception with a custom message
            throw new Exception("Error retrieving disk usage.", ex);
        }
    }


    // Asynchronously retrieves the status of a specified Windows service by its name
    public async Task<string> GetServiceStatusAsync(string serviceName)
    {
        // Run the service status retrieval operation on a separate thread
        return await Task.Run(() =>
        {
            // Create a ManagementObjectSearcher to query the status of the specified service
            using (var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_Service WHERE Name = '{serviceName}'"))
            {
                // Execute the query and get the first result
                var query = searcher.Get().OfType<ManagementObject>().FirstOrDefault();

                // Check if the query returned a result
                if (query != null)
                {
                    // Return the current state of the service (e.g., "Running", "Stopped")
                    return query["State"].ToString();
                }
            }

            // If the service was not found, return a message indicating this
            return "Stopped";
        });
    }

        // Implementing the StartServiceAsync method
        public async Task<bool> StartServiceAsync(string serviceName)
        {
            return await Task.Run(() =>
            {
                using (var serviceController = new ServiceController(serviceName))
                {
                    if (serviceController.Status == ServiceControllerStatus.Stopped)
                    {
                        try
                        {
                            serviceController.Start();
                            serviceController.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                            return true; // Service started successfully
                        }
                        catch (Exception ex)
                        {
                            // Handle error
                            return false;
                        }
                    }
                    return false; // Service already running or not found
                }
            });
        }
    



    // Asynchronously retrieves the health status of the default printer configured in the application settings
    public async Task<string> GetPrinterHealthStatusAsync()
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Step 1: Retrieve the default printer name from the ConfigurationSettings table
        // Query the configuration settings to find the value of the "DefaultPrinterName" key within the "AppSettings" category
        var defaultPrinterName = await context.ConfigurationSettings
            .Where(c => c.Key == "DefaultPrinterName" && c.Category == "AppSettings")
            .Select(c => c.Value)
            .FirstOrDefaultAsync();

        // If the default printer name is not configured, return an appropriate message
        if (string.IsNullOrEmpty(defaultPrinterName))
        {
            return "Default printer not configured";
        }
        

        // Step 2: Retrieve the printer settings from the PrinterSettings table using the default printer name
        // Query the PrinterSettings table to find the printer settings that match the default printer name
        var printerSetting = await context.PrinterSettings
            .Where(p => p.Name == defaultPrinterName)
            .FirstOrDefaultAsync();

        // If the printer is not found in the PrinterSettings table, return an appropriate message
        if (printerSetting == null)
        {
            return "Printer not found in PrinterSettings";
        }

        // If the printer name is not configured within the printer settings, return an appropriate message
        if (string.IsNullOrEmpty(printerSetting.Name))
        {
            return "Printer name not configured";
        }

        // Step 3: Use WMI to get the printer status asynchronously by passing the printer's name
        // Run the GetPrinterStatus method on a separate thread to retrieve the printer's status using WMI
        return await Task.Run(() => GetPrinterStatus(printerSetting.Name));
    }


    // Retrieves the status of the specified printer and other printers connected to the system
    private string GetPrinterStatus(string printerName)
    {
        // StringBuilder is used to accumulate the status messages
        StringBuilder result = new StringBuilder();

        try
        {
            // Step 1: Check the status of the specific configured printer
            // Query WMI to get information about the printer with the specified name
            string query = $"SELECT * FROM Win32_Printer WHERE Name = '{printerName.Replace("\\", "\\\\")}'";
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
            using (ManagementObjectCollection printers = searcher.Get())
            {
                bool printerFound = false;

                // Iterate through the results to find the configured printer
                foreach (ManagementObject printer in printers)
                {
                    printerFound = true; // Mark that the printer was found
                    var status = printer["PrinterStatus"]; // Retrieve the printer's status

                    if (status != null)
                    {
                        string statusString = status.ToString();
                        string statusDescription;

                        // Map the status code to a human-readable status description
                        switch (statusString)
                        {
                            case "3":
                                statusDescription = "Online";
                                break;
                            case "4":
                                statusDescription = "Offline";
                                break;
                            default:
                                statusDescription = "Unknown";
                                break;
                        }

                        // Log and append the status of the configured printer
                        string configuredPrinterStatus = $"Configured Printer '{printerName}' is {statusDescription}.";
                        _logger.LogInformation(configuredPrinterStatus);
                        result.AppendLine(configuredPrinterStatus);
                    }
                }

                // If the printer was not found via WMI, log and return a warning
                if (!printerFound)
                {
                    string notFoundMessage = $"'{printerName}' is configured in the database but was not set up on the PrintPlus server.";
                    _logger.LogWarning(notFoundMessage);
                    result.AppendLine(notFoundMessage);
                }
            }

            // Step 2: Check the status of all other printers connected to the application
            // Query WMI to get information about all printers
            query = "SELECT * FROM Win32_Printer";
            using (ManagementObjectSearcher allPrintersSearcher = new ManagementObjectSearcher(query))
            using (ManagementObjectCollection allPrinters = allPrintersSearcher.Get())
            {
                // Iterate through the results to find all printers
                foreach (ManagementObject printer in allPrinters)
                {
                    string name = printer["Name"]?.ToString(); // Retrieve the printer's name
                    var status = printer["PrinterStatus"]; // Retrieve the printer's status
                    string statusDescription = "Unknown";

                    if (status != null)
                    {
                        string statusString = status.ToString();
                        // Map the status code to a human-readable status description
                        switch (statusString)
                        {
                            case "3":
                                statusDescription = "Online";
                                break;
                            case "4":
                                statusDescription = "Offline";
                                break;
                            default:
                                statusDescription = "Unknown";
                                break;
                        }
                    }

                    // Skip the configured printer to avoid duplicate entries in the result
                    if (name != null && !name.Equals(printerName, StringComparison.OrdinalIgnoreCase))
                    {
                        string otherPrinterStatus = $"Printer '{name}' is {statusDescription}.";
                        _logger.LogInformation(otherPrinterStatus);
                        result.AppendLine(otherPrinterStatus);
                    }
                }
            }
        }
        catch (ManagementException ex)
        {
            // Log and return an error message if an exception occurs during WMI queries
            string errorMessage = $"Error retrieving printer statuses. Details: {ex.Message}";
            _logger.LogError(errorMessage);
            return errorMessage;
        }

        // Return the accumulated status messages
        return result.ToString().TrimEnd();
    }



    // Method to retrieve a specific log entry by its ID
    public async Task<LogEntry> GetLogByIdAsync(int id)
    {
        try
        {
            using var context = _context.CreateDbContext();
            //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

            // Query the Logs table to find the log entry with the specified ID
            var logEntry = await context.Logs
                .Where(log => log.Id == id) // Filter logs by the provided ID
                .Select(log => new LogEntry
                {
                    Id = log.Id,
                    Timestamp = log.Timestamp, // Retrieve the timestamp of the log entry
                    Level = log.Level, // Retrieve the severity level of the log entry (e.g., Info, Warning, Error)
                    Message = log.Message, // Retrieve the message associated with the log entry
                  //  Exception = log.Exception, // Retrieve any exception details stored in the log entry
                  //  ServiceName = log.ServiceName, // Retrieve the name of the service where the log entry was created
                    JobId = log.JobId, // Retrieve the job ID associated with the log entry (if applicable)
                    WorkOrderId = log.WorkOrderId, // Retrieve the work order ID associated with the log entry (if applicable)
                   // Properties = log.Properties // Retrieve any additional properties stored in the log entry
                })
                .FirstOrDefaultAsync(); // Return the first matching log entry or null if none found

            // Check if the log entry was found
            if (logEntry == null)
            {
                // Log a warning if no log entry was found with the specified ID
                _logger.LogWarning($"No log entry found with ID {id}.");
            }

            return logEntry; // Return the log entry or null if not found
        }
        catch (Exception ex)
        {
            // Log the error and include the exception details
            _logger.LogError(ex, $"Error occurred while retrieving log with ID {id}.");
            throw; // Re-throw the exception to be handled by the calling code
        }
    }




    // Method to retrieve log entries with optional filtering by severity level
    public async Task<List<LogEntry>> GetLogsAsync(int count = 100, string level = null)
    {
        try
        {
            using var context = _context.CreateDbContext();
            //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

            // Create a queryable object from the Logs table
            var query = context.Logs.AsQueryable();

            // If a severity level is provided, apply a filter to the query
            if (!string.IsNullOrEmpty(level))
            {
                query = query.Where(log => log.Level == level);
            }

            // Execute the query to retrieve logs with filtering and ordering
            var logs = await query
                .OrderByDescending(log => log.Timestamp)  // Order logs by timestamp in descending order (most recent first)
                .Take(count)  // Limit the number of logs returned based on the specified count
                .Select(log => new LogEntry
                {
                    Id = log.Id,
                    Timestamp = log.Timestamp, // Retrieve the timestamp of the log entry
                    Level = log.Level, // Retrieve the severity level of the log entry (e.g., Info, Warning, Error)
                    Message = log.Message, // Retrieve the message associated with the log entry
                   // Exception = log.Exception, // Retrieve any exception details stored in the log entry
                  //  ServiceName = log.ServiceName, // Retrieve the name of the service where the log entry was created
                    JobId = log.JobId, // Retrieve the job ID associated with the log entry (if applicable)
                    WorkOrderId = log.WorkOrderId, // Retrieve the work order ID associated with the log entry (if applicable)
                    //Properties = log.Properties // Retrieve any additional properties stored in the log entry
                })
                .ToListAsync(); // Execute the query asynchronously and convert the result to a list

            // Check if any logs were found
            if (logs == null || logs.Count == 0)
            {
                // Log a warning if no logs were found
                _logger.LogWarning("No logs found.");
            }

            return logs; // Return the list of log entries
        }
        catch (Exception ex)
        {
            // Log the error and include the exception details
            _logger.LogError(ex, "Error occurred while retrieving logs.");
            throw; // Re-throw the exception to be handled by the calling code
        }
    }

    /// <summary>
    /// Asynchronously adds a log entry to the database.
    /// </summary>
    /// <param name="logEntry">The log entry to be saved, containing details such as timestamp, level, message, exception, etc.</param>
    /// <returns>A Task representing the asynchronous operation of adding the log entry to the database.</returns>
    public async Task AddLogEntryAsync(LogEntry logEntry)
    {
        try
        {
            using var context = _context.CreateDbContext();
            //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

            // Add the log entry to the Logs table in the context.
            context.Logs.Add(logEntry);

            // Save the changes to the database asynchronously.
            if (_useSqlServer)
            {
                await context.SaveChangesAsync();
            }
            else
            {
                await SqliteWriteLock.RunWithWriteLock(async () =>
                {
                    await context.SaveChangesAsync();
                });
            }

            // Log a message indicating the log entry was saved successfully.
           // _logger.LogInformation("Log entry saved to database.");
        }
        catch (Exception ex)
        {
            // Log an error message if saving the log entry to the database fails.
            _logger.LogError(ex, "Failed to save log entry to database.");
        }
    }

    /// <summary>
    /// Retrieves user statistics by UserName.
    /// </summary>
    /// <param name="userName">The name of the user for whom to retrieve statistics.</param>
    /// <returns>A task that represents the asynchronous operation, containing the user's work order statistics.</returns>
    public async Task<UserWorkOrderStats> GetUserWorkOrderStatsAsync(string userName)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Query the database to find the user's work order statistics based on the UserName.
        // If found, it returns the UserWorkOrderStats object; otherwise, it returns null.
        return await context.UserWorkOrderStats
            .FirstOrDefaultAsync(u => u.UserName == userName);
    }

    /// <summary>
    /// Adds new user statistics to the UserWorkOrderStats table.
    /// </summary>
    /// <param name="userWorkOrderStats">The user work order statistics to add.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task AddUserWorkOrderStatsAsync(UserWorkOrderStats userWorkOrderStats)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Adds a new record to the UserWorkOrderStats table.
        await context.UserWorkOrderStats.AddAsync(userWorkOrderStats);
        // Saves the changes to the database.
        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }

    }

    /// <summary>
    /// Updates existing user statistics in the UserWorkOrderStats table.
    /// </summary>
    /// <param name="userWorkOrderStats">The user work order statistics to update.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task UpdateUserWorkOrderStatsAsync(UserWorkOrderStats userWorkOrderStats)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Updates an existing record in the UserWorkOrderStats table.
        context.UserWorkOrderStats.Update(userWorkOrderStats);
        // Saves the changes to the database.
        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }
    }


    /// <summary>
    /// Deletes work order parts that were created before a specified threshold date.
    /// </summary>
    /// <param name="thresholdDate">The date before which work order parts should be deleted.</param>
    public async Task DeleteOldWorkOrderPartsAsync(DateTime thresholdDate)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();
        // Retrieve all work order parts that have a creation date earlier than the threshold date.
        var allWorkOrderParts = await context.WorkOrderParts
            .Where(wop => wop.CreatedAt.CompareTo(thresholdDate.ToString("yyyy-MM-ddTHH:mm:ssZ")) < 0)
            .ToListAsync();

        // Further filter the work order parts to ensure the dates are parsed correctly and are indeed older than the threshold.
        var oldWorkOrderParts = allWorkOrderParts
            .Where(wop => DateTime.Parse(wop.CreatedAt) < thresholdDate)
            .ToList();

        // Remove the filtered old work order parts from the context.
        context.WorkOrderParts.RemoveRange(oldWorkOrderParts);

        // Save changes to the database to finalize the deletions.
        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }
    }

    /// <summary>
    /// Deletes work orders that were created before a specified threshold date.
    /// </summary>
    /// <param name="thresholdDate">The date before which work orders should be deleted.</param>
    public async Task DeleteOldWorkOrdersAsync(DateTime thresholdDate)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();
        // Retrieve all work orders that have a creation date earlier than the threshold date.
        var allWorkOrders = await context.WorkOrders
            .Where(w => w.CreatedAt.CompareTo(thresholdDate.ToString("yyyy-MM-ddTHH:mm:ssZ")) < 0)
            .ToListAsync();

        // Further filter the work orders to ensure the dates are parsed correctly and are indeed older than the threshold.
        var oldWorkOrders = allWorkOrders
            .Where(w => DateTime.Parse(w.CreatedAt) < thresholdDate)
            .ToList();

        // Remove the filtered old work orders from the context.
        context.WorkOrders.RemoveRange(oldWorkOrders);

        // Save changes to the database to finalize the deletions.
        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }
    }

    /// <summary>
    /// Deletes jobs that were created before a specified threshold date.
    /// </summary>
    /// <param name="thresholdDate">The date before which jobs should be deleted.</param>
    public async Task DeleteOldJobsAsync(DateTime thresholdDate)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Retrieve all jobs that have a creation date earlier than the threshold date.
        var allJobs = await context.Jobs
            .Where(j => j.CreatedAt.CompareTo(thresholdDate.ToString("yyyy-MM-ddTHH:mm:ssZ")) < 0)
            .ToListAsync();

        // Further filter the jobs to ensure the dates are parsed correctly and are indeed older than the threshold.
        var oldJobs = allJobs
            .Where(j => DateTime.Parse(j.CreatedAt) < thresholdDate)
            .ToList();

        // Remove the filtered old jobs from the context.
        context.Jobs.RemoveRange(oldJobs);

        // Save changes to the database to finalize the deletions.
        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }
    }

    /// <summary>
    /// Deletes logs that were created before a specified threshold date.
    /// </summary>
    /// <param name="thresholdDate">The date before which logs should be deleted.</param>
    public async Task DeleteOldLogsAsync(DateTime thresholdDate)
    {
        using var context = _context.CreateDbContext();
        //var context = scope.ServiceProvider.GetRequiredService<PrintPlusContext>();

        // Filter and delete logs directly in a single operation.
        var oldLogs = await context.Logs
            .Where(j => j.Timestamp < thresholdDate)
            .ToListAsync();

        // Remove the filtered old logs from the context.
        context.Logs.RemoveRange(oldLogs);

        // Save changes to the database to finalise the deletions.
        if (_useSqlServer)
        {
            await context.SaveChangesAsync();
        }
        else
        {
            await SqliteWriteLock.RunWithWriteLock(async () =>
            {
                await context.SaveChangesAsync();
            });
        }
    }




}







