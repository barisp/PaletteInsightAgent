﻿using System;
using System.Collections.Generic;
using PaletteInsightAgent.Helpers;
using PaletteInsightAgent.Output;
using PaletteInsight.Configuration;

namespace PaletteInsightAgent
{
    /// <summary>
    /// Encapsulates runtime options for PaletteInsightAgent.
    /// </summary>
    public class PaletteInsightAgentOptions
    {
        public struct LogFolderInfo
        {
            public string FolderToWatch { get; set; }
            public string DirectoryFilter { get; set; }
        }
        [CLSCompliant(false)]
        public IDbConnectionInfo ResultDatabase { get; set; }
        public int PollInterval { get; set; }
        public int LogPollInterval { get; set; }
        public int ThreadInfoPollInterval { get; set; }
        public int DBWriteInterval { get; set; }
        public bool AllProcesses { get; set; }
        private static PaletteInsightAgentOptions instance;
        private const int MinPollInterval = 1; // In seconds.
        public IDictionary<string, ProcessData> Processes { get; set; }

        public ICollection<LogFolderInfo> LogFolders { get; set; }

        #region Repo properties

        public string RepoHost { get; set; }
        public Int32 RepoPort { get; set; }
        public string RepoUser { get; set; }
        public string RepoPass { get; set; }
        public string RepoDb { get; set; }

        #endregion Repo properties

        #region Singleton Constructor/Accessor

        private PaletteInsightAgentOptions()
        {
            LogFolders = new List<LogFolderInfo>();
        }

        public static PaletteInsightAgentOptions Instance
        {
            get { return instance ?? (instance = new PaletteInsightAgentOptions()); }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Indicates whether the current options configuration is valid or not.
        /// </summary>
        /// <returns>True if current options are all valid.</returns>
        public bool Valid()
        {
            // removed the host from here as we arent using that functionality
            return PollInterval >= MinPollInterval
                && LogPollInterval >= MinPollInterval
                && ThreadInfoPollInterval >= MinPollInterval
                && DBWriteInterval >= MinPollInterval;
        }

        public override string ToString()
        {
            return String.Format("[PollInterval='{0}', LogPollInterval='{1}', ThreadInfoPollInterval='{2}'], DBWriteInterval='{3}'",
                                   PollInterval, LogPollInterval, ThreadInfoPollInterval, DBWriteInterval);
        }

        #endregion
    }
}