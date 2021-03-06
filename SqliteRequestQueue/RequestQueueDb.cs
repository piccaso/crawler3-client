﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SQLite;

namespace SqliteRequestQueue {
    internal class Job {
        [Indexed] public long CrawlId { get; set; }
        [Indexed] public string Url { get; set; }
        [Indexed] public DateTimeOffset Timeout { get; set; }
    }

    internal class Queue {
        [Indexed] public long CrawlId { get; set; }
        [Indexed] public string Url { get; set; }
    }

    internal class RequestQueueDb {
        private readonly SQLiteAsyncConnection _db;

        public RequestQueueDb(string databasePath) {
            _db = new SQLiteAsyncConnection(databasePath);
        }

        public Task CreateTablesAsync() {
            return _db.CreateTablesAsync<Job, Queue>();
        }

        public Task EnqueueAsync(long crawlId, IEnumerable<string> urls) {
            var q = urls.Select(url => new Queue {
                CrawlId = crawlId,
                Url = url
            });
            return _db.InsertAllAsync(q);
        }

        public async IAsyncEnumerable<string> DequeueAsync(long crawlId, int maxUrls) {
            var queue = await _db.QueryAsync<Queue>("SELECT Url FROM Queue WHERE CrawlId=? LIMIT ?", crawlId, maxUrls);
            foreach (var q in queue)
                await _db.ExecuteAsync("DELETE FROM Queue WHERE CrawlId=? AND Url=?", crawlId, q.Url);

            foreach (var q in queue) yield return q.Url;
        }

        public Task InsertJobsAsync(long crawlId, IEnumerable<string> urls, DateTimeOffset timeout) {
            var jobs = urls.Select(url => new Job {
                CrawlId = crawlId,
                Timeout = timeout,
                Url = url
            });
            return _db.InsertAllAsync(jobs);
        }

        public async Task<int> DeleteJobAsync(long crawlId, string url) {
            var count = 0;
            count += await _db.ExecuteAsync("DELETE FROM Job WHERE CrawlId=? AND Url=?", crawlId, url);
            return count;
        }

        public async Task RequeueTimedOutJobsAsync(long crawlId) {
            var now = DateTimeOffset.UtcNow;
            var jobs = await _db.QueryAsync<Job>("SELECT Url FROM Job WHERE CrawlId=? AND Timeout < ?", crawlId, now);
            foreach (var j in jobs) {
                await _db.ExecuteAsync("DELETE FROM Job WHERE CrawlId=? AND Url=?", crawlId, j.Url);
                await _db.InsertAsync(new Queue {
                    CrawlId = crawlId,
                    Url = j.Url
                });
            }
        }
    }
}