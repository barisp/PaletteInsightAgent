﻿using NLog;
using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using PaletteInsightAgent.Counters;
using PaletteInsightAgent.Helpers;

namespace PaletteInsightAgent.Sampler
{
    /// <summary>
    /// Concrete class that handles sampling all counters in a collection and mapping the results to the desired schema.
    /// </summary>
    internal sealed class CounterSampler
    {
        private const bool USE_STATIC_COLUMN_NAMES = true;
        public static readonly string InProgressLock = "Counter Sampler";
        public static readonly string TABLE_NAME = "countersamples";

        private readonly ICollection<ICounter> counters;
        private readonly DataTable schema;
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public CounterSampler(ICollection<ICounter> counterCollection)
        {
            counters = counterCollection;
            if (USE_STATIC_COLUMN_NAMES)
            {
                schema = makeCounterSamplesTable();
            }
            else
            {
                schema = GenerateSchema();
            }
        }

        #region Public Methods

        /// <summary>
        /// Polls all known counters and maps the results to the dynamic data model.
        /// </summary>
        /// <returns>DataTable of all samples mapped to dynamic data model.</returns>
        public DataTable SampleAll()
        {
            Log.Info("Polling..");
            var pollTimestamp = DateTime.UtcNow;

            // Create a new empty table to store results of this sampling, using our existing column schema.
            var dataTable = schema.Clone();

            // Sample all counters.
            foreach (var counter in counters)
            {
                // Retrieve sample for this counter.
                var counterSample = counter.Sample();

                // Bail out if we were unable to sample.
                if (counterSample == null) continue;

                // Map sample result to schema and insert it into result table.
                var row = MapToSchema(counterSample, dataTable);
                row["timestamp"] = pollTimestamp;
                dataTable.Rows.Add(row);
            }

            var numFailed = counters.Count - dataTable.Rows.Count;
            Log.Info("Finished polling {0} {1}. [{2} {3}]", counters.Count, "counter".Pluralize(counters.Count), numFailed, "failure".Pluralize(numFailed));

            return dataTable;
        }

        public ICollection<ICounter> getCounters()
        {
            return counters;
        }

        #endregion Public Methods

        #region Private Methods

        /// <summary>
        /// Generates a dynamic schema that can support all known counters.
        /// </summary>
        /// <param name="tableName">The name of the resulting data table.</param>
        /// <returns>DataTable that can accomodate both metadata and sample results of all counters managed by this sampler.</returns>
        private DataTable GenerateSchema()
        {
            var generatedSchema = new DataTable(TABLE_NAME);

            generatedSchema.Columns.Add(BuildColumnMetadata("timestamp", "System.DateTime", false));
            generatedSchema.Columns.Add(BuildColumnMetadata("machine", "System.String", false, 16));
            generatedSchema.Columns.Add(BuildColumnMetadata("category", "System.String", false, 64));

            foreach (var counter in counters)
            {
                var colName = toOracleColumnName(counter.Counter.ToSnakeCase());
                if (!generatedSchema.Columns.Contains(colName))
                {
                    generatedSchema.Columns.Add(BuildColumnMetadata(counter.Counter, "System.Double", true));
                }
            }
            generatedSchema.Columns.Add(BuildColumnMetadata("instance", "System.String", true, 64));

            Log.Debug("Dynamically built result schema '{0}'. [{1} {2}]",
                      TABLE_NAME, generatedSchema.Columns.Count, "column".Pluralize(generatedSchema.Columns.Count));
            return generatedSchema;
        }

        /// <summary>
        /// Builds a DataColumn for the given parameters.
        /// </summary>
        /// <param name="columnName">The name of the resulting column.</param>
        /// <param name="columnType">The type of data the column will contain.</param>
        /// <param name="isNullable">Whether or not this column supports nulls.</param>
        /// <param name="maxLength">The max length of this column.</param>
        /// <returns>DataColumn object with schema matching the given parameters.</returns>
        private static DataColumn BuildColumnMetadata(string columnName, string columnType, bool isNullable, int maxLength = -1)
        {
            var type = Type.GetType(columnType);
            if (type == null)
            {
                return null;
            }

            var column = new DataColumn(toOracleColumnName( columnName.ToSnakeCase()), type)
                {
                    AllowDBNull = isNullable
                };
            if (maxLength >= 1)
            {
                column.MaxLength = maxLength;
            }

            return column;
        }

        /// <summary>
        /// Maps a counter's metadata and sampled value to the table schema.
        /// </summary>
        /// <param name="sample">The counter sample to map.</param>
        /// <param name="tableSchema">The DataTable to map the counter to.</param>
        /// <returns>DataRow with all fields from the sample correctly mapped.</returns>
        private static DataRow MapToSchema(ICounterSample sample, DataTable tableSchema)
        {
            var row = tableSchema.NewRow();

            var counter = sample.Counter;
            row["machine"] = counter.HostName;
            row["category"] = counter.Category;
            
            if (USE_STATIC_COLUMN_NAMES)
            {
                row["name"] = counter.Counter;
                row["value"] = sample.SampleValue;
            }
            else
            {
                // Oracle has a 30 char limit for columnNames.
                row[toOracleColumnName(counter.Counter.ToSnakeCase())] = sample.SampleValue;
            }
            row["instance"] = counter.Instance;

            return row;
        }

        public static DataTable makeCounterSamplesTable()
        {

            var table = new DataTable(TABLE_NAME);

            //TableHelper.addColumn(table, "id", "System.Int32", true, true);
            TableHelper.addColumn(table, "timestamp", "System.DateTime");
            TableHelper.addColumn(table, "machine");
            TableHelper.addColumn(table, "category");
            TableHelper.addColumn(table, "instance");

            TableHelper.addColumn(table, "name");
            TableHelper.addColumn(table, "value", "System.Double");


            return table;


        }

        /// <summary>
        /// Converts a table to an oracle-compatible 30 char max underscored name.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private static string toOracleColumnName( string value )
        {
            if (value.Length <= 30) return value;

            // otherwises do the magic
            var hashVal = value.GetHashCode().ToString("x8");
            return String.Format("{0}_{1}", Truncate(value, 20), hashVal);
        }

        /// <summary>
        /// Left truncates a string.
        /// </summary>
        /// <param name="value"></param>
        /// <param name="maxLength"></param>
        /// <returns></returns>
        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }
        #endregion Private Methods
    }
}