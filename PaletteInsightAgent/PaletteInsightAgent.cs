using NLog;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Diagnostics;
using PaletteInsightAgent.CounterConfig;
using PaletteInsightAgent.Counters;
using PaletteInsightAgent.Sampler;
using PaletteInsightAgent.License;
using PaletteInsightAgent.LogPoller;
using PaletteInsightAgent.ThreadInfoPoller;
using PaletteInsightAgent.Output;
using PaletteInsightAgent.Configuration;
using PaletteInsightAgent.Output.OutputDrivers;
using PaletteInsightAgent.RepoTablesPoller;
using PaletteInsightAgent.Helpers;

[assembly: CLSCompliant(true)]

namespace PaletteInsightAgent
{
    /// <summary>
    /// A timer-based performance monitoring agent.  Loads a set of counters from a config file and polls them periodically, passing the results to a writer object.
    /// </summary>
    public class PaletteInsightAgent : IDisposable
    {
        //private Timer licenseCheckTimer;
        private Timer counterSampleTimer;
        private Timer logPollTimer;
        private Timer threadInfoTimer;
        private Timer webserviceTimer;
        private Timer repoTablesPollTimer;
        private Timer streamingTablesPollTimer;
        private LogPollerAgent logPollerAgent;
        private ThreadInfoAgent threadInfoAgent;
        private RepoPollAgent repoPollAgent;
        private CounterSampler sampler;
        private ITableauRepoConn tableauRepo;
        private IOutput output;
        private string tableauDataFolder;
        private readonly PaletteInsightAgentOptions options;
        //private LicenseGuard licenseGuard;
        private bool disposed;
        private const string PathToCountersYaml = @"Config\Counters.yml";
        private const int DBWriteLockAcquisitionTimeout = 10; // In seconds.
        private const int PollWaitTimeout = 1000;  // In milliseconds.
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private bool USE_COUNTERSAMPLES = true;
        private bool USE_LOGPOLLER = true;
        private bool USE_THREADINFO = true;

        // use the constant naming convention for now as the mutability
        // of this variable is temporary until the Db output is removed
        private bool USE_TABLEAU_REPO = true;
        private bool USE_STREAMING_TABLES = true;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1026:DefaultParametersShouldNotBeUsed")]
        public PaletteInsightAgent(bool loadOptionsFromConfig = true)
        {
            // Set the working directory
            Assembly assembly = Assembly.GetExecutingAssembly();
            Directory.SetCurrentDirectory(Path.GetDirectoryName(assembly.Location));

            APIClient.SetTrustSSL();

            // Load PaletteInsightAgentOptions.  In certain use cases we may not want to load options from the config, but provide them another way (such as via a UI).
            options = PaletteInsightAgentOptions.Instance;

            // Locate the Tableau data folder
            tableauDataFolder = Loader.FindTableauDataFolder();

            var configuration = Loader.LoadConfigFile("config/Config.yml");
            Loader.LoadConfigTo(configuration, tableauDataFolder, options);

            // Make sure that our HTTP client is initialized, because Splunk logger might be enabled
            // and it is using HTTP to send log messages to Splunk.
            APIClient.Init(options.WebserviceConfig);

            // Add the webservice username/auth token from the license
            Configuration.Loader.UpdateWebserviceConfigFromLicense(options);

            // NOTE: License check disabled as this project became open-source
            //// check the license after the configuration has been loaded.
            //licenseGuard = new LicenseGuard();
            //if (!licenseGuard.CheckLicense(options.LicenseKey))
            //{
            //    Log.Fatal("Invalid license! Exiting...");
            //    Environment.Exit(-1);
            //}

            // Showing the current version in the log
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
            string version = fvi.FileVersion;
            Log.Info("Palette Insight Agent version: " + version);

            USE_LOGPOLLER = options.UseLogPolling;
            USE_THREADINFO = options.UseThreadInfo;
            USE_COUNTERSAMPLES = options.UseCounterSamples;

            USE_TABLEAU_REPO = options.UseRepoPolling;
            USE_STREAMING_TABLES = options.UseStreamingTables;
            if (USE_TABLEAU_REPO != USE_STREAMING_TABLES)
            {
                // Having different setup for Tableau repo and streaming tables is not welcome
                Log.Error("Invalid repo poll configuration! Either both Tableau repo poll and streaming tables poll should be enabled, or neither! Repo poll: {0} vs. Streaming tables: {1}",
                    USE_TABLEAU_REPO, USE_STREAMING_TABLES);
            }

            if (USE_LOGPOLLER)
            {
                // Load the log poller config & start the agent
                logPollerAgent = new LogPollerAgent(options.LogFolders, options.LogLinesPerBatch);
            }

            if (USE_THREADINFO)
            {
                // start the thread info agent
                threadInfoAgent = new ThreadInfoAgent(options.ThreadInfoPollInterval);
            }

            Loader.AddRepoFromWorkgroupYaml(configuration, tableauDataFolder, options);
            tableauRepo = new Tableau9RepoConn(options.RepositoryDatabase, options.StreamingTablesPollLimit);
            repoPollAgent = new RepoPollAgent();
        }

