﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Crawler3WebsocketClient;
using Crawler3WebsocketClient.Tests;

namespace TestCli {
    class Program {
        static readonly object Sync = new object();

        static void WriteLine(string str) => WriteLine((object)str);
        static void WriteLine(object str) {
            lock (Sync) {
                Console.WriteLine(str);
            }
        }

        static void Main(string[] args) {
            AsyncMain().GetAwaiter().GetResult();
        }

        static async Task AsyncMain() {

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) => {
                if (cts.IsCancellationRequested) return;
                cts.Cancel(false);
                e.Cancel = true;
            };

            var settings = new TestConfiguration();
            var logger = new LambdaLogger(WriteLine);
            
            using var db = new Db("test.db");
            //var baseUrl = "https://ld.m.887.at/p/";
            var baseUrl = "https://www.ichkoche.at/";
            //var baseUrl = "https://www.acolono.com/";
            //var baseUrl = "https://orf.at/";
            var crawlerConfig = new CrawlerConfig {
                CheckExternalLinks = false,
                FollowInternalLinks = true,
                MaxConcurrency = 8,
                MaxRequestsPerCrawl = 500,
                TakeScreenShots = true,
                RequestQueue = {baseUrl},
                UrlFilter = $"{baseUrl}[.*]",
            };
            var crawlId = db.NewCrawl(baseUrl);
            var eot = false;
            while (!eot && !cts.Token.IsCancellationRequested) {
                try {
                    WriteLine($"{crawlId}: {baseUrl}");
                    var purge = db.PurgeCrawl(crawlId);
                    WriteLine("Purge Count: " + purge);
                    using var client = new WebsocketJsonClient(new Uri(settings.CrawlerWebsocketUrl), logger);
                    var crawlSw = new Stopwatch();
                    crawlSw.Start();

                    client.OnStatus += (s) => {
                        var avgPageSpeed = crawlSw.Elapsed.TotalSeconds / s.HandledRequestCount;
                        WriteLine($"pending: {s.PendingRequestCount}, handled: {s.HandledRequestCount}, buff: {client.JsonChannelSize}, avgSecondsPerPage:{avgPageSpeed:0.000}sec");
                    };
                    client.OnEot += () => {
                        WriteLine("Done");
                        eot = true;
                    };

                    client.OnNode += (n) => {
                        db.StoreNodes(crawlId, n);
                        WriteLine($"LoadTime: {n.LoadTime:0.000}sec - {n.Url}");
                    };
                    client.OnEdges += (e) => {
                        db.StoreEdges(crawlId, e.Edges);
                    };

                    await client.SendAsync(crawlerConfig, cts.Token);
                    await client.ReceiveAllAsync(cancellationToken: cts.Token);
                }
                catch (Exception e) {
                    WriteLine(e);
                    await Task.Delay(5000);
                    crawlerConfig.MaxConcurrency = 1;
                }
            }
        }
    }
}