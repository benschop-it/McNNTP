// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ArticleRetriever.cs" company="Sean McElroy">
//   Copyright Sean McElroy, 2014.  All rights reserved.
// </copyright>
// <summary>
//   High-performance article retrieval with caching support
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace McNNTP.Core.Server.NNTP
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Threading.Tasks;
    using McNNTP.Core.Database;
    using McNNTP.Core.Server.Cache;
    using McNNTP.Data;
    using NHibernate.Linq;
    using System.Linq;

    /// <summary>
    /// High-performance article retrieval with caching support
    /// </summary>
    internal static class ArticleRetriever
    {
        /// <summary>
        /// Retrieves an article by message ID with cache support
        /// </summary>
        public static async Task<ArticleNewsgroup?> GetArticleByMessageIdAsync(
            string messageId,
            ArticleCache cache)
        {
            // Try cache first
            if (cache.TryGetArticleByMessageId(messageId, out var cachedArticle))
            {
                return cachedArticle;
            }

            // Fetch from database
            return await Task.Run(() =>
            {
                using var session = SessionUtility.OpenSession();
                var article = session.Query<ArticleNewsgroup>()
                    .Where(an => an.Article.MessageId == messageId)
                    .Fetch(an => an.Article)
                    .Fetch(an => an.Newsgroup)
                    .FirstOrDefault();

                session.Close();

                // Cache for future requests
                if (article != null)
                {
                    cache.CacheArticle(article);
                }

                return article;
            });
        }

        /// <summary>
        /// Retrieves an article by newsgroup and number with cache support
        /// </summary>
        public static async Task<ArticleNewsgroup?> GetArticleByNumberAsync(
            string newsgroup,
            long articleNumber,
            bool includeDeleted,
            bool includePending,
            ArticleCache cache)
        {
            // Try cache first
            if (cache.TryGetArticleByNumber(newsgroup, articleNumber, out var cachedArticle))
            {
                return cachedArticle;
            }

            // Fetch from database
            return await Task.Run(() =>
            {
                using var session = SessionUtility.OpenSession();
                
                ArticleNewsgroup? article;
                
                if (includeDeleted)
                {
                    article = session.Query<ArticleNewsgroup>()
                        .Where(an => an.Cancelled && 
                               an.Newsgroup.Name == newsgroup.Substring(0, newsgroup.Length - 8) && 
                               an.Number == articleNumber)
                        .Fetch(an => an.Article)
                        .Fetch(an => an.Newsgroup)
                        .FirstOrDefault();
                }
                else if (includePending)
                {
                    article = session.Query<ArticleNewsgroup>()
                        .Where(an => an.Pending && 
                               an.Newsgroup.Name == newsgroup.Substring(0, newsgroup.Length - 8) && 
                               an.Number == articleNumber)
                        .Fetch(an => an.Article)
                        .Fetch(an => an.Newsgroup)
                        .FirstOrDefault();
                }
                else
                {
                    article = session.Query<ArticleNewsgroup>()
                        .Where(an => !an.Cancelled && !an.Pending && 
                               an.Newsgroup.Name == newsgroup && 
                               an.Number == articleNumber)
                        .Fetch(an => an.Article)
                        .Fetch(an => an.Newsgroup)
                        .FirstOrDefault();
                }

                session.Close();

                // Cache for future requests
                if (article != null)
                {
                    cache.CacheArticle(article);
                }

                return article;
            });
        }

        /// <summary>
        /// Retrieves a newsgroup by name with cache support
        /// </summary>
        public static async Task<Newsgroup?> GetNewsgroupAsync(
            string name,
            ArticleCache cache)
        {
            // Try cache first
            if (cache.TryGetNewsgroup(name, out var cachedNewsgroup))
            {
                return cachedNewsgroup;
            }

            // Fetch from database
            return await Task.Run(() =>
            {
                using var session = SessionUtility.OpenSession();
                var newsgroup = session.Query<Newsgroup>()
                    .Where(n => n.Name == name)
                    .FirstOrDefault();

                session.Close();

                // Cache for future requests
                if (newsgroup != null)
                {
                    cache.CacheNewsgroup(newsgroup);
                }

                return newsgroup;
            });
        }

        /// <summary>
        /// Batch retrieves articles for LIST operations using stateless session for better performance
        /// </summary>
        public static async Task<ArticleNewsgroup[]> GetArticleRangeAsync(
            string newsgroup,
            long startNumber,
            long endNumber,
            int maxResults = 1000)
        {
            return await Task.Run(() =>
            {
                // Use stateless session for bulk read operations - much faster
                using var session = SessionUtility.OpenStatelessSession();
                
                var articles = session.Query<ArticleNewsgroup>()
                    .Where(an => !an.Cancelled && !an.Pending &&
                           an.Newsgroup.Name == newsgroup &&
                           an.Number >= startNumber &&
                           an.Number <= endNumber)
                    .OrderBy(an => an.Number)
                    .Take(maxResults)
                    .ToArray();

                return articles;
            });
        }
    }
}