        ~PaletteInsightAgent()
        {
            Dispose(false);
        }

        #region Public Methods

        /// <summary>
        /// Starts up the agent.
        /// </summary>
        public void Start()
        {
            Log.Info("Initializing PaletteInsightAgent..");

            // Assert that runtime options are valid.
            if (!PaletteInsightAgentOptions.Instance.Valid())
            {
                Log.Fatal("Invalid PaletteInsightAgent options specified!\nAborting..");
                return;
            }

            // NOTE: License check disabled as this project became open-source
            //// Check the license every day
            //var oneDayInMs = 24 * 60 * 60 * 1000;
            //licenseCheckTimer = new Timer(callback: licenseGuard.PollLicense, state: options.LicenseKey, dueTime: oneDayInMs, period: oneDayInMs);

            // only start the JMX if we want to
            if (USE_COUNTERSAMPLES)
            {
                ICollection<ICounter> counters;
                try
                {
                    counters = CounterConfigLoader.Load(PathToCountersYaml);
                }
                catch (ConfigurationErrorsException ex)
                {
                    Log.Error("Failed to correctly load '{0}': {1}\nAborting..", PathToCountersYaml, ex.Message);
                    return;
                }

                // Spin up counter sampler.
                sampler = new CounterSampler(counters);

                // Kick off the polling timer.
                Log.Info("PaletteInsightAgent initialized!  Starting performance counter polling..");
                counterSampleTimer = new Timer(callback: PollCounters, state: null, dueTime: 0, period: options.PollInterval * 1000);
            }


            if (USE_LOGPOLLER)
            {
                // Start the log poller agent
                logPollerAgent.start();
                logPollTimer = new Timer(callback: PollLogs, state: null, dueTime: 0, period: options.LogPollInterval * 1000);
            }

            if (USE_THREADINFO)
            {
                // Kick off the thread polling timer
                int dueTime = CalculateDueTime(options.ThreadInfoPollInterval);
                Log.Debug("Due time until the next best timing for thread info poll start: {0} msec", dueTime);
                threadInfoTimer = new Timer(callback: PollThreadInfo, state: null, dueTime: dueTime, period: options.ThreadInfoPollInterval * 1000);
            }

            // send the metadata if there is a tableau repo behind us
            if (IsConnectionToTableauRepoRequired())
            {
                // On start get the schema of the repository tables
                var table = tableauRepo.GetSchemaTable();

                // Add the metadata of the agent table to the schema table
                DataTableUtils.AddAgentMetadata(table);

                // Serialize schema table so that it gets uploaded with all other tables
                OutputSerializer.Write(table, true);

                // Do the same for index data
                table = tableauRepo.GetIndices();
                OutputSerializer.Write(table, true);
            }

            output = WebserviceOutput.MakeWebservice(options.WebserviceConfig);
            webserviceTimer = new Timer(callback: UploadData, state: output, dueTime: 0, period: options.UploadInterval * 1000);

            if (USE_TABLEAU_REPO)
            {
                // Poll Tableau repository data as well
                repoTablesPollTimer = new Timer(callback: PollFullTables, state: output, dueTime: 0, period: options.RepoTablesPollInterval * 1000);
            }

            if (USE_STREAMING_TABLES)
            {
                streamingTablesPollTimer = new Timer(callback: PollStreamingTables, state: output, dueTime: 0, period: options.StreamingTablesPollInterval * 1000);
            }
        }


