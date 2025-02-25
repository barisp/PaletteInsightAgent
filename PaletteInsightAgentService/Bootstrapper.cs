﻿using System;
using PaletteInsightAgent;
using NLog;

namespace PaletteInsightAgentService
{
    /// <summary>
    /// Serves as a thin bootstrapper for the PaletteInsightAgent class and adapts underlying Stop/Start methods to the service context.
    /// </summary>
    public class PaletteInsightAgentServiceBootstrapper : Topshelf.ServiceControl, IDisposable
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private PaletteInsightAgent.PaletteInsightAgent agent;
        private bool disposed;

        ~PaletteInsightAgentServiceBootstrapper()
        {
            Dispose(false);
        }

        #region Public Methods

        /// <summary>
        /// Creates an instance of the PaletteInsightAgent and starts it.
        /// </summary>
        /// <param name="hostControl">Service HostControl object</param>
        /// <returns>Indicator that service succesfully started</returns>
        public bool Start(Topshelf.HostControl hostControl)
        {
            // Request additional time from the service host due to how much initialization has to take place.
            hostControl.RequestAdditionalTime(TimeSpan.FromSeconds(10));

            // Initialize and start service.
            try
            {
                agent = new PaletteInsightAgent.PaletteInsightAgent();
                // move starting the agent here, so exceptions get properly logged not only on construction,
                // but on start  also
                agent.Start();

                // check the isRunning flag as this place stopped the execution numerous times
                // without logging that the IsRunning() call returned false
                var isRunning = agent.IsRunning();
                if (!isRunning)
                    Log.Error("The agent seems to be not running: IsRunning() returned false right after .Start()");

                return isRunning;
            }
            catch (Exception e)
            {
                Log.Fatal(e, "Exception is: {0}", e);
                return false;
            }
        }

        /// <summary>
        /// Stops the PaletteInsightAgent service.
        /// </summary>
        /// <param name="hostControl">Service HostControl object</param>
        /// <returns>Indicator that service succesfully stopped</returns>
        public bool Stop(Topshelf.HostControl hostControl)
        {
            if (agent != null)
            {
                agent.Stop();
                agent.Dispose();
            }
            return true;
        }

        #endregion

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

            if (disposing)
            {
                agent.Dispose();
            }
            disposed = true;
        }

        #endregion
    }
}