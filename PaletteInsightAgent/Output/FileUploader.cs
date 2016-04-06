﻿using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PaletteInsightAgent.Output
{
    class FileUploader
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        /// <summary>
        /// The directory we store the succesfully uploaded files
        /// </summary>
        private const string PROCESSED_PREFIX = @"processed/";
        /// <summary>
        /// The directory where the files that have errors (invalid names, etc.)
        /// </summary>
        private const string ERROR_PREFIX = @"errors/";

        private static string DataFilePattern
        {
            get
            {
                return "*" + OutputSerializer.Extension;
            }
        }

        private static string ProcessedPath
        {
            get
            {
                return Path.Combine(OutputSerializer.DATA_FOLDER, PROCESSED_PREFIX);
            }
        }

        private static string ErrorPath
        {
            get
            {
                return Path.Combine(OutputSerializer.DATA_FOLDER, ERROR_PREFIX);
            }
        }

        public static readonly object FileUploadLock = new object();
        public static readonly int fileUploadLockTimeout = 1000;

        /// <summary>
        /// Start a single write loop
        /// </summary>
        /// <param name="output"></param>
        public static void Start(IOutput output, int processedFilesTTL, long storageLimit)
        {
            DoUpload(output, OutputSerializer.DATA_FOLDER);
            DeleteOldFiles(ProcessedPath, processedFilesTTL);
            // Storage limit is given in megabytes in config, but we will work with
            // bytes while applying the storage limit
            ApplyStorageLimit(storageLimit * 1024 * 1024);
        }

        private static void DeleteOldFiles(string from, int ttl)
        {
            if (!Directory.Exists(from))
            {
                // No directory, no old files in there.
                return;
            }

            Directory.EnumerateFiles(from)
                .Select(f => new FileInfo(f))
                .Where(f => f.CreationTime < DateTime.Now.AddSeconds(-ttl))
                .ToList()
                .ForEach(f => f.Delete());
        }

        /// <summary>
        /// Deletes oldest file so that storage size can fit in the configured
        /// value. For example if the storage limit is set to 1Gb and the
        /// size of the stored files are larger than 1Gb, then oldest files
        /// will be started to be deleted, until the size of the stored files
        /// are less than half of the configured limit (which is 500 Mb in this
        /// example).
        /// </summary>
        /// <param name="storageLimitInBytes">Given in bytes</param>
        private static void ApplyStorageLimit(long storageLimitInBytes)
        {
            IList<FileInfo> storedFiles = CollectStoredFiles();

            long cumulatedSize = 0;
            foreach (var file in storedFiles)
            {
                cumulatedSize += file.Length;
            }

            if (storageLimitInBytes > cumulatedSize)
            {
                // We are within limits, no need to do anything this time.
                return;
            }

            // Delete files while we are getting well below of the storage limit.
            // Well below here means the half of the limit.
            foreach (var file in storedFiles)
            {
                try
                {
                    // Directory separators are not included in the directory name
                    if (!file.DirectoryName.EndsWith(PROCESSED_PREFIX.TrimEnd(new char[] { '\\', '/' })))
                    {
                        // Deleting an already processed file is not a big deal, but deleting
                        // other files means dataloss.
                        Log.Warn("Deleting unprocessed file because of storage limit: {0} File creation time: {1}",
                            file.FullName, file.CreationTimeUtc);
                    }
                    File.Delete(file.FullName);

                    cumulatedSize -= file.Length;
                    if (cumulatedSize <= storageLimitInBytes / 2)
                    {
                        // We have deleted enough files to get well below the storage limit.
                        break;
                    }
                }
                catch (Exception e)
                {
                    Log.Error("Failed to delete file {0} while applying storage limit! Error message: {1}",
                        file.FullName, e.Message);
                }                
            }
        }

        /// <summary>
        /// Returns an ordered list of files found in "processed", "error"
        /// and "unsent" folders. The first item of the resulting list
        /// is the oldest file.
        /// </summary>
        private static IList<FileInfo> CollectStoredFiles()
        {
            // Collect all the stored files (processed, unsent, error) into an ordered
            // list, where the first item is going to be the oldest file.
            IList<string> folders = new List<string>(new string[] { ProcessedPath, ErrorPath });

            return folders.Where(folder => Directory.Exists(folder))
                .SelectMany(folder => Directory.EnumerateFiles(folder))
                .Select(file => new FileInfo(file))
                .OrderBy(file => file.CreationTimeUtc)
                .ToList();
        }

        private static IList<string> GetPendingTables(string from)
        {
            return Directory.EnumerateFiles(from)
                .Select(f => new FileInfo(f).Name)
                .Where(fileName => fileName.Contains('-'))
                .Select(fileName => fileName.Split('-')[0])
                .Where(tableName => tableName.Length > 0)
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// Implementation of sending all data files from a directory
        /// </summary>
        /// <param name="output"></param>
        /// <param name="dataPath"></param>
        private static void DoUpload(IOutput output, string dataPath)
        {
            try
            {
                // The old code (a while loop) gets stuck if we use the 'unsent' folder
                // as a source.
                var tableNames = GetPendingTables(dataPath);
                MoveAllFiles(OutputWriteResult.Aggregate( tableNames, (table) => {
                    return output.Write(GetFilesOfTable(dataPath, table));
                }));
            }
            catch (DirectoryNotFoundException)
            {
                // This means that the data folder does not exist, which also means that
                // there are no data files to process.
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to write data files to database! Exception message: {0}", e);
            }
        }

        /// <summary>
        /// Moves all files of an output write to their proper locations
        /// </summary>
        /// <param name="processedFiles"></param>
        private static void MoveAllFiles(OutputWriteResult processedFiles)
        {
            // Move files to processed folder
            MoveToFolder(processedFiles.successfullyWrittenFiles, ProcessedPath);
            // Move files with errors to the errors folder
            MoveToFolder(processedFiles.failedFiles, ErrorPath);
        }

        /// <summary>
        /// Gets all data files from a dataPath with the prefix specified by table.
        /// </summary>
        /// <param name="dataPath"></param>
        /// <param name="table"></param>
        /// <returns></returns>
        private static IList<string> GetFilesOfTable(string dataPath, string table)
        {
            // Remove those files that are still being written.
            return Directory.GetFiles(dataPath, table + "-" + DataFilePattern)
                            .Where(fileName => !fileName.Contains(OutputSerializer.IN_PROGRESS_FILE_POSTFIX))
                            .ToList();
        }

        #region Deprecated, but used in tests

        /// <summary>
        /// DbWriterTests uses this function with this signature.
        /// </summary>
        /// <returns></returns>
        public static IList<string> GetFilesOfSameTable()
        {
            return GetFilesOfSameTable(OutputSerializer.DATA_FOLDER);
        }

        /// <summary>
        /// Gives back a list of files for the same table, empty list otherwise 
        /// </summary>
        /// <param name="dataPath">The directory of data files</param>
        /// <returns></returns>
        public static IList<string> GetFilesOfSameTable(string dataPath)
        {
            var allFiles = Directory.GetFiles(dataPath, DataFilePattern);
            if (allFiles.Length == 0)
            {
                return new List<string>();
            }
            var pattern = GetTableName(allFiles[0]);
            if (pattern == "")
            {
                return new List<string>();
            }

            // Remove those files that are still being written.
            return Directory.GetFiles(dataPath, pattern + "-" + DataFilePattern)
                            .Where(fileName => !fileName.Contains(OutputSerializer.IN_PROGRESS_FILE_POSTFIX))
                            .ToList();
        }


        public static string GetFileName(string fullFileName)
        {
            var tokens = fullFileName.Split('/');
            if (tokens.Length == 0)
            {
                return "";           
            }
            return tokens[tokens.Length - 1];
        }

        // FileName: tableName-otherstuff.ext
        // Return tableName
        // If FileName is not in the given format return ""
        public static string GetTableName(string fullFileName)
        {
            var fileName = GetFileName(fullFileName);
            var tokens = fileName.Split('-');
            if (tokens.Length == 1)
            {
                // If the file name does not contain any delimiters,
                // then it must be some error since we are creating
                // these files.
                Log.Error("Failed to extract table name from file name: {0}", fullFileName);
                return "";
            }
            return tokens[0];
        }

        #endregion

        /// <summary>
        /// Helper method to move a list of files to a new folder
        /// </summary>
        /// <param name="fileList"></param>
        /// <param name="outputFolder"></param>
        private static void MoveToFolder(IList<string> fileList, string outputFolder)
        {
            foreach (var fullFileName in fileList)
            {
                try
                {
                    // create the output directory
                    if (!Directory.Exists(outputFolder))
                        Directory.CreateDirectory(outputFolder);

                    var fileName = GetFileName(fullFileName);
                    Log.Debug("Trying to move: {0}", fileName);
                    var targetFile = Path.Combine(outputFolder, fileName);

                    // If we are trying to move from unsent to unsent we may find
                    // that we have the same filename twice
                    if (Path.GetFullPath(targetFile) == Path.GetFullPath(fullFileName))
                    {
                        Log.Debug("Skipping moving a file to itself: {0}", fullFileName);
                        continue;
                    }
                    
                    // Delete the output if it already exists
                    // TODO: shouldnt we rename the old file here?
                    if (File.Exists(targetFile))
                        File.Delete(targetFile);

                    // Do the actual move
                    File.Move(fullFileName, targetFile);
                    Log.Info("Moved file: {0} to {1}", fileName, outputFolder);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Exception while moving file {0} to {1}: {2}", fullFileName, outputFolder, ex);
                }
            }
        }

        internal static void MoveToProcessed(IList<string> testFileList)
        {
            MoveToFolder(testFileList, ProcessedPath);
        }

    }
}