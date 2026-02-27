// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ArticleCache.cs" company="Sean McElroy">
//   Copyright Sean McElroy, 2014.  All rights reserved.
// </copyright>
// <summary>
//   High-performance caching layer for article data
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace McNNTP.Core.Server.Cache
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using McNNTP.Data;

    /// <summary>
    /// High-performance caching layer for frequently accessed article data
    /// </summary>
    public class ArticleCache
    {
        private readonly ConcurrentDictionary<string, CacheEntry<ArticleNewsgroup>> _articleByMessageId;
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, CacheEntry<ArticleNewsgroup>>> _articleByNewsgroupAndNumber;
        private readonly ConcurrentDictionary<string, CacheEntry<Newsgroup>> _newsgroupByName;
        private readonly TimeSpan _defaultExpiration;
        private readonly long _maxCacheSize;
        private long _currentCacheSize;
        private readonly Timer _cleanupTimer;

        public ArticleCache(TimeSpan? expiration = null, long maxCacheSizeBytes = 500_000_000) // 500MB default
        {
            _articleByMessageId = new ConcurrentDictionary<string, CacheEntry<ArticleNewsgroup>>(StringComparer.OrdinalIgnoreCase);
            _articleByNewsgroupAndNumber = new ConcurrentDictionary<string, ConcurrentDictionary<long, CacheEntry<ArticleNewsgroup>>>(StringComparer.OrdinalIgnoreCase);
            _newsgroupByName = new ConcurrentDictionary<string, CacheEntry<Newsgroup>>(StringComparer.OrdinalIgnoreCase);
            _defaultExpiration = expiration ?? TimeSpan.FromMinutes(15);
            _maxCacheSize = maxCacheSizeBytes;
            _currentCacheSize = 0;

            // Cleanup timer runs every 5 minutes
            _cleanupTimer = new Timer(CleanupExpiredEntries, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        public bool TryGetArticleByMessageId(string messageId, out ArticleNewsgroup? article)
        {
            if (_articleByMessageId.TryGetValue(messageId, out var entry) && !entry.IsExpired)
            {
                entry.LastAccessed = DateTime.UtcNow;
                article = entry.Value;
                return true;
            }

            article = null;
            return false;
        }

        public bool TryGetArticleByNumber(string newsgroup, long articleNumber, out ArticleNewsgroup? article)
        {
            if (_articleByNewsgroupAndNumber.TryGetValue(newsgroup, out var articles) &&
                articles.TryGetValue(articleNumber, out var entry) && !entry.IsExpired)
            {
                entry.LastAccessed = DateTime.UtcNow;
                article = entry.Value;
                return true;
            }

            article = null;
            return false;
        }

        public bool TryGetNewsgroup(string name, out Newsgroup? newsgroup)
        {
            if (_newsgroupByName.TryGetValue(name, out var entry) && !entry.IsExpired)
            {
                entry.LastAccessed = DateTime.UtcNow;
                newsgroup = entry.Value;
                return true;
            }

            newsgroup = null;
            return false;
        }

        public void CacheArticle(ArticleNewsgroup articleNewsgroup)
        {
            if (articleNewsgroup?.Article?.MessageId == null) return;

            var estimatedSize = EstimateSize(articleNewsgroup);
            
            // Check if adding would exceed cache size
            if (Interlocked.Add(ref _currentCacheSize, estimatedSize) > _maxCacheSize)
            {
                Interlocked.Add(ref _currentCacheSize, -estimatedSize);
                EvictLeastRecentlyUsed();
                Interlocked.Add(ref _currentCacheSize, estimatedSize);
            }

            var entry = new CacheEntry<ArticleNewsgroup>(articleNewsgroup, _defaultExpiration, estimatedSize);

            // Cache by message ID
            _articleByMessageId[articleNewsgroup.Article.MessageId] = entry;

            // Cache by newsgroup and number
            if (articleNewsgroup.Newsgroup?.Name != null)
            {
                var articles = _articleByNewsgroupAndNumber.GetOrAdd(
                    articleNewsgroup.Newsgroup.Name,
                    _ => new ConcurrentDictionary<long, CacheEntry<ArticleNewsgroup>>());
                articles[articleNewsgroup.Number] = entry;
            }
        }

        public void CacheNewsgroup(Newsgroup newsgroup)
        {
            if (newsgroup?.Name == null) return;

            var estimatedSize = EstimateSize(newsgroup);
            var entry = new CacheEntry<Newsgroup>(newsgroup, _defaultExpiration, estimatedSize);
            
            _newsgroupByName[newsgroup.Name] = entry;
            Interlocked.Add(ref _currentCacheSize, estimatedSize);
        }

        public void InvalidateArticle(string messageId)
        {
            if (_articleByMessageId.TryRemove(messageId, out var entry))
            {
                Interlocked.Add(ref _currentCacheSize, -entry.EstimatedSize);
            }
        }

        public void Clear()
        {
            _articleByMessageId.Clear();
            _articleByNewsgroupAndNumber.Clear();
            _newsgroupByName.Clear();
            Interlocked.Exchange(ref _currentCacheSize, 0);
        }

        private void CleanupExpiredEntries(object? state)
        {
            // Cleanup article by message ID
            foreach (var kvp in _articleByMessageId)
            {
                if (kvp.Value.IsExpired)
                {
                    if (_articleByMessageId.TryRemove(kvp.Key, out var removed))
                    {
                        Interlocked.Add(ref _currentCacheSize, -removed.EstimatedSize);
                    }
                }
            }

            // Cleanup newsgroups
            foreach (var kvp in _newsgroupByName)
            {
                if (kvp.Value.IsExpired)
                {
                    if (_newsgroupByName.TryRemove(kvp.Key, out var removed))
                    {
                        Interlocked.Add(ref _currentCacheSize, -removed.EstimatedSize);
                    }
                }
            }
        }

        private void EvictLeastRecentlyUsed()
        {
            // Simple LRU eviction - remove 10% of oldest entries
            var entriesToRemove = Math.Max(1, _articleByMessageId.Count / 10);
            var sortedEntries = new List<KeyValuePair<string, CacheEntry<ArticleNewsgroup>>>(_articleByMessageId);
            sortedEntries.Sort((a, b) => a.Value.LastAccessed.CompareTo(b.Value.LastAccessed));

            for (int i = 0; i < entriesToRemove && i < sortedEntries.Count; i++)
            {
                if (_articleByMessageId.TryRemove(sortedEntries[i].Key, out var removed))
                {
                    Interlocked.Add(ref _currentCacheSize, -removed.EstimatedSize);
                }
            }
        }

        private long EstimateSize(ArticleNewsgroup articleNewsgroup)
        {
            // Rough estimation: headers + body + metadata
            return (articleNewsgroup.Article?.Headers?.Length ?? 0) +
                   (articleNewsgroup.Article?.Body?.Length ?? 0) +
                   1024; // Metadata overhead
        }

        private long EstimateSize(Newsgroup newsgroup)
        {
            return (newsgroup.Name?.Length ?? 0) +
                   (newsgroup.Description?.Length ?? 0) +
                   512; // Metadata overhead
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
        }

        private class CacheEntry<T>
        {
            public T Value { get; }
            public DateTime ExpirationTime { get; }
            public DateTime LastAccessed { get; set; }
            public long EstimatedSize { get; }

            public bool IsExpired => DateTime.UtcNow > ExpirationTime;

            public CacheEntry(T value, TimeSpan expiration, long estimatedSize)
            {
                Value = value;
                ExpirationTime = DateTime.UtcNow.Add(expiration);
                LastAccessed = DateTime.UtcNow;
                EstimatedSize = estimatedSize;
            }
        }
    }
}