        /// <summary>
        /// Stops the agent by disabling the timer.  Uses a write lock to prevent data from being corrupted mid-write.
        /// </summary>
        public void Stop()
        {
            Log.Info("Shutting down PaletteInsightAgent..");

            if (USE_COUNTERSAMPLES)
            {
                if (counterSampleTimer != null)
                {
                    counterSampleTimer.Dispose();
                }
            }

            if (USE_LOGPOLLER)
            {
                // Stop the log poller agent
                if (logPollTimer != null)
                {
                    logPollTimer.Dispose();
                    Log.Info("Stopping logPollTimer.");
                }
                logPollerAgent.stop();
            }

            if (USE_THREADINFO)
            {
                // Stop the thread info timer
                if (threadInfoTimer != null)
                {
                    threadInfoTimer.Dispose();
                }
            }

            if (webserviceTimer != null)
            {
                webserviceTimer.Dispose();
            }

            if (streamingTablesPollTimer != null)
            {
                streamingTablesPollTimer.Dispose();
            }

            if (repoTablesPollTimer != null)
            {
                repoTablesPollTimer.Dispose();
            }

            Log.Info("PaletteInsightAgent stopped.");
        }

        /// <summary>
        /// Indicates whether the agent is currently running (is initialized & has an active timer).
        /// </summary>
        /// <returns>Bool indicating whether the agent is currently running.</returns>
        public bool IsRunning()
        {
            var running = true;
            if (USE_COUNTERSAMPLES) running = running && (sampler != null && counterSampleTimer != null);
            if (USE_LOGPOLLER) running = running && (logPollTimer != null);
            if (USE_THREADINFO) running = running && (threadInfoTimer != null);
            running = running && (webserviceTimer != null);
            if (USE_TABLEAU_REPO) running = running && repoTablesPollTimer != null;
            if (USE_STREAMING_TABLES) running = running && streamingTablesPollTimer != null;
            return running;
        }

        /// <summary>
        /// Calculate when is going to be the next moment, when the "time is right" - let's call it a beat -
        /// to start the timer. Beats are calculated from the unix epoch base time, so that we can have the
        /// same beats across multiple machines.
        /// </summary>
        /// <param name="pollInterval"></param>
        /// <returns>Offset in milliseconds until the next beat to start the timer.</returns>
        public static int CalculateDueTime(int pollInterval)
        {
            int dueSeconds = GetNextTimerBeat(pollInterval);
            // It doesn't matter if now is not in UTC, because we only care about the seconds this time.
            var now = DateTime.Now;
            if (dueSeconds == 0)
            {
                if (now.Millisecond == 0)
                {
                    // Odd, but still.
                    return 0;
                }
                // Otherwise, it means that we just missed the beat.
                dueSeconds += pollInterval;
            }
            return dueSeconds * 1000 - now.Millisecond;
        }

        #endregion Public Methods

        #region Private Methods

