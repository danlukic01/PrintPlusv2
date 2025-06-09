using CommonInterfaces.Models;
using Microsoft.EntityFrameworkCore;

namespace PrintPlusService.Services
{
    // This class represents the database context for the Print Processing Service.
    // It is responsible for managing the database connections and entities.
    public class PrintPlusContext : DbContext
    {
        // Constructor to initialize the DbContext with options, typically provided by dependency injection.
        public PrintPlusContext(DbContextOptions<PrintPlusContext> options) : base(options) { }

        // DbSet properties represent the tables in the database.
        public DbSet<Job> Jobs { get; set; } // Table for Job entities
        public DbSet<WorkOrder> WorkOrders { get; set; } // Table for WorkOrder entities
        public DbSet<WorkOrderPart> WorkOrderParts { get; set; } // Table for WorkOrderPart entities
        public DbSet<ConfigurationSettings> ConfigurationSettings { get; set; } // Table for configuration settings
        public DbSet<SapFolderSetting> SapFolderSettings { get; set; } // Table for SAP folder settings
        public DbSet<PrinterSetting> PrinterSettings { get; set; } // Table for printer settings
        public DbSet<RFCSettings> RFCSettings { get; set; } // Table for RFC settings
        public DbSet<User> Users { get; set; } // Table for Users
        public DbSet<LogEntry> Logs { get; set; } // Table for log entries

        // New DbSet property for the UserWorkOrderStats table
        public DbSet<UserWorkOrderStats> UserWorkOrderStats { get; set; } // Table for tracking user work order statistics

        // Configures the model relationships and table mappings when the model is being created.
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Map entities to their respective tables in the database
            modelBuilder.Entity<Job>().ToTable("Jobs");
            modelBuilder.Entity<WorkOrder>().ToTable("WorkOrders");
            modelBuilder.Entity<WorkOrderPart>().ToTable("WorkOrderParts");
            modelBuilder.Entity<ConfigurationSettings>().ToTable("ConfigurationSettings");
            modelBuilder.Entity<SapFolderSetting>().ToTable("SapFolderSettings");
            modelBuilder.Entity<PrinterSetting>().ToTable("PrinterSettings");
            modelBuilder.Entity<RFCSettings>().ToTable("RFCSettings");
            modelBuilder.Entity<User>().ToTable("Users");
            modelBuilder.Entity<LogEntry>().ToTable("Logs");
            modelBuilder.Entity<UserWorkOrderStats>().ToTable("UserWorkOrderStats"); // Map UserWorkOrderStats to the UserWorkOrderStats table

            // Define the relationship between WorkOrder and Job entities
            modelBuilder.Entity<WorkOrder>()
                .HasOne(wo => wo.Job) // A WorkOrder has one associated Job
                .WithMany(j => j.WorkOrders) // A Job can have multiple WorkOrders
                .HasForeignKey(wo => wo.UniqueJobId) // The foreign key in WorkOrder points to UniqueJobId in Job
                .HasPrincipalKey(j => j.UniqueJobId) // The principal key in Job is UniqueJobId
                .OnDelete(DeleteBehavior.Cascade); // Cascade delete to ensure related WorkOrders are deleted if the Job is deleted

            // Define the relationship between WorkOrderPart and WorkOrder entities
            modelBuilder.Entity<WorkOrderPart>()
                .HasOne(wop => wop.WorkOrder) // A WorkOrderPart has one associated WorkOrder
                .WithMany(wo => wo.WorkOrderParts) // A WorkOrder can have multiple WorkOrderParts
                .HasForeignKey(wop => new { wop.UniqueJobId, wop.WorkOrderId }) // The foreign key in WorkOrderPart points to UniqueJobId and WorkOrderId in WorkOrder
                .HasPrincipalKey(wo => new { wo.UniqueJobId, wo.WorkOrderId }) // The principal key in WorkOrder is a composite key of UniqueJobId and WorkOrderId
                .OnDelete(DeleteBehavior.Cascade); // Cascade delete to ensure related WorkOrderParts are deleted if the WorkOrder is deleted
        }
    }
}
