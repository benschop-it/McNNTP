namespace McNNTP.Core.Server.NNTP
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Net;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;

    internal class NntpListener : TcpListener
    {
        // Thread signal.
        private readonly NntpServer server;
        private readonly ILogger<NntpListener> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly SemaphoreSlim _connectionSemaphore;
        private const int MaxConcurrentConnections = 1000; // Configurable limit

        public NntpListener([NotNull] NntpServer server, [NotNull] IPEndPoint localEp, [NotNull] ILogger<NntpListener> logger, [NotNull] ILoggerFactory loggerFactory)
            : base(localEp)
        {
            this.server = server;
            this._logger = logger;
            this._loggerFactory = loggerFactory;
            this._connectionSemaphore = new SemaphoreSlim(MaxConcurrentConnections, MaxConcurrentConnections);
        }

        public PortClass PortType { get; set; }

        public async void StartAccepting(CancellationToken cancellationToken = default)
        {
            // Establish the local endpoint for the socket.
            var localEndPoint = new IPEndPoint(IPAddress.Any, ((IPEndPoint)this.LocalEndpoint).Port);

            // Create a TCP/IP socket.
            var listener = new NntpListener(this.server, localEndPoint, _logger, _loggerFactory)
            {
                PortType = this.PortType
            };

            // Bind the socket to the local endpoint and listen for incoming connections.
            try
            {
                listener.Start(100);
                _logger.LogInformation("Listener started on port {Port} ({PortType})", ((IPEndPoint)this.LocalEndpoint).Port, this.PortType);

                while (!cancellationToken.IsCancellationRequested)
                {
                    // Wait for available connection slot
                    await _connectionSemaphore.WaitAsync(cancellationToken);

                    try
                    {
                        // Start an asynchronous socket to listen for connections.
                        var handler = await listener.AcceptTcpClientAsync(cancellationToken);

                        // Handle connection asynchronously without blocking the listener
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await HandleConnectionAsync(handler, cancellationToken);
                            }
                            finally
                            {
                                _connectionSemaphore.Release();
                            }
                        }, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Error accepting connection");
                        _connectionSemaphore.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Listener gracefully stopped due to cancellation");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception when trying to accept connection from listener");
            }
            finally
            {
                listener.Stop();
                _connectionSemaphore.Dispose();
            }
        }

        private async Task HandleConnectionAsync(TcpClient handler, CancellationToken cancellationToken)
        {
            NntpConnection? nntpConnection = null;

            try
            {
                if (this.PortType == PortClass.ClearText || this.PortType == PortClass.ExplicitTLS)
                {
                    var stream = handler.GetStream();
                    nntpConnection = new NntpConnection(this.server, handler, stream, _loggerFactory.CreateLogger<NntpConnection>());
                }
                else
                {
                    var stream = handler.GetStream();
                    var sslStream = new SslStream(stream, false);

                    try
                    {
                        var sslOptions = new SslServerAuthenticationOptions
                        {
                            ServerCertificate = this.server.ServerAuthenticationCertificate
                        };
                        await sslStream.AuthenticateAsServerAsync(sslOptions, cancellationToken);
                    }
                    catch (IOException ioe)
                    {
                        _logger.LogError(ioe, "I/O Exception attempting to perform TLS handshake");
                        await sslStream.DisposeAsync();
                        handler.Dispose();
                        return;
                    }

                    nntpConnection = new NntpConnection(this.server, handler, sslStream, _loggerFactory.CreateLogger<NntpConnection>(), true);
                }

                this.server.AddConnection(nntpConnection);
                nntpConnection.Process();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling connection");
                if (nntpConnection != null)
                {
                    await nntpConnection.Shutdown();
                }
                else
                {
                    handler?.Dispose();
                }
            }
        }
    }
}