        /// <summary>
        /// Polls the sampler's counters and writes the results to the writer object.
        /// </summary>
        /// <param name="stateInfo"></param>
        private void PollCounters(object stateInfo)
        {
            tryStartIndividualPoll(CounterSampler.InProgressLock, PollWaitTimeout, () =>
            {
                var sampleResults = sampler.SampleAll();
                OutputSerializer.Write(sampleResults, false);
            });
        }


        /// <summary>
        /// Polls the logs from tableau and inserts them into the database
        /// </summary>
        /// <param name="stateInfo"></param>
        private void PollLogs(object stateInfo)
        {
            tryStartIndividualPoll(LogPollerAgent.InProgressLock, PollWaitTimeout, () =>
            {
                logPollerAgent.pollLogs();
            });
        }

        /// <summary>
        /// Reads thread information from jmx and inserts them to the database
        /// </summary>
        /// <param name="stateInfo"></param>
        private void PollThreadInfo(object stateInfo)
        {
            tryStartIndividualPoll(ThreadInfoAgent.InProgressLock, PollWaitTimeout, () =>
            {
                Log.Info("Polling threadinfo");
                threadInfoAgent.poll(options.Processes, options.AllProcesses);
            });
        }

        /// <summary>
        /// Get Tableau repository tables from the database
        /// </summary>
        /// <param name="stateInfo"></param>
        private void PollFullTables(object stateInfo)
        {
            if (!IsTargetTableauRepoResident())
            {
                Log.Info("Target Tableau repo is not located on this computer. Skip polling full tables.");
                return;
            }

            tryStartIndividualPoll(RepoPollAgent.FullTablesInProgressLock, PollWaitTimeout, () =>
            {
                Log.Info("Polling Repository tables");
                repoPollAgent.PollFullTables(tableauRepo, options.RepositoryTables);
            });
        }

        /// <summary>
        /// Get Tableau repository streaming tables from the database
        /// </summary>
        /// <param name="stateInfo"></param>
        private void PollStreamingTables(object stateInfo)
        {
            if (!IsTargetTableauRepoResident())
            {
                Log.Info("Target Tableau repo is not located on this computer. Skip polling streaming tables.");
                return;
            }

            tryStartIndividualPoll(RepoPollAgent.StreamingTablesInProgressLock, PollWaitTimeout, () =>
            {
                Log.Info("Polling streaming tables");
                repoPollAgent.PollStreamingTables(tableauRepo, options.RepositoryTables, (IOutput)stateInfo);
            });
        }

        private List<string> GetTableauRepoNodes()
        {
            List<string> repoNodes = new List<string>();

            var workgroupYmlPath = Loader.GetWorkgroupYmlPath(tableauDataFolder);
            Loader.Workgroup workgroup = Loader.GetRepoFromWorkgroupYaml(workgroupYmlPath, options.PreferPassiveRepository);
            if (workgroup == null)
            {
                Log.Error("Failed to get Tableau repository nodes, because parsed workgroup is NULL!");
                return repoNodes;
            }

            if (workgroup.PgHost0 != null)
            {
                repoNodes.Add(workgroup.PgHost0);
            }
            if (workgroup.PgHost1 != null)
            {
                repoNodes.Add(workgroup.PgHost1);
            }
            if (repoNodes.Count == 0 && workgroup.Connection.Host != null)
            {
                repoNodes.Add(workgroup.Connection.Host);
            }

            Log.Info("Tableau repository node(s): {0}", string.Join(",", repoNodes.ToArray()));
            return repoNodes;
        }

