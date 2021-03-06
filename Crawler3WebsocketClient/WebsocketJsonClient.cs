﻿using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Crawler3WebsocketClient {
    public class WebsocketJsonClient : IDisposable {
        private readonly Uri _socketUrl;
        private readonly IWebsocketLogger _logger;
        private readonly Encoding _encoding;
        private ClientWebSocket _socket = new ClientWebSocket();
        private readonly byte[] _socketBuffer;
        private readonly Channel<byte[]> _jsonChannel = Channel.CreateUnbounded<byte[]>();
        private long _jsonChannelSize;
        private readonly JsonProcessor _jsonProcessor;
        public long JsonChannelSize => Interlocked.Read(ref _jsonChannelSize);

        public WebsocketJsonClient(Uri socketUrl, IWebsocketLogger logger = null, ICredentials credentials = null, int bufferSize = 1024 * 5, Encoding encoding = null, IWebProxy proxy = null) {
            _socketUrl = socketUrl;
            _logger = logger;
            _socketBuffer = new byte[bufferSize];
            _encoding = encoding ?? Encoding.UTF8;
            if (credentials != null) _socket.Options.Credentials = credentials;
            if (proxy != null) _socket.Options.Proxy = proxy;
            if (_socket.Options.Credentials == null && !string.IsNullOrEmpty(_socketUrl.UserInfo) && _socketUrl.UserInfo.Contains(":")) {
                var split = _socketUrl.UserInfo.Split(':');
                if(split.Length == 2) _socket.Options.Credentials = new NetworkCredential(Uri.UnescapeDataString(split[0]), Uri.UnescapeDataString(split[1]));
            }
            _jsonProcessor = new JsonProcessor(_logger);
            _jsonProcessor.OnEot += () => OnEot?.Invoke();
            _jsonProcessor.OnEdges += (e) => OnEdges?.Invoke(e);
            _jsonProcessor.OnNode += (n) => OnNode?.Invoke(n);
            _jsonProcessor.OnStatus += (s) => OnStatus?.Invoke(s);
        }

        public void Connect(CancellationToken cancellationToken = default) => ConnectAsync(cancellationToken).GetAwaiter().GetResult();
        public async Task ConnectAsync(CancellationToken cancellationToken = default) {
            if(_socket.State == WebSocketState.Open) return;
            _logger?.LogInfo("Websocket Connected");
            await _socket.ConnectAsync(_socketUrl, cancellationToken);
        }

        internal void Send(string message, CancellationToken cancellationToken = default) => SendAsync(message, cancellationToken).GetAwaiter().GetResult();
        internal async Task SendAsync(string message, CancellationToken cancellationToken = default) {
            await ConnectAsync(cancellationToken);

            var buffer = _encoding.GetBytes(message);
            var segment = new ArraySegment<byte>(buffer);
            await _socket.SendAsync(segment, WebSocketMessageType.Text, true, cancellationToken);
        }

        public void Send(CrawlerConfig config, CancellationToken cancellationToken = default) => SendAsync(config, cancellationToken).GetAwaiter().GetResult();
        public async Task SendAsync(CrawlerConfig config, CancellationToken cancellationToken = default) {
            var message = _jsonProcessor.Serialize(config);
            await SendAsync(message, cancellationToken);
        }

        public async Task<Exception> ReceiveAllAsync(int timeOutMsec = 1000 * 60 * 5, CancellationToken cancellationToken = default) {
            await ConnectAsync(cancellationToken);

            var processingTask = Task.Run(async () => {
                await ProcessAsync(cancellationToken);
            });

            Exception lastException = null;
            var eot = false;
            OnEot += () => eot = true;

            while (!cancellationToken.IsCancellationRequested && !eot) {

                using var cts = new CancellationTokenSource();
                cts.CancelAfter(timeOutMsec);
                using var combinedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

                var (message, exception) = await ReceiveAsync(combinedCancellationTokenSource.Token);

                if (exception != null) {
                    lastException = exception;
                }

                if (message == null && exception != null) {
                    break;
                } else {
                    await _jsonChannel.Writer.WriteAsync(message, cancellationToken);
                    Interlocked.Increment(ref _jsonChannelSize);
                }
            }
            _jsonChannel.Writer.Complete();
                
            await processingTask;

            if (lastException != null && !eot) {
                _logger?.LogError("Exception on receive", lastException);
            }

            return lastException;
        }


        internal async Task<(byte[] message,Exception exception)> ReceiveAsync(CancellationToken cancellationToken = default) {

            Exception ex = null;
            try {
                await using var ms = new MemoryStream();
                var segment = new ArraySegment<byte>(_socketBuffer, 0, _socketBuffer.Length);
                bool endOfMessage;
                do {
                    var result = await _socket.ReceiveAsync(segment, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Binary) {
                        _logger?.LogError($"Unsupported MessageType '{result.MessageType}'");
                        return (null, ex);
                    }
                    if (result.MessageType == WebSocketMessageType.Close) {
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken);
                        _logger?.LogInfo($"MessageType '{result.MessageType}'");
                        return (null, ex);
                    }
                    endOfMessage = result.EndOfMessage;
                    if (result.Count > 0) {
                        ms.Write(segment.AsSpan(0, result.Count));
                    }
                } while (!endOfMessage);

                var msg = ms.ToArray();
                return (msg, ex);
            } catch (WebSocketException e) {
                ex = e;
            } catch (TaskCanceledException e) {
                ex = e;
            } catch (OperationCanceledException e) {
                ex = e;
            }
            return (null, ex);
        }

        public event Action OnEot;
        public event Action<CrawlerResponseEdges> OnEdges;
        public event Action<CrawlerResponseNode> OnNode;
        public event Action<CrawlerResponseStatus> OnStatus;

        private async Task ProcessAsync(CancellationToken ct) {
            while (await _jsonChannel.Reader.WaitToReadAsync(ct)) {
                if (_jsonChannel.Reader.TryRead(out var messageBytes)) {
                    Interlocked.Decrement(ref _jsonChannelSize);
                    var message = _encoding.GetString(messageBytes);
                    _jsonProcessor.ProcessMessage(message);
                }
            }
        }

        /// <inheritdoc />
        public void Dispose() {
            if (_socket != null) {
                _socket.Dispose();
                _socket = null;
            }
        }
    }
}
