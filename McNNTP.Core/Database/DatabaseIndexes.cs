// --------------------------------------------------------------------------------------------------------------------
// <copyright file="DatabaseIndexes.cs" company="Sean McElroy">
//   Copyright Sean McElroy, 2014.  All rights reserved.
// </copyright>
// <summary>
//   Utility for creating database indexes to optimize query performance
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace McNNTP.Core.Database
{
    using System;
    using System.Data.SQLite;
    using Microsoft.Extensions.Logging;
    using McNNTP.Data;

    /// <summary>
    /// Utility for creating database indexes to optimize query performance for millions of articles
    /// </summary>
    public static class DatabaseIndexes
    {
        /// <summary>
        /// Creates all necessary indexes for optimal query performance
        /// </summary>
        public static void CreatePerformanceIndexes(ILogger? logger = null)
        {
            var configuration = DatabaseUtility.CreateConfiguration();
            var connectionString = configuration.GetProperty("connection.connection_string");

            using var connection = new SQLiteConnection(connectionString);
            connection.Open();

            try
            {
                using var transaction = connection.BeginTransaction();

                // Article indexes
                ExecuteNonQuery(connection, transaction, 
                    "CREATE INDEX IF NOT EXISTS idx_article_messageid ON Article (MessageId)", logger);
                ExecuteNonQuery(connection, transaction, 
                    "CREATE INDEX IF NOT EXISTS idx_article_date ON Article (DateTimeParsed DESC)", logger);
                ExecuteNonQuery(connection, transaction, 
                    "CREATE INDEX IF NOT EXISTS idx_article_from ON Article (From)", logger);

                // ArticleNewsgroup indexes (critical for performance)
                ExecuteNonQuery(connection, transaction, 
                    "CREATE INDEX IF NOT EXISTS idx_articlenewsgroup_newsgroup_number ON ArticleNewsgroup (NewsgroupId, Number, Cancelled, Pending)", logger);
                ExecuteNonQuery(connection, transaction, 
                    "CREATE INDEX IF NOT EXISTS idx_articlenewsgroup_article ON ArticleNewsgroup (ArticleId)", logger);
                ExecuteNonQuery(connection, transaction, 
                    "CREATE INDEX IF NOT EXISTS idx_articlenewsgroup_cancelled ON ArticleNewsgroup (Cancelled, Pending)", logger);
                ExecuteNonQuery(connection, transaction, 
                    "CREATE INDEX IF NOT EXISTS idx_articlenewsgroup_number ON ArticleNewsgroup (Number)", logger);

                // Newsgroup indexes
                ExecuteNonQuery(connection, transaction, 
                    "CREATE UNIQUE INDEX IF NOT EXISTS idx_newsgroup_name ON Newsgroup (Name)", logger);
                ExecuteNonQuery(connection, transaction, 
                    "CREATE INDEX IF NOT EXISTS idx_newsgroup_createdate ON Newsgroup (CreateDate DESC)", logger);

                // User indexes
                ExecuteNonQuery(connection, transaction, 
                    "CREATE UNIQUE INDEX IF NOT EXISTS idx_user_username ON User (Username)", logger);

                // Subscription indexes
                ExecuteNonQuery(connection, transaction, 
                    "CREATE INDEX IF NOT EXISTS idx_subscription_user_newsgroup ON Subscription (UserId, NewsgroupId)", logger);

                transaction.Commit();
                logger?.LogInformation("Successfully created/verified all performance indexes");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error creating performance indexes");
                throw;
            }
            finally
            {
                connection.Close();
            }
        }

        /// <summary>
        /// Analyzes the database to update statistics for the query optimizer
        /// </summary>
        public static void AnalyzeDatabase(ILogger? logger = null)
        {
            var configuration = DatabaseUtility.CreateConfiguration();
            var connectionString = configuration.GetProperty("connection.connection_string");

            using var connection = new SQLiteConnection(connectionString);
            connection.Open();

            try
            {
                ExecuteNonQuery(connection, null, "ANALYZE", logger);
                logger?.LogInformation("Database analysis completed");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error analyzing database");
            }
            finally
            {
                connection.Close();
            }
        }

        /// <summary>
        /// Runs VACUUM to reclaim space and optimize the database file
        /// </summary>
        public static void VacuumDatabase(ILogger? logger = null)
        {
            var configuration = DatabaseUtility.CreateConfiguration();
            var connectionString = configuration.GetProperty("connection.connection_string");

            using var connection = new SQLiteConnection(connectionString);
            connection.Open();

            try
            {
                ExecuteNonQuery(connection, null, "VACUUM", logger);
                logger?.LogInformation("Database vacuum completed");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error vacuuming database");
            }
            finally
            {
                connection.Close();
            }
        }

        private static void ExecuteNonQuery(SQLiteConnection connection, SQLiteTransaction? transaction, string sql, ILogger? logger)
        {
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                if (transaction != null)
                {
                    command.Transaction = transaction;
                }
                command.ExecuteNonQuery();
                logger?.LogDebug("Executed: {Sql}", sql);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to execute: {Sql}", sql);
                // Don't throw - some indexes might already exist
            }
        }
    }
}
