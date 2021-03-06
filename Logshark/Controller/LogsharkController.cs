﻿using log4net;
using Logshark.Controller.Extraction;
using Logshark.Controller.Metadata.Logset.Mongo;
using Logshark.Controller.Parsing;
using Logshark.Controller.Parsing.Validation;
using Logshark.Controller.Plugin;
using Logshark.Controller.Workbook;
using Logshark.Exceptions;
using Logshark.Mongo;
using System;
using System.Linq;
using System.Reflection;

namespace Logshark.Controller
{
    /// <summary>
    /// Handles the core functionalities of the Logshark library.
    /// </summary>
    public static class LogsharkController
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Display a list of all available plugins to the user.
        /// </summary>
        public static void PrintAvailablePlugins()
        {
            PluginLoader.PrintAvailablePlugins();
        }

        #region Pre-processing Tasks

        /// <summary>
        /// Handles any initialization tasks that need to happen with the Logshark request prior to processing a logset.
        /// </summary>
        public static void InitializeRequest(LogsharkRequest request)
        {
            Log.Info("Initializing Logshark..");

            // Purge temp directory of any data left over from aborted runs.
            PurgeTempDirectory();

            // Load all plugins required for this request.
            PluginLoader pluginLoader = new PluginLoader(request);
            request.RunContext.PluginTypesToExecute = pluginLoader.LoadPlugins();

            // Compute the hash of the logset we are working with.
            try
            {
                request.RunContext.LogsetHash = ComputeLogsetHash(request.Target);
            }
            catch (Exception ex)
            {
                Log.FatalFormat("Unable to determine logset hash: {0}", ex.Message);
                throw;
            }

            if (request.Target.IsHashId)
            {
                // Disable force parse as we aren't actually working with a logset payload.
                request.ForceParse = false;
            }
        }

        /// <summary>
        /// Computes a unique hash for the target logset.
        /// </summary>
        public static string ComputeLogsetHash(LogsharkRequestTarget target)
        {
            if (target.IsHashId)
            {
                return target;
            }

            Log.Info("Computing Logset Hash..");
            string logsetHash = LogsetHashUtil.GetLogSetHash(target);
            Log.InfoFormat("Logset hash is '{0}'.", logsetHash);

            return logsetHash;
        }

        /// <summary>
        /// Fires up a local MongoDB instance and updates the connection information in the request to point to it.
        /// </summary>
        /// <returns>LocalMongoProcessManager object for a Mongod process in the Running state.</returns>
        public static LocalMongoProcessManager StartLocalMongoDbInstance(LogsharkRequest request)
        {
            var localMongoProcessManager = new LocalMongoProcessManager(request.LocalMongoPort);

            if (request.Configuration.LocalMongoOptions.PurgeLocalMongoOnStartup)
            {
                localMongoProcessManager.PurgeData();
            }

            localMongoProcessManager.StartMongoProcess();

            // Update MongoConnectionInfo on the request to point to the local instance.
            request.Configuration.MongoConnectionInfo = localMongoProcessManager.GetConnectionInfo();

            return localMongoProcessManager;
        }

        #endregion Pre-processing Tasks

        #region Processing Tasks

        /// <summary>
        /// Unpacks the target logset and sets the root log directory. Contains logic to copy files locally if target is on a remote server.
        /// </summary>
        public static void ExtractLogFiles(LogsharkRequest request)
        {
            var extractor = new LogsetExtractor(request);
            extractor.Process();
        }

        /// <summary>
        /// Retrieves the status of any existing logsets that match the hash of this request.
        /// </summary>
        public static LogsetStatus GetExistingLogsetStatus(LogsharkRequest request)
        {
            return LogsetProcessingStatusChecker.GetStatus(request);
        }

        /// <summary>
        /// Creates the appropriate collections & indexes, then parses the target logset into Mongo.
        /// </summary>
        public static void ParseLogset(LogsharkRequest request)
        {
            var mongoWriter = new MongoWriter(request);
            mongoWriter.ProcessLogset();
        }

