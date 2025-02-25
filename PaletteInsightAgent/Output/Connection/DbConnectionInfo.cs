﻿namespace PaletteInsightAgent.Output
{
    /// <summary>
    /// Contains all of the information associated with a database connection.
    /// </summary>
    public class DbConnectionInfo
    {
        public string Server { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string DatabaseName { get; set; }
        public int CommandTimeout { get; set; }

        /// <summary>
        /// Indicates whether the current connection information is well-formed.
        /// </summary>
        /// <returns>True if this connection information is valid.</returns>
        public bool Valid()
        {
            return Server != null &&
                   Port > 0 &&
                   Port <= 65535 &&
                   Username != null &&
                   Password != null &&
                   DatabaseName != null &&
                   CommandTimeout > 0;
        }
    }
}