        private bool IsTableauRepoNode(List<string> repoNodes)
        {
            if (repoNodes == null || repoNodes.Count == 0)
            {
                Log.Error("No repository node list was provided to check if this machine is a repository node or not!");
                return false;
            }

            // Only log an error if this node is meant to be a repo node at all
            foreach (var node in repoNodes)
            {
                try
                {
                    var repoHolder = Dns.GetHostEntry(node);
                    var localNode = Dns.GetHostName();
                    var localhost = Dns.GetHostEntry(localNode);

                    foreach (var repoAddress in repoHolder.AddressList)
                    {
                        if (IPAddress.IsLoopback(repoAddress))
                        {
                            Log.Info("This is a Tableau repo node. Repo address is the loopback address of this machine.");
                            return true;
                        }

                        foreach (var localAddress in localhost.AddressList)
                        {
                            Log.Info("Check for Tableau repo node: '{0}' -- local address: '{1}'", repoAddress, localAddress);
                            if (repoAddress.Equals(localAddress))
                            {
                                // This is a repo node
                                return true;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "Failed to match repo holder: '{0}' with localhost! Exception: ", node);
                }
            }

            Log.Info("This machine is not a Tableau repository node");
            return false;
        }

        /// <summary>
        /// Checks whether the target Tableau repository resides on this node.
        /// </summary>
        /// <returns></returns>
        private bool IsTargetTableauRepoResident()
        {
            List<string> repoNodes = GetTableauRepoNodes();
            if (!IsTableauRepoNode(repoNodes))
            {
                return false;
            }

            try
            {
                var isPassive = tableauRepo.isInRecoveryMode();
                if (isPassive && options.PreferPassiveRepository)
                {
                    Log.Info("This machine is the target Tableau repo node. Passive repo is preferred and this is the passive repo node.");
                    return true;
                }

                if (!isPassive)
                {
                    if (!options.PreferPassiveRepository)
                    {
                        Log.Info("This machine is the target Tableau repo node. Active repo is preferred and this is the active repo node.");
                        return true;
                    }

                    if (repoNodes.Count < 2) {
                        Log.Info("This machine is the target Tableau repo node. Passive is preferred, but this is the only repo node.");
                        return true;
                    }
                }
            }
            catch (Exception pgEx)
            {
                Log.Error(pgEx, "Failed to detect whether it is the target Tableau repo node!");
                return false;
            }

            Log.Info("This machine is not the target Tableau repo node");
            return false;
        }

        private bool IsConnectionToTableauRepoRequired()
        {
            return (USE_TABLEAU_REPO || USE_STREAMING_TABLES) && IsTargetTableauRepoResident();
        }

        private void UploadData(object stateInfo)
        {
            var thread = new Thread(() =>
            {
                if (!Monitor.TryEnter(FileUploader.FileUploadLock, FileUploader.fileUploadLockTimeout))
                {
                    Log.Debug("Skipping file upload as it is already in progress.");
                    return;
                }

                try
                {
                    FileUploader.Start((IOutput)stateInfo, options.ProcessedFilestTTL, options.StorageLimit);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error while uploading data to insight server.");
                }
                finally
                {
                    Monitor.Exit(FileUploader.FileUploadLock);
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Checks whether polling is in progress at the moment for a given poll method.
        /// If not, it executes the poll.
        /// </summary>
        private void tryStartIndividualPoll(object pollTypeLock, int timeout, Action pollDelegate)
        {
            if (!Monitor.TryEnter(pollTypeLock, timeout))
            {
                // Do not execute the poll delegate as it is already being executed.
                Log.Debug("Skipping poll as it is already in progress: " + pollTypeLock.ToString());
                return;
            }

            try
            {
                pollDelegate();
            }
            catch(Exception e)
            {
                Log.Error(e, "Exception during poll:{0}", e);
            }
            finally
            {
                // Ensure that the lock is released.
                Monitor.Exit(pollTypeLock);
            }
        }

        private static int GetNextTimerBeat(int pollInterval)
        {
            long nowTs = DateTimeConverter.ToTimestamp(DateTime.UtcNow);
            int modulo = (int)(nowTs % pollInterval);
            if (modulo == 0)
            {
                return 0;
            }

            return pollInterval - modulo;
        }

        #endregion Private Methods

        #region IDisposable Methods

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
                return;

            disposed = true;
        }

        #endregion IDisposable Methods
    }
}