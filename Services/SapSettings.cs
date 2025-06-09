using System;

namespace PrintPlusService.Services
{
    /// <summary>
    /// Represents the settings required for connecting to an SAP system.
    /// </summary>
    public class SapSettings
    {
        /// <summary>
        /// Gets or sets the SAP Gateway host.
        /// </summary>
        public string GatewayHost { get; set; }

        /// <summary>
        /// Gets or sets the SAP Gateway service.
        /// </summary>
        public string GatewayService { get; set; }

        /// <summary>
        /// Gets or sets the Program ID.
        /// </summary>
        public string ProgramID { get; set; }

        /// <summary>
        /// Gets or sets the repository destination.
        /// </summary>
        public string RepositoryDestination { get; set; }

        /// <summary>
        /// Gets or sets the destination.
        /// </summary>
        public string Destination { get; set; }

        /// <summary>
        /// Gets or sets the SAP user.
        /// </summary>
        public string User { get; set; }

        /// <summary>
        /// Gets or sets the SAP password.
        /// </summary>
        public string Password { get; set; }

        /// <summary>
        /// Gets or sets the client.
        /// </summary>
        public string Client { get; set; }

        /// <summary>
        /// Gets or sets the language.
        /// </summary>
        public string Language { get; set; }

        /// <summary>
        /// Gets or sets the system number.
        /// </summary>
        public string SystemNumber { get; set; }

        /// <summary>
        /// Gets or sets the pool size.
        /// </summary>
        public int PoolSize { get; set; }

        /// <summary>
        /// Gets or sets the maximum pool size.
        /// </summary>
        public int MaxPoolSize { get; set; }

        /// <summary>
        /// Gets or sets the idle timeout.
        /// </summary>
        public int IdleTimeout { get; set; }

        /// <summary>
        /// Gets or sets the trace level.
        /// </summary>
        public int Trace { get; set; }
    }
}
