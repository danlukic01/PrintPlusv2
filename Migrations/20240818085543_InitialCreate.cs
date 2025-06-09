using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PrintProcessingService.Migrations
{
    public partial class InitialCreate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create ConfigurationSettings table if it doesn't exist
            migrationBuilder.Sql(
                "CREATE TABLE IF NOT EXISTS ConfigurationSettings (" +
                "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                "Key TEXT, " +
                "Value TEXT, " +
                "Category TEXT);");

            // Create Jobs table if it doesn't exist
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS Jobs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    JobId TEXT NULL,
                    XmlFilePath TEXT NULL,
                    MegaFile TEXT NULL,
                    LangTemplate TEXT NULL,
                    Printer TEXT NULL,
                    Duplex TEXT NULL,
                    User TEXT NULL,
                    Password TEXT NULL,
                    Print TEXT NULL,
                    ErrorMessage TEXT NULL,
                    Status TEXT NULL,
                    CreatedAt TEXT NULL,
                    ProcessedAt TEXT NULL,
                    UniqueJobId TEXT NOT NULL,
                    CONSTRAINT AK_Jobs_UniqueJobId UNIQUE (UniqueJobId)
                );");

            // Create Logs table if it doesn't exist
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS Logs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT NOT NULL,
                    Level TEXT NULL,
                    Message TEXT NULL,
                    Exception TEXT NULL,
                    ServiceName TEXT NULL,
                    JobId TEXT NULL,
                    WorkOrderId TEXT NULL,
                    Properties TEXT NULL
                );");

            // Create PrinterSettings table if it doesn't exist
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS PrinterSettings (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NULL,
                    ShortName TEXT NULL,
                    PrintQueue TEXT NULL,
                    Ip TEXT NULL,
                    AltPrintPRN INTEGER NOT NULL,
                    PrintMF INTEGER NOT NULL,
                    DirectPrint INTEGER NOT NULL,
                    GeneratePRN INTEGER NOT NULL,
                    GenMF INTEGER NOT NULL,
                    Staple INTEGER NOT NULL
                );");

            // Create RFCSettings table if it doesn't exist
            migrationBuilder.Sql(@"
    CREATE TABLE IF NOT EXISTS RFCSettings (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        MSHOST TEXT NULL,
        MSSERV TEXT NULL,
        [GROUP] TEXT NULL,
        SYSID TEXT NULL,
        SYSNR TEXT NULL,
        User TEXT NULL,
        Password TEXT NULL,
        TRACE TEXT NULL,
        CLIENT TEXT NULL,
        LogonLang TEXT NULL
    );");

            // Create SapFolderSettings table if it doesn't exist
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS SapFolderSettings (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NULL,
                    User TEXT NULL,
                    Path TEXT NULL,
                    OutDir TEXT NULL,
                    SapPath TEXT NULL
                );");

            // Create Users table if it doesn't exist
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS Users (
                    UserId INTEGER PRIMARY KEY AUTOINCREMENT,
                    Username TEXT NULL,
                    Password TEXT NULL,
                    Role TEXT NULL,
                    CreatedAt TEXT NOT NULL
                );");

            // Create WorkOrders table if it doesn't exist
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS WorkOrders (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UniqueJobId TEXT NOT NULL,
                    JobId TEXT NULL,
                    WorkOrderId TEXT NOT NULL,
                    Objtyp TEXT NULL,
                    File TEXT NULL,
                    ErrorMessage TEXT NULL,
                    Status TEXT NULL,
                    CreatedAt TEXT NULL,
                    ProcessedAt TEXT NULL,
                    CONSTRAINT AK_WorkOrders_UniqueJobId_WorkOrderId UNIQUE (UniqueJobId, WorkOrderId),
                    FOREIGN KEY (UniqueJobId) REFERENCES Jobs (UniqueJobId) ON DELETE CASCADE
                );");

            // Create WorkOrderParts table if it doesn't exist
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS WorkOrderParts (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UniqueJobId TEXT NULL,
                    WorkOrderId TEXT NULL,
                    PartId TEXT NULL,
                    SeqId TEXT NULL,
                    Type TEXT NULL,
                    FilePath TEXT NULL,
                    ErrorMessage TEXT NULL,
                    Status TEXT NULL,
                    CreatedAt TEXT NULL,
                    ProcessedAt TEXT NULL,
                    FOREIGN KEY (UniqueJobId, WorkOrderId) REFERENCES WorkOrders (UniqueJobId, WorkOrderId) ON DELETE CASCADE
                );");

            // Create Index for WorkOrderParts table if it doesn't exist
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS IX_WorkOrderParts_UniqueJobId_WorkOrderId ON WorkOrderParts (UniqueJobId, WorkOrderId);");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfigurationSettings");

            migrationBuilder.DropTable(
                name: "Logs");

            migrationBuilder.DropTable(
                name: "PrinterSettings");

            migrationBuilder.DropTable(
                name: "RFCSettings");

            migrationBuilder.DropTable(
                name: "SapFolderSettings");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "WorkOrderParts");

            migrationBuilder.DropTable(
                name: "WorkOrders");

            migrationBuilder.DropTable(
                name: "Jobs");
        }
    }
}
