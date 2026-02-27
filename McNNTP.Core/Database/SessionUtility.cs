// --------------------------------------------------------------------------------------------------------------------
// <copyright file="SessionUtility.cs" company="Sean McElroy">
//   Copyright Sean McElroy, 2014.  All rights reserved.
// </copyright>
// <summary>
//   A utility class that provides assistance managing and consuming NHibernate database sessions
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace McNNTP.Core.Database
{
    using System;
    using System.Diagnostics.Contracts;
    using McNNTP.Data;
    using NHibernate;
    using NHibernate.Cfg;

    /// <summary>
    /// A utility class that provides assistance managing and consuming NHibernate database sessions.
    /// </summary>
    public static class SessionUtility
    {
        /// <summary>
        /// A singleton instance of an NHibernate <see cref="ISessionFactory"/> built from the
        /// configuration of the application.
        /// </summary>
        private static readonly Lazy<ISessionFactory> SessionFactory = new Lazy<ISessionFactory>(() =>
        {
            var configuration = DatabaseUtility.CreateConfiguration();

            // Enable second-level cache for performance
            configuration.SetProperty("cache.use_second_level_cache", "true");
            configuration.SetProperty("cache.provider_class", "NHibernate.Cache.HashtableCacheProvider");

            // Enable query cache
            configuration.SetProperty("cache.use_query_cache", "true");

            // Optimize batch size for bulk operations
            configuration.SetProperty("adonet.batch_size", "50");

            // Prepare SQL at startup
            configuration.SetProperty("prepare_sql", "true");

            configuration.AddAssembly(typeof(Newsgroup).Assembly);

            return configuration.BuildSessionFactory();
        });

        /// <summary>
        /// Builds a new session from the NHibernate session factory.
        /// </summary>
        /// <returns>A new session from the NHibernate session factory.</returns>
        [Pure]
        public static ISession OpenSession() => SessionFactory.Value.OpenSession();

        /// <summary>
        /// Builds a new stateless session for high-performance read operations.
        /// Stateless sessions are faster but don't support lazy loading or caching.
        /// </summary>
        /// <returns>A new stateless session from the NHibernate session factory.</returns>
        [Pure]
        public static IStatelessSession OpenStatelessSession() => SessionFactory.Value.OpenStatelessSession();
    }
}