        /// <summary>
        /// Validates that a parsed logset contains at least one record.
        /// </summary>
        public static void ValidateMongoDatabaseContainsData(LogsharkRequest request)
        {
            if (!LogsetValidator.MongoDatabaseContainsRecords(request))
            {
                throw new ProcessingException(String.Format("Mongo database {0} contains no valid log data!", request.RunContext.MongoDatabaseName));
            }
        }

        #endregion Processing Tasks

        #region Post-processing Tasks

        /// <summary>
        /// Executes requested Logshark plugins.
        /// </summary>
        public static void ExecutePlugins(LogsharkRequest request)
        {
            PluginExecutor executor = new PluginExecutor(request);
            executor.ExecutePlugins();
        }

        /// <summary>
        /// Display a summary of the run to the user, including locations of any assets.
        /// </summary>
        public static void DisplayRunSummary(LogsharkRequest request)
        {
            // Display logset hash, if relevant.
            if (!String.IsNullOrWhiteSpace(request.RunContext.LogsetHash))
            {
                Log.InfoFormat("Logset hash for this run was '{0}'.", request.RunContext.LogsetHash);
            }
            
            // Display Postgres output location, if relevant.
            int pluginSuccesses = request.RunContext.PluginResponses.Count(pluginResponse => pluginResponse.SuccessfulExecution);
            if (pluginSuccesses > 0)
            {
                Log.InfoFormat("Plugin backing data was written to Postgres database '{0}\\{1}'.", request.Configuration.PostgresConnectionInfo, request.PostgresDatabaseName);
            }

            // A plugin may run successfully, yet not output a workbook.  We only want to display the workbook output location if at least one workbook was output.
            int workbooksOutput = request.RunContext.PluginResponses.Sum(pluginResponse => pluginResponse.WorkbooksOutput.Count);
            if (workbooksOutput > 0)
            {
                Log.InfoFormat("Plugin workbook output was saved to '{0}'.", PluginExecutor.GetOutputLocation(request.RunId));
            }

            // Display information about any published workbooks, if relevant.
            if (request.PublishWorkbooks && pluginSuccesses > 0)
            {
                Log.Info(WorkbookPublisher.BuildPublishingSummary(request.RunContext.PublishedWorkbooks));
            }
        }

        #endregion Post-processing Tasks

        #region Cleanup Tasks

        /// <summary>
        /// Performs any teardown tasks.
        /// </summary>
        public static void TearDown(LogsharkRequest request)
        {
            // Drop logset if user didn't want to retain it, assuming they didn't piggyback on an existing processed logset.
            if (request.DropMongoDBPostRun && !request.RunContext.UtilizedExistingProcessedLogset)
            {
                try
                {
                    Log.InfoFormat("Dropping Mongo database {0}..", request.RunContext.MongoDatabaseName);
                    MongoAdminUtil.DropDatabase(request.Configuration.MongoConnectionInfo.GetClient(), request.RunContext.MongoDatabaseName);

                    // Remove metadata record for this run from master metadata DB.
                    LogsetMetadataWriter metadataWriter = new LogsetMetadataWriter(request);
                    metadataWriter.DeleteMasterMetadataRecord();
                }
                catch (Exception ex)
                {
                    Log.ErrorFormat("Failed to clean up DB {0}: {1}", request.RunContext.MongoDatabaseName, ex.Message);
                }
            }

            LogsetExtractor.CleanUpRun(request.RunId);
        }

        /// <summary>
        /// Shutdown a local MongoDB instances, if it is currently running.
        /// </summary>
        public static void ShutDownLocalMongoDbInstance(LocalMongoProcessManager localMongoProcessManager)
        {
            if (localMongoProcessManager != null && localMongoProcessManager.IsMongoRunning())
            {
                Log.InfoFormat("Shutting down local MongoDB process..");
                localMongoProcessManager.KillAllMongoProcesses();
            }
        }

        /// <summary>
        /// Cleans out all contents of the application temp directory.
        /// </summary>
        public static void PurgeTempDirectory()
        {
            // Purge any "temp" files left over from previous runs.
            LogsetExtractor.CleanUpAll();
        }

        #endregion Cleanup Tasks
    }
}