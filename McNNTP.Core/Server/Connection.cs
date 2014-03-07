﻿using JetBrains.Annotations;
using log4net;
using McNNTP.Common;
using McNNTP.Data;
using MoreLinq;
using NHibernate;
using NHibernate.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace McNNTP.Core.Server
{
    // State object for reading client data asynchronously
    internal class Connection
    {
        private static readonly Dictionary<string, Func<Connection, string, Task<CommandProcessingResult>>> _commandDirectory;
        private static readonly ILog _logger = LogManager.GetLogger(typeof(Connection));

        // Client socket.
        [NotNull]
        private readonly NntpServer _server;
        [NotNull] 
        private readonly TcpClient _client;
        [NotNull] 
        private readonly Stream _stream;
        // Size of receive buffer.
        private const int BUFFER_SIZE = 1024;
        // Receive buffer.
        [NotNull]
        private readonly byte[] _buffer = new byte[BUFFER_SIZE];
        // Received data string.
        [NotNull]
        private readonly StringBuilder _builder = new StringBuilder();

        private readonly IPAddress _remoteAddress;
        private readonly int _remotePort;
        private readonly IPAddress _localAddress;
        private readonly int _localPort;

        [CanBeNull]
        private CommandProcessingResult _inProcessCommand;

        public bool AllowStartTls { get; set; }
        public bool CanPost { get; private set; }
        public bool ShowBytes { get; set; }
        public bool ShowCommands { get; set; }
        public bool ShowData { get; set; }
        public string PathHost { get; set; }
        
        #region Authentication
        [CanBeNull]
        public string Username { get; set; }
        [CanBeNull]
        public Administrator Identity { get; set; }
        public bool TLS { get; set; }
        #endregion

        #region Compression
        public bool Compression { get; set; }
        public bool CompressionGZip { get; set; }
        public bool CompressionTerminator { get; set; }
        #endregion

        [CanBeNull]
        public string CurrentNewsgroup { get; private set; }
        public long? CurrentArticleNumber { get; private set; }

        #region Derived instance properties
        public IPAddress RemoteAddress
        {
            get { return _remoteAddress; }
        }
        public int RemotePort
        {
            get { return _remotePort; }
        }
        public IPAddress LocalAddress
        {
            get { return _localAddress; }
        }
        public int LocalPort
        {
            get { return _localPort; }
        }
        #endregion

        static Connection()
        {
            _commandDirectory = new Dictionary<string, Func<Connection, string, Task<CommandProcessingResult>>>
                {
                    {"ARTICLE", async (c, data) => await c.Article(data) },
                    {"AUTHINFO", async (c, data) => await c.AuthInfo(data)},
                    {"BODY", async (c, data) => await c.Body(data)},
                    {"CAPABILITIES", async (c, data) => await c.Capabilities() },
                    {"DATE", async (c, data) => await c.Date()},
                    {"GROUP", async (c, data) => await c.Group(data)},
                    {"HDR", async (c, data) => await c.Hdr(data)},
                    {"HEAD", async (c, data) => await c.Head(data)},
                    {"HELP", async (c, data) => await c.Help()},
                    {"LAST", async (c, data) => await c.Last()},
                    {"LIST", async (c, data) => await c.List(data)},
                    {"LISTGROUP", async (c, data) => await c.ListGroup(data)},
                    {"MODE", async (c, data) => await c.Mode(data)},
                    {"NEWGROUPS", async (c, data) => await c.Newgroups(data)},
                    {"NEXT", async (c, data) => await c.Next()},
                    {"OVER", async (c,data) => await c.Over(data)},
                    {"POST", async (c, data) => await c.Post()},
                    {"STAT", async (c, data) => await c.Stat(data)},
                    {"XFEATURE", async (c, data) => await c.XFeature(data)},
                    {"XHDR", async (c, data) => await c.XHDR(data)},
                    {"XOVER", async (c, data) => await c.XOver(data)},
                    {"QUIT", async (c, data) => await c.Quit()}
                };
        }

        public Connection(
            [NotNull] NntpServer server,
            [NotNull] TcpClient client,
            [NotNull] Stream stream,
            bool tls = false)
        {
            AllowStartTls = server.AllowStartTLS;
            CanPost = server.AllowPosting;
            _client = client;
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            PathHost = server.PathHost;
            ShowBytes = server.ShowBytes;
            ShowCommands = server.ShowCommands;
            ShowData = server.ShowData;
            _server = server;
            _stream = stream;
            TLS = tls;

            var remoteIpEndpoint = (IPEndPoint) _client.Client.RemoteEndPoint;
            _remoteAddress = remoteIpEndpoint.Address;
            _remotePort = remoteIpEndpoint.Port;
            var localIpEndpoint = (IPEndPoint) _client.Client.LocalEndPoint;
            _localAddress = localIpEndpoint.Address;
            _localPort = localIpEndpoint.Port;
        }

        #region IO and Connection Management
        public async void Process()
        {
            // ReSharper disable ConvertIfStatementToConditionalTernaryExpression
            if (CanPost)
                // ReSharper restore ConvertIfStatementToConditionalTernaryExpression
                await Send("200 Service available, posting allowed\r\n");
            else
                await Send("201 Service available, posting prohibited\r\n");

            Debug.Assert(_stream != null);

            bool send403;

            try
            {
                while (true)
                {
                    if (!_client.Connected || !_client.Client.Connected)
                        return;

                    if (!_stream.CanRead)
                    {
                        await Shutdown();
                        return;
                    }

                    var bytesRead = await _stream.ReadAsync(_buffer, 0, BUFFER_SIZE);

                    // There  might be more data, so store the data received so far.
                    _builder.Append(Encoding.ASCII.GetString(_buffer, 0, bytesRead));
                    
                    // Not all data received OR no more but not yet ending with the delimiter. Get more.
                    var content = _builder.ToString();
                    if (bytesRead == BUFFER_SIZE || !content.EndsWith("\r\n", StringComparison.Ordinal))
                    {
                        // Read some more.
                        continue;
                    }

                    // All the data has been read from the 
                    // client. Display it on the console.
                    if (ShowBytes && ShowData)
                        _logger.TraceFormat("{0}:{1} >{2}> {3} bytes: {4}", RemoteAddress, RemotePort, TLS ? "!" : ">", content.Length, content.TrimEnd('\r', '\n'));
                    else if (ShowBytes)
                        _logger.TraceFormat("{0}:{1} >{2}> {3} bytes", RemoteAddress, RemotePort, TLS ? "!" : ">", content.Length);
                    else if (ShowData)
                        _logger.TraceFormat("{0}:{1} >{2}> {3}", RemoteAddress, RemotePort, TLS ? "!" : ">", content.TrimEnd('\r', '\n'));

                    if (_inProcessCommand != null && _inProcessCommand.MessageHandler != null)
                    {
                        // Ongoing read - don't parse it for commands
                        _inProcessCommand = _inProcessCommand.MessageHandler.Invoke(content, _inProcessCommand);
                        if (_inProcessCommand != null && _inProcessCommand.IsQuitting)
                            _inProcessCommand = null;
                    }
                    else
                    {
                        var command = content.Split(' ').First().TrimEnd('\r', '\n').ToUpperInvariant();
                        if (_commandDirectory.ContainsKey(command))
                        {
                            try
                            {
                                if (ShowCommands)
                                    _logger.TraceFormat("{0}:{1} >{2}> {3}", RemoteAddress, RemotePort, TLS ? "!" : ">", content.TrimEnd('\r', '\n'));

                                var result = await _commandDirectory[command].Invoke(this, content);

                                if (!result.IsHandled)
                                    await Send("500 Unknown command\r\n");
                                else if (result.MessageHandler != null)
                                    _inProcessCommand = result;
                                else if (result.IsQuitting)
                                    return;
                            }
                            catch (Exception ex)
                            {
                                send403 = true;
                                _logger.Error("Exception processing a command", ex);
                                break;
                            }
                        }
                        else
                            await Send("500 Unknown command\r\n");
                    }

                    _builder.Clear();
                }

            }
            catch (IOException se)
            {
                send403 = true;
                _logger.Error("I/O Exception on socket " + RemoteAddress, se);
            }
            catch (SocketException se)
            {
                send403 = true;
                _logger.Error("Socket Exception on socket " + RemoteAddress, se);
            }
            catch (ObjectDisposedException ode)
            {
                _logger.Error("Object Disposed Exception", ode);
                return;
            }

            if (send403)
                await Send("403 Archive server temporarily offline\r\n");
        }

        private Task<bool> Send(string data)
        {
            return Send(data, true, Encoding.UTF8);
        }
        private async Task<bool> Send([NotNull] string data, bool async, [NotNull] Encoding encoding, bool compressedIfPossible = false)
        {
            // Convert the string data to byte data using ASCII encoding.
            byte[] byteData;
            if (compressedIfPossible && Compression && CompressionGZip && CompressionTerminator)
                byteData = await data.GZipCompress();
            else
                byteData = encoding.GetBytes(data);

            try
            {
                if (async)
                {
                    // Begin sending the data to the remote device.
                    await _stream.WriteAsync(byteData, 0, byteData.Length);
                    if (ShowBytes && ShowData)
                        _logger.TraceFormat("{0}:{1} <{2}{3} {4} bytes: {5}",
                            RemoteAddress,
                            RemotePort,
                            TLS ? "!" : "<",
                            compressedIfPossible && CompressionGZip ? "G" : "<",
                            byteData.Length,
                            data.TrimEnd('\r', '\n'));
                    else if (ShowBytes)
                        _logger.TraceFormat("{0}:{1} <{2}{3} {4} bytes",
                            RemoteAddress,
                            RemotePort,
                            TLS ? "!" : "<",
                            compressedIfPossible && CompressionGZip ? "G" : "<",
                            byteData.Length);
                    else if (ShowData)
                        _logger.TraceFormat("{0}:{1} <{2}{3} {4}",
                            RemoteAddress,
                            RemotePort,
                            TLS ? "!" : "<",
                            compressedIfPossible && CompressionGZip ? "G" : "<",
                            data.TrimEnd('\r', '\n'));
                }
                else // Block
                {
                    _stream.Write(byteData, 0, byteData.Length);
                    if (ShowBytes && ShowData)
                        _logger.TraceFormat("{0}:{1} <{2}{3} {4} bytes: {5}",
                            RemoteAddress, 
                            RemotePort,
                            TLS ? "!" : "<",
                            compressedIfPossible && CompressionGZip ? "G" : "<",
                            byteData.Length, data.TrimEnd('\r', '\n'));
                    else if (ShowBytes)
                        _logger.TraceFormat("{0}:{1} <{2}{3} {4} bytes", 
                            RemoteAddress, 
                            RemotePort, 
                            TLS ? "!" : "<",
                            compressedIfPossible && CompressionGZip ? "G" : "<",
                            byteData.Length);
                    else if (ShowData)
                        _logger.TraceFormat("{0}:{1} <{2}{3} {4}", 
                            RemoteAddress, 
                            RemotePort, 
                            TLS ? "!" : "<",
                            compressedIfPossible && CompressionGZip ? "G" : "<",
                            data.TrimEnd('\r', '\n'));
                }

                return true;
            }
            catch (IOException)
            {
                // Don't send 403 - the sending socket isn't working.
                _logger.VerboseFormat("{0}:{1} XXX CONNECTION TERMINATED", RemoteAddress, RemotePort);
                return false;
            }
            catch (SocketException)
            {
                // Don't send 403 - the sending socket isn't working.
                _logger.VerboseFormat("{0}:{1} XXX CONNECTION TERMINATED", RemoteAddress, RemotePort);
                return false;
            }
            catch (ObjectDisposedException)
            {
                _logger.VerboseFormat("{0}:{1} XXX CONNECTION TERMINATED", RemoteAddress, RemotePort);
                return false;
            }
        }
        public async Task Shutdown()
        {
            if (_client.Connected)
            {
                await Send("205 closing connection\r\n", false, Encoding.UTF8);
                _client.Client.Shutdown(SocketShutdown.Both);
                _client.Close();
            }

            _server.RemoveConnection(this);
        }
        #endregion

        #region Commands
        private async Task<CommandProcessingResult> Article(string content)
        {
            var param = 
                (string.Compare(content, "ARTICLE\r\n", StringComparison.OrdinalIgnoreCase) == 0) ||
                (string.Compare(content, "ARTICLE \r\n", StringComparison.OrdinalIgnoreCase) == 0)
                ? null
                : content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (string.IsNullOrEmpty(param))
            {
                if (!CurrentArticleNumber.HasValue)
                {
                    await Send("430 No article with that message-id\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            else if (string.IsNullOrEmpty(CurrentNewsgroup) && !param.StartsWith("<", StringComparison.Ordinal))
            {
                await Send("412 No newsgroup selected\r\n");
                return new CommandProcessingResult(true);
            }

            using (var session = Database.SessionUtility.OpenSession())
            {
                ArticleNewsgroup articleNewsgroup;
                int type;
                if (string.IsNullOrEmpty(param))
                {
                    if (CurrentNewsgroup == null)
                    {
                        await Send("412 No newsgroup selected\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (CurrentNewsgroup.EndsWith(".deleted"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber);
                    else if (CurrentNewsgroup.EndsWith(".pending"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber);
                    else
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == CurrentArticleNumber);
                    type = 3;
                }
                else if (param.StartsWith("<", StringComparison.Ordinal))
                {
                    articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Article.MessageId == param);
                    type = 1;
                }
                else
                {
                    int articleNumber;
                    if (!int.TryParse(param, out articleNumber))
                    {
                        await Send("423 No article with that number\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (CurrentNewsgroup == null)
                    {
                        await Send("412 No newsgroup selected\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (CurrentNewsgroup.EndsWith(".deleted"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == articleNumber);
                    else if (CurrentNewsgroup.EndsWith(".pending"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == articleNumber);
                    else
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == articleNumber);
                    type = 2;
                }

                session.Close();

                if (articleNewsgroup == null)
                    switch (type)
                    {
                        case 1:
                            await Send("430 No article with that message-id\r\n");
                            break;
                        case 2:
                            await Send("423 No article with that number\r\n");
                            break;
                        case 3:
                            await Send("420 Current article number is invalid\r\n");
                            break;

                    }
                else
                {
                    switch (type)
                    {
                        case 1:
                            await Send(string.Format(CultureInfo.InvariantCulture, "220 {0} {1} Article follows (multi-line)\r\n",
                                (!string.IsNullOrEmpty(CurrentNewsgroup) && string.CompareOrdinal(articleNewsgroup.Newsgroup.Name, CurrentNewsgroup) == 0) ? articleNewsgroup.Number : 0,
                                articleNewsgroup.Article.MessageId), false, Encoding.UTF8);
                            break;
                        case 2:
                            await Send(string.Format(CultureInfo.InvariantCulture, "220 {0} {1} Article follows (multi-line)\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId), false, Encoding.UTF8);
                            break;
                        case 3:
                            await Send(string.Format(CultureInfo.InvariantCulture, "220 {0} {1} Article follows (multi-line)\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId), false, Encoding.UTF8);
                            break;
                    }

                    await Send(articleNewsgroup.Article.Headers + "\r\n\r\n", false, Encoding.UTF8);
                    await Send(articleNewsgroup.Article.Body + "\r\n.\r\n", false, Encoding.UTF8);
                }
            }

            return new CommandProcessingResult(true);
        }
        private async Task<CommandProcessingResult> AuthInfo(string content)
        {
            // RFC 4643 - NNTP AUTHENTICATION
            var param = (string.Compare(content, "AUTHINFO\r\n", StringComparison.OrdinalIgnoreCase) == 0)
                ? null
                : content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (string.IsNullOrWhiteSpace(param))
            {
                await Send("481 Authentication failed/rejected\r\n");
                return new CommandProcessingResult(true);
            }

            var args = param.Split(' ');

            if (string.Compare(args[0], "USER", StringComparison.OrdinalIgnoreCase) == 0)
            {
                Username = args.Skip(1).Aggregate((c, n) => c + " " + n);
                await Send("381 Password required\r\n");
                return new CommandProcessingResult(true);
            }

            if (string.Compare(args[0], "PASS", StringComparison.OrdinalIgnoreCase) == 0)
            {
                if (Username == null)
                {
                    await Send("482 Authentication commands issued out of sequence\r\n");
                    return new CommandProcessingResult(true);
                }

                if (Identity != null)
                {
                    await Send("502 Command unavailable\r\n");
                    return new CommandProcessingResult(true);
                }

                var password = args.Skip(1).Aggregate((c, n) => c + " " + n);
                var saltBytes = new byte[64];
                var rng = RandomNumberGenerator.Create();
                rng.GetNonZeroBytes(saltBytes);

                Administrator[] allAdmins;
                using (var session = Database.SessionUtility.OpenSession())
                {
                    allAdmins = session.Query<Administrator>().Fetch(a => a.Moderates).ToArray();
                    session.Close();
                }

                var admin = allAdmins
                        .SingleOrDefault(a =>
                                a.Username == Username &&
                                a.PasswordHash ==
                                Convert.ToBase64String(new SHA512CryptoServiceProvider().ComputeHash(Encoding.UTF8.GetBytes(string.Concat(a.PasswordSalt, password)))));

                if (admin == null)
                {
                    await Send("481 Authentication failed/rejected\r\n");
                    return new CommandProcessingResult(true);
                }

                if (admin.LocalAuthenticationOnly &&
                    !IPAddress.IsLoopback(RemoteAddress))
                {
                    await Send("481 Authentication not allowed except locally\r\n");
                    return new CommandProcessingResult(true);
                }

                Identity = admin;
                _logger.InfoFormat("User {0} authenticated from {1}", admin.Username, RemoteAddress);

                await Send("281 Authentication accepted\r\n");
                return new CommandProcessingResult(true);
            }

            //if (string.Compare(args[0], "GENERIC", StringComparison.OrdinalIgnoreCase) == 0)
            //{
            //    Send("501 Command not supported\r\n");
            //    return new CommandProcessingResult(true);
            //}

            await Send("501 Command not supported\r\n");
            return new CommandProcessingResult(true);
        }
        private async Task<CommandProcessingResult> Body(string content)
        {
            var param = (string.Compare(content, "BODY\r\n", StringComparison.OrdinalIgnoreCase) == 0)
                ? null
                : content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (string.IsNullOrEmpty(param))
            {
                if (!CurrentArticleNumber.HasValue)
                {
                    await Send("430 No article with that message-id\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            else
            {
                if (CurrentNewsgroup == null)
                {
                    await Send("412 No newsgroup selected\r\n");
                    return new CommandProcessingResult(true);
                }
            }

            using (var session = Database.SessionUtility.OpenSession())
            {
                int type;
                ArticleNewsgroup articleNewsgroup;
                if (string.IsNullOrEmpty(param))
                {
                    if (CurrentNewsgroup == null)
                    {
                        await Send("412 No newsgroup selected\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (CurrentNewsgroup.EndsWith(".deleted"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber);
                    else if (CurrentNewsgroup.EndsWith(".pending"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber);
                    else
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == CurrentArticleNumber);
                    type = 3;
                }
                else if (param.StartsWith("<", StringComparison.Ordinal))
                {
                    articleNewsgroup = session.Query<ArticleNewsgroup>().FirstOrDefault(an => !an.Cancelled && !an.Pending && an.Article.MessageId == param);
                    type = 1;
                }
                else
                {
                    if (CurrentNewsgroup == null)
                    {
                        await Send("412 No newsgroup selected\r\n");
                        return new CommandProcessingResult(true);
                    }

                    int articleNumber;
                    if (!int.TryParse(param, out articleNumber))
                    {
                        await Send("423 No article with that number\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (CurrentNewsgroup.EndsWith(".deleted"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == articleNumber);
                    else if (CurrentNewsgroup.EndsWith(".pending"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == articleNumber);
                    else
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == articleNumber);
                    type = 2;
                }

                session.Close();

                if (articleNewsgroup == null)
                    switch (type)
                    {
                        case 1:
                            await Send("430 No article with that message-id\r\n");
                            break;
                        case 2:
                            await Send("423 No article with that number\r\n");
                            break;
                        case 3:
                            await Send("420 Current article number is invalid\r\n");
                            break;

                    }
                else
                {
                    switch (type)
                    {
                        case 1:
                            await Send(string.Format(CultureInfo.InvariantCulture, "222 {0} {1} Body follows (multi-line)\r\n",
                                (!string.IsNullOrEmpty(CurrentNewsgroup) && string.CompareOrdinal(articleNewsgroup.Newsgroup.Name, CurrentNewsgroup) == 0) ? articleNewsgroup.Number : 0,
                                articleNewsgroup.Article.MessageId));
                            break;
                        case 2:
                            await Send(string.Format(CultureInfo.InvariantCulture, "222 {0} {1} Body follows (multi-line)\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId));
                            break;
                        case 3:
                            await Send(string.Format(CultureInfo.InvariantCulture, "222 {0} {1} Body follows (multi-line)\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId));
                            break;
                    }

                    await Send(articleNewsgroup.Article.Body + "\r\n.\r\n", false, Encoding.UTF8);
                }
            }

            return new CommandProcessingResult(true);
        }

        private async Task<CommandProcessingResult> Capabilities()
        {
            var sb = new StringBuilder();
            sb.Append("101 Capability list:\r\n");
            sb.Append("VERSION 2\r\n");
            //sb.Append("IHAVE\r\n");
            sb.Append("HDR\r\n");
            sb.Append("LIST ACTIVE NEWSGROUPS ACTIVE.TIMES DISTRIB.PATS HEADERS OVERVIEW.FMT\r\n");
            sb.Append("MODE-READER");
            //sb.Append("NEWNEWS\r\n");
            sb.Append("OVER MSGID\r\n");
            sb.Append("POST\r\n");
            sb.Append("READER\r\n");
            if (AllowStartTls)
                sb.Append("STARTTLS\r\n");
            sb.Append("XFEATURE-COMPRESS GZIP TERMINATOR\r\n");
            sb.Append("IMPLEMENTATION McNNTP 1.0.0\r\n");
            sb.Append(".\r\n");
            await Send(sb.ToString());
            return new CommandProcessingResult(true);
        }
        private async Task<CommandProcessingResult> Date()
        {
            await Send(string.Format(CultureInfo.InvariantCulture, "111 {0:yyyyMMddHHmmss}\r\n", DateTime.UtcNow));
            return new CommandProcessingResult(true);
        }        
        private async Task<CommandProcessingResult> Group(string content)
        {
            content = content.TrimEnd('\r', '\n').Substring(content.IndexOf(' ') + 1).Split(' ')[0];
            Newsgroup ng;
            using (var session = Database.SessionUtility.OpenSession())
            {
                ng = session.Query<Newsgroup>().AddMetagroups(session, Identity).SingleOrDefault(n => n.Name == content);
                session.Close();
            }

            if (ng == null)
                await Send(string.Format("411 {0} is unknown\r\n", content));
            else
            {
                CurrentNewsgroup = ng.Name;
                CurrentArticleNumber = ng.LowWatermark;

                // ReSharper disable ConvertIfStatementToConditionalTernaryExpression
                if (ng.PostCount == 0)
                    // ReSharper restore ConvertIfStatementToConditionalTernaryExpression
                    await Send(string.Format("211 0 0 0 {0}\r\n", ng.Name));
                else
                    await Send(string.Format("211 {0} {1} {2} {3}\r\n", ng.PostCount, ng.LowWatermark, ng.HighWatermark, ng.Name));
            }
            return new CommandProcessingResult(true);
        }
        private async Task<CommandProcessingResult> Hdr(string content)
        {
            var parts = content.TrimEnd('\r', '\n').Split(' ');
            if (parts.Length < 2 || parts.Length > 3)
            {
                await Send("501 Syntax Error\r\n");
                return new CommandProcessingResult(true);
            }

            int type;

            if (parts.Length == 3 && parts[2].StartsWith("<", StringComparison.Ordinal))
                type = 1;
            else if (parts.Length == 3 && !parts[2].StartsWith("<", StringComparison.Ordinal))
            {
                type = 2;
                int articleId;
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out articleId))
                {
                    await Send("501 Syntax Error\r\n");
                    return new CommandProcessingResult(true);
                }

                if (string.IsNullOrEmpty(CurrentNewsgroup))
                {
                    await Send("412 No newsgroup selected\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            else //if (parts.Length == 2)
            {
                type = 3;
                if (string.IsNullOrEmpty(CurrentNewsgroup))
                {
                    await Send("412 No newsgroup selected\r\n");
                    return new CommandProcessingResult(true);
                }
                if (!CurrentArticleNumber.HasValue)
                {
                    await Send("420 Current article number is invalid\r\n");
                    return new CommandProcessingResult(true);
                }
            }

            using (var session = Database.SessionUtility.OpenSession())
            {
                IEnumerable<ArticleNewsgroup> articleNewsgroups;
                switch (type)
                {
                    case 1:
                        articleNewsgroups = new[] { session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Article.MessageId == parts[2]) };
                        break;
                    case 2:
                        var range = ParseRange(parts[2]);
                        if (range.Equals(default(System.Tuple<int, int?>)))
                        {
                            await Send("501 Syntax Error\r\n");
                            return new CommandProcessingResult(true);
                        }

                        if (CurrentNewsgroup.EndsWith(".deleted"))
                            articleNewsgroups = (range.Item2.HasValue)
                                ? session.Query<ArticleNewsgroup>().Where(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number >= range.Item1 && an.Number <= range.Item2)
                                : session.Query<ArticleNewsgroup>().Where(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number >= range.Item1);
                        else if (CurrentNewsgroup.EndsWith(".pending"))
                            articleNewsgroups = (range.Item2.HasValue)
                                ? session.Query<ArticleNewsgroup>().Where(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number >= range.Item1 && an.Number <= range.Item2)
                                : session.Query<ArticleNewsgroup>().Where(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number >= range.Item1);
                        else
                            articleNewsgroups = (range.Item2.HasValue)
                                ? session.Query<ArticleNewsgroup>().Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number >= range.Item1 && an.Number <= range.Item2)
                                : session.Query<ArticleNewsgroup>().Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number >= range.Item1);
                        break;
                    case 3:
                        Debug.Assert(CurrentArticleNumber.HasValue);

                        if (CurrentNewsgroup.EndsWith(".deleted"))
                            articleNewsgroups = session.Query<ArticleNewsgroup>().Where(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber.Value);
                        else if (CurrentNewsgroup.EndsWith(".pending"))
                            articleNewsgroups = session.Query<ArticleNewsgroup>().Where(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber.Value);
                        else
                            articleNewsgroups = session.Query<ArticleNewsgroup>().Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == CurrentArticleNumber.Value);
                        break;
                    default:
                        // Unrecognized...
                        await Send("501 Syntax Error\r\n");
                        return new CommandProcessingResult(true);
                }

                session.Close();

                if (!articleNewsgroups.Any())
                    switch (type)
                    {
                        case 1:
                            await Send("430 No article with that message-id\r\n");
                            break;
                        case 2:
                            await Send("423 No articles in that range\r\n");
                            break;
                        case 3:
                            await Send("420 Current article number is invalid\r\n");
                            break;
                    }
                else
                {
                    await Send("225 Headers follow (multi-line)\r\n");

                    Func<Article, string> headerFunction;
                    switch (parts[1].ToUpperInvariant())
                    {
                        case "DATE":
                            headerFunction = a => a.Date;
                            break;
                        case "FROM":
                            headerFunction = a => a.From;
                            break;
                        case "MESSAGE-ID":
                            headerFunction = a => a.MessageId;
                            break;
                        case "REFERENCES":
                            headerFunction = a => a.References;
                            break;
                        case "SUBJECT":
                            headerFunction = a => a.Subject;
                            break;
                        default:
                            {
                                Dictionary<string, string> headers, headersAndFullLines;
                                headerFunction = a => Data.Article.TryParseHeaders(a.Headers, out headers, out headersAndFullLines)
                                    ? a.GetHeader(parts[1])
                                    : null;
                                break;
                            }
                    }

                    foreach (var articleNewsgroup in articleNewsgroups)
                        if (type == 1)
                            await Send(string.Format(CultureInfo.InvariantCulture, "{0} {1}\r\n",
                                (!string.IsNullOrEmpty(CurrentNewsgroup) && string.CompareOrdinal(articleNewsgroup.Newsgroup.Name, CurrentNewsgroup) == 0) ? articleNewsgroup.Article.MessageId : "0",
                                headerFunction.Invoke(articleNewsgroup.Article)), false, Encoding.UTF8);
                        else
                            await Send(string.Format(CultureInfo.InvariantCulture, "{0} {1}\r\n",
                                articleNewsgroup.Number,
                                headerFunction.Invoke(articleNewsgroup.Article)), false, Encoding.UTF8);

                    await Send(".\r\n");
                }
            }

            return new CommandProcessingResult(true);
        }
        private async Task<CommandProcessingResult> Head(string content)
        {
            var param = (string.Compare(content, "HEAD\r\n", StringComparison.OrdinalIgnoreCase) == 0)
                ? null
                : content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (string.IsNullOrEmpty(param))
            {
                if (!CurrentArticleNumber.HasValue)
                {
                    await Send("430 No article with that message-id\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(CurrentNewsgroup))
                {
                    await Send("412 No newsgroup selected\r\n");
                    return new CommandProcessingResult(true);
                }
            }

            using (var session = Database.SessionUtility.OpenSession())
            {
                ArticleNewsgroup articleNewsgroup;
                int type;
                if (string.IsNullOrEmpty(param))
                {
                    if (CurrentNewsgroup == null)
                    {
                        await Send("412 No newsgroup selected\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (CurrentNewsgroup.EndsWith(".deleted"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber);
                    else if (CurrentNewsgroup.EndsWith(".pending"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == CurrentArticleNumber);
                    else
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == CurrentArticleNumber);
                    type = 3;
                }
                else if (param.StartsWith("<", StringComparison.Ordinal))
                {
                    articleNewsgroup = session.Query<ArticleNewsgroup>().FirstOrDefault(an => an.Article.MessageId == param);
                    type = 1;
                }
                else
                {
                    if (CurrentNewsgroup == null)
                    {
                        await Send("412 No newsgroup selected\r\n");
                        return new CommandProcessingResult(true);
                    }
                    
                    int articleNumber;
                    if (!int.TryParse(param, out articleNumber))
                    {
                        await Send("423 No article with that number\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (CurrentNewsgroup.EndsWith(".deleted"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Cancelled && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == articleNumber);
                    else if (CurrentNewsgroup.EndsWith(".pending"))
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Pending && an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Number == articleNumber);
                    else
                        articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == articleNumber);
                    type = 2;
                }

                session.Close();

                if (articleNewsgroup == null)
                    switch (type)
                    {
                        case 1:
                            await Send("430 No article with that message-id\r\n");
                            break;
                        case 2:
                            await Send("423 No article with that number\r\n");
                            break;
                        case 3:
                            await Send("420 Current article number is invalid\r\n");
                            break;

                    }
                else
                {
                    switch (type)
                    {
                        case 1:
                            await Send(string.Format(CultureInfo.InvariantCulture, "221 {0} {1} Headers follow (multi-line)\r\n",
                                (string.CompareOrdinal(articleNewsgroup.Newsgroup.Name, CurrentNewsgroup) == 0) ? articleNewsgroup.Number : 0, articleNewsgroup.Article.MessageId));
                            break;
                        case 2:
                            await Send(string.Format(CultureInfo.InvariantCulture, "221 {0} {1} Headers follow (multi-line)\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId));
                            break;
                        case 3:
                            await Send(string.Format(CultureInfo.InvariantCulture, "221 {0} {1} Headers follow (multi-line)\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId));
                            break;
                    }

                    await Send(articleNewsgroup.Article.Headers + "\r\n.\r\n", false, Encoding.UTF8);
                }
            }

            return new CommandProcessingResult(true);
        }
        private async Task<CommandProcessingResult> Help()
        {
            var sb = new StringBuilder();
            sb.Append("100 Help text follows\r\n");

            var dirName = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (dirName != null && File.Exists(Path.Combine(dirName, "HelpFile.txt")))
            {
                using (var sr = new StreamReader(Path.Combine(dirName, "HelpFile.txt"), Encoding.UTF8))
                {
                    sb.Append(await sr.ReadToEndAsync());
                    sr.Close();
                }
            }
            else
            {
                sb.Append("The list of commands understood by this server are:\r\n");
                foreach (var cmd in _commandDirectory)
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0}\r\n", cmd.Key);
            }

            if (!sb.ToString().EndsWith("\r\n.\r\n"))
                sb.Append("\r\n.\r\n");

            await Send(sb.ToString());
            return new CommandProcessingResult(true);
        }
        private async Task<CommandProcessingResult> Last()
        {
            // If the currently selected newsgroup is invalid, a 412 response MUST be returned.
            if (string.IsNullOrWhiteSpace(CurrentNewsgroup))
            {
                await Send("412 No newsgroup selected\r\n");
                return new CommandProcessingResult(true);
            }

            var currentArticleNumber = CurrentArticleNumber;

            ArticleNewsgroup previousArticleNewsgroup;

            if (!currentArticleNumber.HasValue)
            {
                await Send("420 Current article number is invalid\r\n");
                return new CommandProcessingResult(true);
            }

            using (var session = Database.SessionUtility.OpenSession())
            {
                previousArticleNewsgroup = session.Query<ArticleNewsgroup>()
                    .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number < currentArticleNumber.Value)
                    .MaxBy(an => an.Number);
                session.Close();
            }

            // If the current article number is already the first article of the newsgroup, a 422 response MUST be returned.
            if (previousArticleNewsgroup == null)
            {
                await Send("422 No previous article in this group\r\n");
                return new CommandProcessingResult(true);
            }

            CurrentArticleNumber = previousArticleNewsgroup.Number;

            await Send(string.Format("223 {0} {1} retrieved\r\n", previousArticleNewsgroup.Number, previousArticleNewsgroup.Article.MessageId));
            return new CommandProcessingResult(true);
        }
        private async Task<CommandProcessingResult> List(string content)
        {
            var contentParts = content.Split(' ');

            if (string.Compare(content, "LIST ACTIVE.TIMES\r\n", StringComparison.OrdinalIgnoreCase) == 0 ||
                content.StartsWith("LIST ACTIVE.TIMES ", StringComparison.OrdinalIgnoreCase))
            {
                IList<Newsgroup> newsGroups = null;

                var wildmat = contentParts.Length == 2
                    ? null
                    : content.TrimEnd('\r', '\n').Split(' ').Skip(2).Aggregate((c, n) => c + " " + n);

                var send403 = false;

                try
                {
                    using (var session = Database.SessionUtility.OpenSession())
                    {
                        newsGroups = wildmat == null
                            ? session.Query<Newsgroup>().AddMetagroups(session, Identity).OrderBy(n => n.Name).ToList()
                            : session.Query<Newsgroup>().AddMetagroups(session, Identity).OrderBy(n => n.Name.MatchesWildmat(wildmat)).ToList();
                        session.Close();
                    }
                }
                catch (MappingException mex)
                {
                    send403 = true;
                    _logger.Error("NHibernate Mapping Exception! (Is schema out of date or damaged?)", mex);
                }
                catch (Exception ex)
                {
                    send403 = true;
                    _logger.Error("Exception when trying to handle LIST", ex);
                }

                if (send403)
                {
                    await Send("403 Archive server temporarily offline\r\n");
                    return new CommandProcessingResult(true);
                }

                await Send("215 information follows\r\n");
                var epoch = new DateTime(1970, 1, 1);
                foreach (var ng in newsGroups)
                    await Send(string.Format("{0} {1} {2}\r\n", ng.Name, (ng.CreateDate - epoch).TotalSeconds, ng.CreatorEntity), false, Encoding.UTF8);
                await Send(".\r\n");

                return new CommandProcessingResult(true);
            }

            if (string.Compare(content, "LIST DISTRIB.PATS\r\n", StringComparison.OrdinalIgnoreCase) == 0)
            {
                List<DistributionPattern> pats;

                using (var session = Database.SessionUtility.OpenSession())
                {
                    pats = session.Query<DistributionPattern>().ToList();
                    session.Close();
                }

                await Send("215 information follows\r\n");
                foreach (var pat in pats)
                    await Send(string.Format("{0}:{1}:{2}\r\n", pat.Weight, pat.Wildmat, pat.Distribution), false, Encoding.UTF8);
                await Send(".\r\n");

                return new CommandProcessingResult(true);
            }

            if (content.StartsWith("LIST HEADERS", StringComparison.OrdinalIgnoreCase))
            {
                await Send("215 information follows\r\n:\r\n.\r\n");
                return new CommandProcessingResult(true);
            }

            if (string.Compare(content, "LIST NEWSGROUPS\r\n", StringComparison.OrdinalIgnoreCase) == 0)
            {
                IList<Newsgroup> newsGroups = null;

                var send403 = false;

                try
                {
                    using (var session = Database.SessionUtility.OpenSession())
                    {
                        newsGroups = session.Query<Newsgroup>().AddMetagroups(session, Identity).OrderBy(n => n.Name).ToList();
                        session.Close();
                    }
                }
                catch (MappingException mex)
                {
                    send403 = true;
                    _logger.Error("NHibernate Mapping Exception! (Is schema out of date or damaged?)", mex);
                }
                catch (Exception ex)
                {
                    send403 = true;
                    _logger.Error("Exception when trying to handle LIST", ex);
                   
                }

                if (send403)
                {
                    await Send("403 Archive server temporarily offline\r\n");
                    return new CommandProcessingResult(true);
                }

                await Send("215 information follows\r\n");
                foreach (var ng in newsGroups)
                    await Send(string.Format("{0}\t{1}\r\n", ng.Name, ng.Description), false, Encoding.UTF8);
                await Send(".\r\n");

                return new CommandProcessingResult(true);
            }

            if (string.Compare(content, "LIST OVERVIEW.FMT\r\n", StringComparison.OrdinalIgnoreCase) == 0)
            {
                await Send("215 Order of fields in overview database.\r\n");
                await Send("Subject:\r\n");
                await Send("From:\r\n");
                await Send("Date:\r\n");
                await Send("Message-ID:\r\n");
                await Send("References:\r\n");
                await Send(":bytes\r\n");
                await Send(":lines\r\n");
                await Send(".\r\n");

                return new CommandProcessingResult(true);
            }

            // RFC 3977 - If no keyword is provided, it defaults to ACTIVE.
            if (string.Compare(content, "LIST\r\n", StringComparison.OrdinalIgnoreCase) == 0 ||
                string.Compare(content, "LIST ACTIVE\r\n", StringComparison.OrdinalIgnoreCase) == 0 ||
                content.StartsWith("LIST ", StringComparison.OrdinalIgnoreCase) ||
                content.StartsWith("LIST ACTIVE ", StringComparison.OrdinalIgnoreCase))
            {
                IList<Newsgroup> newsGroups = null;

                string wildmat;
                if (content.EndsWith("LIST\r\n", StringComparison.OrdinalIgnoreCase) ||
                    content.EndsWith("ACTIVE\r\n", StringComparison.OrdinalIgnoreCase))
                    wildmat = null;
                else if (content.StartsWith("LIST ACTIVE ", StringComparison.OrdinalIgnoreCase))
                    wildmat = content.TrimEnd('\r', '\n').Split(' ').Skip(2).Aggregate((c, n) => c + " " + n);
                else
                    wildmat = content.TrimEnd('\r', '\n').Split(' ').Skip(1).Aggregate((c, n) => c + " " + n);

                var send403 = false;

                try
                {
                    using (var session = Database.SessionUtility.OpenSession())
                    {
                        newsGroups = wildmat == null
                            ? session.Query<Newsgroup>().AddMetagroups(session, Identity).OrderBy(n => n.Name).ToList()
                            : session.Query<Newsgroup>().AddMetagroups(session, Identity).OrderBy(n => n.Name.MatchesWildmat(wildmat)).ToList();
                        session.Close();
                    }
                }
                catch (MappingException mex)
                {
                    _logger.Error("NHibernate Mapping Exception! (Is schema out of date or damaged?)", mex);
                    send403 = true;
                }
                catch (Exception ex)
                {
                    _logger.Error("Exception when trying to handle LIST", ex);
                    send403 = true;
                }

                if (send403)
                {
                    await Send("403 Archive server temporarily offline\r\n");
                    return new CommandProcessingResult(true);
                }

                await Send("215 list of newsgroups follows\r\n");
                foreach (var ng in newsGroups)
                    await Send(string.Format("{0} {1} {2} {3}\r\n", ng.Name, ng.HighWatermark ?? 0, ng.LowWatermark ?? 0, ng.Moderated ? "m" : CanPost ? "y" : "n"), false, Encoding.UTF8);
                await Send(".\r\n");
                return new CommandProcessingResult(true);
            }

            await Send("501 Syntax Error\r\n");
            return new CommandProcessingResult(true);
        }
        private async Task<CommandProcessingResult> ListGroup(string content)
        {
            var parts = content.TrimEnd('\r', '\n').Split(' ');

            if (parts.Length == 1 && CurrentNewsgroup == null)
                await Send("412 No newsgroup selected\r\n");

            using (var session = Database.SessionUtility.OpenSession())
            {
                var name = (parts.Length == 2) ? parts[1] : CurrentNewsgroup;
                var ng = session.Query<Newsgroup>().AddMetagroups(session, Identity).SingleOrDefault(n => n.Name == name);

                if (ng == null)
                {
                    await Send("411 No such newsgroup\r\n");
                    return new CommandProcessingResult(true);
                }

                CurrentNewsgroup = ng.Name;
                if (ng.PostCount == 0)
                {
                    await Send(string.Format("211 0 0 0 {0}\r\n", ng.Name));
                    return new CommandProcessingResult(true);
                }

                IList<ArticleNewsgroup> articleNewsgroups;
                if (parts.Length < 3)
                    articleNewsgroups = session.Query<ArticleNewsgroup>().Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == ng.Name).OrderBy(a => a.Number).ToList();
                else
                {
                    var range = ParseRange(parts[2]);
                    if (range.Equals(default(System.Tuple<int, int?>)))
                    {
                        await Send("501 Syntax Error\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (!range.Item2.HasValue) // LOW-
                        articleNewsgroups = session.Query<ArticleNewsgroup>().Fetch(a => a.Newsgroup).Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == ng.Name && an.Number >= range.Item1).OrderBy(a => a.Number).ToList();
                    else // LOW-HIGH
                        articleNewsgroups = session.Query<ArticleNewsgroup>().Fetch(a => a.Newsgroup).Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == ng.Name && an.Number >= range.Item1 && an.Number <= range.Item2.Value).ToList();
                }

                session.Close();

                CurrentArticleNumber = !articleNewsgroups.Any() ? default(long?) : articleNewsgroups.First().Number;

                await Send(string.Format("211 {0} {1} {2} {3}\r\n", ng.PostCount, ng.LowWatermark, ng.HighWatermark, ng.Name), false, Encoding.UTF8);
                foreach (var article in articleNewsgroups)
                    await Send(string.Format(CultureInfo.InvariantCulture, "{0}\r\n", article.Number.ToString(CultureInfo.InvariantCulture)), false, Encoding.UTF8);
                await Send(".\r\n", false, Encoding.UTF8);
            }

            return new CommandProcessingResult(true);
        }
        private async Task<CommandProcessingResult> Mode(string content)
        {
            if (content.StartsWith("MODE READER", StringComparison.OrdinalIgnoreCase))
            {
                await Send("200 This server is not a mode-switching server, but whatever!\r\n");
                return new CommandProcessingResult(true);
            }

            await Send("501 Syntax Error\r\n");
            return new CommandProcessingResult(true);
        }
        private async Task<CommandProcessingResult> Newgroups(string content)
        {
            var parts = content.TrimEnd('\r', '\n').Split(' ');

            var dateTime = string.Join(" ", parts.ElementAt(1), parts.ElementAt(2));
            DateTime afterDate;
            if (!(parts.ElementAt(1).Length == 8 && DateTime.TryParseExact(dateTime, "yyyyMMdd HHmmss", CultureInfo.InvariantCulture, parts.Length == 4 ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out afterDate)))
                if (!(parts.ElementAt(1).Length == 6 && DateTime.TryParseExact(dateTime, "yyMMdd HHmmss", CultureInfo.InvariantCulture, parts.Length == 4 ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out afterDate)))
                    afterDate = DateTime.MinValue;

            if (afterDate == DateTime.MinValue)
            {
                await Send("231 List of new newsgroups follows (multi-line)\r\n.\r\n");
                return new CommandProcessingResult(true);
            }

            List<Newsgroup> newsGroups;
            using (var session = Database.SessionUtility.OpenSession())
            {
                newsGroups = session.Query<Newsgroup>().AddMetagroups(session, Identity).Where(n => n.CreateDate >= afterDate).OrderBy(n => n.Name).ToList();
                session.Close();
            }

            await Send("231 List of new newsgroups follows (multi-line)\r\n", false, Encoding.UTF8);
            foreach (var ng in newsGroups)
            {
                await Send(string.Format("{0} {1} {2} {3}\r\n", ng.Name, ng.HighWatermark, ng.LowWatermark,
                    CanPost ? "y" : "n"), false, Encoding.UTF8);
            }
            await Send(".\r\n", false, Encoding.UTF8);
            return new CommandProcessingResult(true);
        }
        private async Task<CommandProcessingResult> Next()
        {
            // If the currently selected newsgroup is invalid, a 412 response MUST be returned.
            if (string.IsNullOrWhiteSpace(CurrentNewsgroup))
            {
                await Send("412 No newsgroup selected\r\n");
                return new CommandProcessingResult(true);
            }

            var currentArticleNumber = CurrentArticleNumber;

            ArticleNewsgroup previousArticleNewsgroup;

            if (!currentArticleNumber.HasValue)
            {
                await Send("420 Current article number is invalid\r\n");
                return new CommandProcessingResult(true);
            }

            using (var session = Database.SessionUtility.OpenSession())
            {
                previousArticleNewsgroup = session.Query<ArticleNewsgroup>()
                    .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number > currentArticleNumber.Value)
                    .MinBy(an => an.Number);
                session.Close();
            }

            // If the current article number is already the last article of the newsgroup, a 421 response MUST be returned.
            if (previousArticleNewsgroup == null)
            {
                await Send("421 No next article in this group\r\n");
                return new CommandProcessingResult(true);
            }

            CurrentArticleNumber = previousArticleNewsgroup.Number;

            await Send(string.Format("223 {0} {1} retrieved\r\n", previousArticleNewsgroup.Number, previousArticleNewsgroup.Article.MessageId));
            return new CommandProcessingResult(true);
        }
        private async Task<CommandProcessingResult> Over(string content)
        {
            var param = content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');
            
            IList<ArticleNewsgroup> articleNewsgroups = null;
            var send403 = false;

            try
            {
                using (var session = Database.SessionUtility.OpenSession())
                {
                    if (string.IsNullOrWhiteSpace(param))
                    {
                        //  Third form (current article number used)
                        if (CurrentNewsgroup == null)
                        {
                            await Send("412 No news group current selected\r\n");
                            return new CommandProcessingResult(true);
                        }

                        if (CurrentArticleNumber == null)
                        {
                            await Send("420 Current article number is invalid\r\n");
                            return new CommandProcessingResult(true);
                        }

                        if (CurrentNewsgroup.EndsWith(".deleted"))
                            articleNewsgroups = session.Query<ArticleNewsgroup>()
                                .Where(an => an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Cancelled && an.Number == CurrentArticleNumber)
                                .ToArray();
                        else if (CurrentNewsgroup.EndsWith(".pending"))
                            articleNewsgroups = session.Query<ArticleNewsgroup>()
                                .Where(an => an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Pending && an.Number == CurrentArticleNumber)
                                .ToArray();
                        else
                            articleNewsgroups = session.Query<ArticleNewsgroup>()
                                .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == CurrentArticleNumber)
                                .ToArray();

                        if (!articleNewsgroups.Any())
                        {
                            await Send("420 Current article number is invalid\r\n");
                            return new CommandProcessingResult(true);
                        }
                    }
                    else if (param.StartsWith("<", StringComparison.Ordinal))
                    {
                        // First form (message-id specified)
                        articleNewsgroups = session.Query<ArticleNewsgroup>().Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Article.MessageId == param).ToArray();

                        if (!articleNewsgroups.Any())
                        {
                            await Send("430 No article with that message-id\r\n");
                            return new CommandProcessingResult(true);
                        }
                    }
                    else
                    {
                        // Second form (range specified)
                        if (CurrentNewsgroup == null)
                        {
                            await Send("412 No news group current selected\r\n");
                            return new CommandProcessingResult(true);
                        }

                        var range = ParseRange(param);
                        if (range.Equals(default(System.Tuple<int, int?>)))
                        {
                            await Send("423 No articles in that range\r\n");
                            return new CommandProcessingResult(true);
                        }

                        if (!range.Item2.HasValue) // LOW-
                        {
                            if (CurrentNewsgroup.EndsWith(".deleted"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Cancelled && an.Number >= range.Item1)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else if (CurrentNewsgroup.EndsWith(".pending"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Pending && an.Number >= range.Item1)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number >= range.Item1)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                        }
                        else // LOW-HIGH
                        {
                            if (CurrentNewsgroup.EndsWith(".deleted"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Cancelled && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else if (CurrentNewsgroup.EndsWith(".pending"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Newsgroup.Name == CurrentNewsgroup.Substring(0, CurrentNewsgroup.Length - 8) && an.Pending && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                        }

                        if (!articleNewsgroups.Any())
                        {
                            await Send("423 No articles in that range\r\n");
                            return new CommandProcessingResult(true);
                        }
                    }

                    session.Close();
                }
            }
            catch (Exception ex)
            {
                send403 = true;
                _logger.Error("Exception when trying to handle XOVER", ex);
            }

            if (send403)
            {
                await Send("403 Archive server temporarily offline\r\n");
                return new CommandProcessingResult(true);
            }
            
            CurrentArticleNumber = articleNewsgroups.First().Number;
            Func<string, string> unfold = i => string.IsNullOrWhiteSpace(i) ? i : i.Replace("\r\n", "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

            if (Compression && CompressionGZip)
                await Send("224 Overview information follows (multi-line) [COMPRESS=GZIP]\r\n", false, Encoding.UTF8);
            else
                await Send("224 Overview information follows (multi-line)\r\n", false, Encoding.UTF8);

            var sb = new StringBuilder();

            foreach (var articleNewsgroup in articleNewsgroups)
                sb.Append(string.Format(CultureInfo.InvariantCulture,
                    "{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\r\n",
                    string.CompareOrdinal(articleNewsgroup.Newsgroup.Name, CurrentNewsgroup) == 0
                        ? articleNewsgroup.Number
                        : 0,
                    unfold(articleNewsgroup.Article.Subject).Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' '),
                    unfold(articleNewsgroup.Article.From).Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' '),
                    unfold(articleNewsgroup.Article.Date).Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' '),
                    unfold(articleNewsgroup.Article.MessageId).Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' '),
                    unfold(articleNewsgroup.Article.References).Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' '),
                    unfold((articleNewsgroup.Article.Body.Length * 2).ToString(CultureInfo.InvariantCulture)),
                    unfold(articleNewsgroup.Article.Body.Split(new[] { "\r\n" }, StringSplitOptions.None).Length.ToString(CultureInfo.InvariantCulture))));
            sb.Append(".\r\n");
            await Send(sb.ToString(), false, Encoding.UTF8, true);

            return new CommandProcessingResult(true);
        }

        private async Task<CommandProcessingResult> Post()
        {
            if (!CanPost)
            {
                await Send("440 Posting not permitted\r\n");
                return new CommandProcessingResult(true);
            }

            await Send("340 Send article to be posted\r\n");

            Func<string, CommandProcessingResult, CommandProcessingResult> messageAccumulator = null;
            messageAccumulator = (msg, prev) =>
            {
                if (
                    // Message ends naturally
                    msg != null && (msg.EndsWith("\r\n.\r\n", StringComparison.OrdinalIgnoreCase)) ||
                    // Message delimiter comes in second batch
                    (prev != null && prev.Message != null && prev.Message.EndsWith("\r\n", StringComparison.OrdinalIgnoreCase) && msg != null && msg.EndsWith(".\r\n", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        Article article;
                        if (!Data.Article.TryParse(prev.Message == null ? msg.Substring(0, msg.Length - 5) : prev.Message + msg, out article))
                        {
                            Send("441 Posting failed\r\n");
                            return new CommandProcessingResult(true, true);
                        }

                        foreach (var newsgroupName in article.Newsgroups.Split(' '))
                        {
                            bool canApprove;
                            if (Identity == null)
                                canApprove = false;
                            else if (Identity.CanInject || Identity.CanApproveAny)
                                canApprove = true;
                            else
                                canApprove = Identity.Moderates.Any(ng => ng.Name == newsgroupName);

                            if (!canApprove)
                            {
                                article.Approved = null;
                                article.RemoveHeader("Approved");
                            }
                            if (Identity != null && !Identity.CanCancel)
                            {
                                article.Supersedes = null;
                                article.RemoveHeader("Supersedes");
                            }
                            if (Identity != null && !Identity.CanInject)
                            {
                                article.InjectionDate = DateTime.UtcNow.ToString("dd MMM yyyy HH:mm:ss") + " +0000";
                                article.ChangeHeader("Injection-Date", DateTime.UtcNow.ToString("dd MMM yyyy HH:mm:ss") + " +0000");
                                article.InjectionInfo = null;
                                article.RemoveHeader("Injection-Info");
                                article.Xref = null;
                                article.RemoveHeader("Xref");

                                // RFC 5536 3.2.6. The Followup-To header field SHOULD NOT appear in a message, unless its content is different from the content of the Newsgroups header field.
                                if (!string.IsNullOrWhiteSpace(article.FollowupTo) &&
                                    string.Compare(article.FollowupTo, article.Newsgroups, StringComparison.OrdinalIgnoreCase) == 0)
                                    article.FollowupTo = null;
                            }

                            if ((article.Control != null && Identity == null) ||
                                (article.Control != null && Identity != null && article.Control.StartsWith("cancel ", StringComparison.OrdinalIgnoreCase) && !Identity.CanCancel) ||
                                (article.Control != null && Identity != null && article.Control.StartsWith("newgroup ", StringComparison.OrdinalIgnoreCase) && !Identity.CanCreateGroup) ||
                                (article.Control != null && Identity != null && article.Control.StartsWith("rmgroup ", StringComparison.OrdinalIgnoreCase) && !Identity.CanDeleteGroup) ||
                                (article.Control != null && Identity != null && article.Control.StartsWith("checkgroups ", StringComparison.OrdinalIgnoreCase) && !Identity.CanCheckGroups))
                            {
                                Send("480 Permission to issue control message denied\r\n");
                                return new CommandProcessingResult(true, true);
                            }

                            using (var session = Database.SessionUtility.OpenSession())
                            {
                                // Moderation - if this is a moderator's approval message, don't post it, but approve the referenced message.
                                if (canApprove && !string.IsNullOrEmpty(article.References) && 
                                    (article.Body.StartsWith("APPROVE\r\n", StringComparison.OrdinalIgnoreCase) ||
                                     article.Body.StartsWith("APPROVED\r\n", StringComparison.OrdinalIgnoreCase)))
                                {
                                    var references = article.References.Split(' ');

                                    var target = session.CreateQuery("from ArticleNewsgroup an where an.Article.MessageId IN (:ReferencesList) AND an.Newsgroup.Name = :NewsgroupName")
                                        .SetParameterList("ReferencesList", references)
                                        .SetParameter("NewsgroupName", newsgroupName)
                                        .List<ArticleNewsgroup>()
                                        .SingleOrDefault();

                                    if (target != null)
                                    {
                                        target.Article.Approved = Identity.Mailbox ?? string.Format("{0}@{1}", Identity.Username, _server.PathHost);
                                        session.SaveOrUpdate(target.Article);

                                        target.Pending = false;
                                        session.SaveOrUpdate(target);
                                        session.Flush();
                                        session.Close();
                                        
                                        Send("240 Article received OK\r\n");
                                        return new CommandProcessingResult(true, true)
                                        {
                                            Message = prev.Message + msg
                                        };
                                    }
                                }
                                
                                var newsgroupNameClosure = newsgroupName;
                                // We don't add metagroups here, you can't 'post' directly to a meta group.
                                var newsgroup = session.Query<Newsgroup>().SingleOrDefault(n => n.Name == newsgroupNameClosure);
                                if (newsgroup == null)
                                    continue;
                                
                                article.Id = 0;
                                article.Path = PathHost;
                                session.Save(article);

                                var articleNewsgroup = new ArticleNewsgroup
                                {
                                    Article = article,
                                    Cancelled = false,
                                    Id = 0,
                                    Newsgroup = newsgroup,
                                    Number =  session.CreateQuery("select max(an.Number) from ArticleNewsgroup an where an.Newsgroup.Name = :NewsgroupName").SetParameter("NewsgroupName", newsgroupName).UniqueResult<int>() + 1,
                                    Pending = newsgroup.Moderated && !canApprove
                                };
                                session.Save(articleNewsgroup);

                                session.Close();

                                if (article.Control != null)
                                    HandleControlMessage(newsgroup, article);
                            }
                        }


                        Send("240 Article received OK\r\n");
                        return new CommandProcessingResult(true, true)
                        {
                            Message = prev.Message + msg
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Exception when trying to handle POST", ex);
                        Send("441 Posting failed\r\n");
                        return new CommandProcessingResult(true);
                    }
                }

                return new CommandProcessingResult(true, false)
                {
                    MessageHandler = messageAccumulator,
                    Message = prev == null ? msg : prev.Message == null ? msg : prev.Message + "\r\n" + msg
                };
            };

            return messageAccumulator.Invoke(null, null);
        }
        private async Task<CommandProcessingResult> Quit()
        {
            await Shutdown();
            return new CommandProcessingResult(true, true);
        }
        private CommandProcessingResult StartTLS()
        {
            if (TLS)
            {
                Send("502 Command unavailable\r\n");
                return new CommandProcessingResult(true);
            }

            Send("580 Can not initiate TLS negotiation\r\n");
            return new CommandProcessingResult(true);
        }
        private async Task<CommandProcessingResult> Stat(string content)
        {
            var param = (string.Compare(content, "STAT\r\n", StringComparison.OrdinalIgnoreCase) == 0)
                ? null
                : content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (string.IsNullOrEmpty(param))
            {
                if (!CurrentArticleNumber.HasValue)
                {
                    await Send("430 No article with that message-id\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(CurrentNewsgroup))
                {
                    await Send("412 No newsgroup selected\r\n");
                    return new CommandProcessingResult(true);
                }
            }

            using (var session = Database.SessionUtility.OpenSession())
            {
                ArticleNewsgroup articleNewsgroup;
                int type;
                if (string.IsNullOrEmpty(param))
                {
                    articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == CurrentArticleNumber);
                    type = 3;
                }
                else if (param.StartsWith("<", StringComparison.Ordinal))
                {
                    articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Article.MessageId == param);
                    type = 1;
                }
                else
                {
                    int articleNumber;
                    if (!int.TryParse(param, out articleNumber))
                    {
                        await Send("423 No article with that number\r\n");
                        return new CommandProcessingResult(true);
                    }

                    articleNewsgroup = session.Query<ArticleNewsgroup>().SingleOrDefault(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == CurrentNewsgroup && an.Number == articleNumber);
                    type = 2;
                }

                session.Close();

                if (articleNewsgroup == null)
                    switch (type)
                    {
                        case 1:
                            await Send("430 No article with that message-id\r\n");
                            break;
                        case 2:
                            await Send("423 No article with that number\r\n");
                            break;
                        case 3:
                            await Send("420 Current article number is invalid\r\n");
                            break;

                    }
                else
                {
                    switch (type)
                    {
                        case 1:
                            await Send(string.Format(CultureInfo.InvariantCulture, "223 {0} {1}\r\n",
                                (!string.IsNullOrEmpty(CurrentNewsgroup) && string.CompareOrdinal(articleNewsgroup.Newsgroup.Name, CurrentNewsgroup) == 0) ? articleNewsgroup.Number : 0,
                                articleNewsgroup.Article.MessageId));
                            break;
                        case 2:
                            await Send(string.Format(CultureInfo.InvariantCulture, "223 {0} {1}\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId));
                            break;
                        case 3:
                            await Send(string.Format(CultureInfo.InvariantCulture, "223 {0} {1}\r\n", articleNewsgroup.Number, articleNewsgroup.Article.MessageId));
                            break;
                    }
                }
            }

            return new CommandProcessingResult(true);
        }

        private async Task<CommandProcessingResult> XFeature(string content)
        {
            if (string.Compare(content, "XFEATURE COMPRESS GZIP TERMINATOR\r\n", StringComparison.OrdinalIgnoreCase) == 0)
            {
                Compression = true;
                CompressionGZip = true;
                CompressionTerminator = true;

                await Send("290 feature enabled\r\n");
                return new CommandProcessingResult(true);
            }

            // Not handled.
            return new CommandProcessingResult(false);
        }

        private async Task<CommandProcessingResult> XHDR(string content)
        {
            // See RFC 2980 2.6

            var header = content.Split(' ')[1];
            var rangeExpression = content.Split(' ')[2].TrimEnd('\r', '\n');
            
            if (CurrentNewsgroup == null)
            {
                await Send("412 No news group current selected\r\n");
                return new CommandProcessingResult(true);
            }

            if (header == null)
            {
                await Send(".\r\n");
                return new CommandProcessingResult(true);
            }

            IList<ArticleNewsgroup> articleNewsgroups = null;

            var send403 = false;
            try
            {
                using (var session = Database.SessionUtility.OpenSession())
                {
                    var ng = session.Query<Newsgroup>().AddMetagroups(session, Identity).SingleOrDefault(n => n.Name == CurrentNewsgroup);
                    if (ng == null)
                    {
                        await Send("412 No news group current selected\r\n");
                        return new CommandProcessingResult(true);
                    }

                    if (string.IsNullOrEmpty(rangeExpression))
                    {
                        if (CurrentArticleNumber == null)
                        {
                            await Send("420 No article(s) selected\r\n");
                            return new CommandProcessingResult(true);
                        }

                        if (CurrentNewsgroup.EndsWith(".deleted"))
                            articleNewsgroups = session.Query<ArticleNewsgroup>()
                                .Where(an => an.Cancelled && an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Number == CurrentArticleNumber)
                                .OrderBy(a => a.Number)
                                .ToList();
                        else if (CurrentNewsgroup.EndsWith(".pending"))
                            articleNewsgroups = session.Query<ArticleNewsgroup>()
                                .Where(an => an.Pending && an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Number == CurrentArticleNumber)
                                .OrderBy(a => a.Number)
                                .ToList();
                        else
                            articleNewsgroups = session.Query<ArticleNewsgroup>()
                                .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == ng.Name && an.Number == CurrentArticleNumber)
                                .OrderBy(a => a.Number)
                                .ToList();
                    }
                    else
                    {
                        var range = ParseRange(rangeExpression);
                        if (range.Equals(default(System.Tuple<int, int?>)))
                        {
                            await Send("501 Syntax Error\r\n");
                            return new CommandProcessingResult(true);
                        }

                        if (!range.Item2.HasValue) // LOW-
                        {
                            if (CurrentNewsgroup.EndsWith(".deleted"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Cancelled && an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Number >= range.Item1)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else if (CurrentNewsgroup.EndsWith(".pending"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Pending && an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Number >= range.Item1)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == ng.Name && an.Number >= range.Item1)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                        }
                        else // LOW-HIGH
                        {
                            if (CurrentNewsgroup.EndsWith(".deleted"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Cancelled && an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else if (CurrentNewsgroup.EndsWith(".pending"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Pending && an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => !an.Cancelled && !an.Pending && an.Newsgroup.Name == ng.Name && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                        }
                    }

                    session.Close();
                }
            }
            catch (Exception ex)
            {
                send403 = true;
                _logger.Error("Exception when trying to handle XHDR", ex);
            }

            if (send403)
            {
                await Send("403 Archive server temporarily offline\r\n");
                return new CommandProcessingResult(true);
            }


            if (!articleNewsgroups.Any())
            {
                await Send(".\r\n");
                return new CommandProcessingResult(true);
            }

            await Send("221 Header follows\r\n", false, Encoding.UTF8);
            var sb = new StringBuilder();
            foreach (var articleNewsgroup in articleNewsgroups)
                sb.Append(string.Format("{0} {1}\r\n", articleNewsgroup.Number, articleNewsgroup.Article.GetHeader(header)));
            sb.Append(".\r\n");
            await Send(sb.ToString(), false, Encoding.UTF8, true);

            return new CommandProcessingResult(true);
        }
        private async Task<CommandProcessingResult> XOver(string content)
        {
            var rangeExpression = content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (CurrentNewsgroup == null)
                await Send("412 No news group current selected\r\n");
            else
            {
                Newsgroup ng = null;
                IList<ArticleNewsgroup> articleNewsgroups = null;
                var send403 = false;

                try
                {
                    using (var session = Database.SessionUtility.OpenSession())
                    {
                        ng = session.Query<Newsgroup>().AddMetagroups(session, Identity).SingleOrDefault(n => n.Name == CurrentNewsgroup);

                        if (string.IsNullOrEmpty(rangeExpression))
                        {
                            if (CurrentNewsgroup.EndsWith(".deleted"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Cancelled)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else if (CurrentNewsgroup.EndsWith(".pending"))
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Pending)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                            else
                                articleNewsgroups = session.Query<ArticleNewsgroup>()
                                    .Where(an => an.Newsgroup.Name == ng.Name && !an.Cancelled && !an.Pending)
                                    .OrderBy(an => an.Number)
                                    .ToList();
                        }
                        else
                        {
                            var range = ParseRange(rangeExpression);
                            if (range.Equals(default(System.Tuple<int, int?>)))
                            {
                                await Send("501 Syntax Error\r\n");
                                return new CommandProcessingResult(true);
                            }

                            if (!range.Item2.HasValue) // LOW-
                            {
                                if (CurrentNewsgroup.EndsWith(".deleted"))
                                    articleNewsgroups = session.Query<ArticleNewsgroup>()
                                        .Where(an => an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Cancelled && an.Number >= range.Item1)
                                        .OrderBy(an => an.Number)
                                        .ToList();
                                else if (CurrentNewsgroup.EndsWith(".pending"))
                                    articleNewsgroups = session.Query<ArticleNewsgroup>()
                                        .Where(an => an.Newsgroup.Name == ng.Name.Substring(0, ng.Name.Length - 8) && an.Pending && an.Number >= range.Item1)
                                        .OrderBy(an => an.Number)
                                        .ToList();
                                else
                                    articleNewsgroups = session.Query<ArticleNewsgroup>()
                                        .Where(an => an.Newsgroup.Name == ng.Name && !an.Cancelled && !an.Pending && an.Number >= range.Item1)
                                        .OrderBy(an => an.Number)
                                        .ToList();
                            }
                            else // LOW-HIGH
                            {
                                if (CurrentNewsgroup.EndsWith(".deleted"))
                                    articleNewsgroups = session.Query<ArticleNewsgroup>()
                                        .Where(an => an.Newsgroup.Name == ng.Name.Substring(0, CurrentNewsgroup.Length - 8) && an.Cancelled && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                        .OrderBy(an => an.Number)
                                        .ToList();
                                else if (CurrentNewsgroup.EndsWith(".deleted"))
                                    articleNewsgroups = session.Query<ArticleNewsgroup>()
                                        .Where(an => an.Newsgroup.Name == ng.Name.Substring(0, CurrentNewsgroup.Length - 8) && an.Pending && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                        .OrderBy(an => an.Number)
                                        .ToList();
                                else
                                    articleNewsgroups = session.Query<ArticleNewsgroup>()
                                        .Where(an => an.Newsgroup.Name == ng.Name && !an.Cancelled && !an.Pending && an.Number >= range.Item1 && an.Number <= range.Item2.Value)
                                        .OrderBy(an => an.Number)
                                        .ToList();
                            }
                        }

                        session.Close();
                    }
                }
                catch (Exception ex)
                {
                    send403 = true;
                    _logger.Error("Exception when trying to handle XOVER", ex);
                }

                if (send403)
                {
                    await Send("403 Archive server temporarily offline\r\n");
                    return new CommandProcessingResult(true);
                }

                if (ng == null)
                {
                    await Send("411 No such newsgroup\r\n");
                    return new CommandProcessingResult(true);
                }

                if (!articleNewsgroups.Any())
                {
                    await Send("420 No article(s) selected\r\n");
                    return new CommandProcessingResult(true);
                }

                CurrentArticleNumber = articleNewsgroups.First().Number;
                Func<string, string> unfold = i => string.IsNullOrWhiteSpace(i) ? i : i.Replace("\r\n", "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

                if (Compression && CompressionGZip)
                    await Send("224 Overview information follows [COMPRESS=GZIP]\r\n", false, Encoding.UTF8);
                else
                    await Send("224 Overview information follows\r\n", false, Encoding.UTF8);

                var sb = new StringBuilder();

                foreach (var articleNewsgroup in articleNewsgroups)
                    sb.Append(string.Format(CultureInfo.InvariantCulture,
                        "{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\r\n",
                        string.CompareOrdinal(articleNewsgroup.Newsgroup.Name, CurrentNewsgroup) == 0
                            ? articleNewsgroup.Number
                            : 0,
                        unfold(articleNewsgroup.Article.Subject),
                        unfold(articleNewsgroup.Article.From),
                        unfold(articleNewsgroup.Article.Date),
                        unfold(articleNewsgroup.Article.MessageId),
                        unfold(articleNewsgroup.Article.References),
                        unfold((articleNewsgroup.Article.Body.Length * 2).ToString(CultureInfo.InvariantCulture)),
                        unfold(
                            articleNewsgroup.Article.Body.Split(new[] { "\r\n" }, StringSplitOptions.None)
                                .Length.ToString(CultureInfo.InvariantCulture))));
                sb.Append(".\r\n");
                await Send(sb.ToString(), false, Encoding.UTF8, true);
            }

            return new CommandProcessingResult(true);
        }
        #endregion

        private void HandleControlMessage(Newsgroup newsgroup, Article article)
        {
            Debug.Assert(article.Control != null);
            Debug.Assert(Identity != null);

            if (article.Control.StartsWith("cancel ", StringComparison.OrdinalIgnoreCase))
            {
                /* RFC 1036 3.1: Only the author of the message or the local news administrator is
                 * allowed to send this message.  The verified sender of a message is
                 * the "Sender" line, or if no "Sender" line is present, the "From"
                 * line.  The verified sender of the cancel message must be the same as
                 * either the "Sender" or "From" field of the original message.  A
                 * verified sender in the cancel message is allowed to match an
                 * unverified "From" in the original message.
                 */

                // SM: In this implementation, ONLY administrators can issue cancel messages
                Debug.Assert(Identity.CanCancel);

                var messageId = article.Control.Split(' ').Skip(1).Take(1).SingleOrDefault();
                if (messageId != null && messageId.StartsWith("<", StringComparison.Ordinal))
                {
                    using (var session = Database.SessionUtility.OpenSession())
                    {
                        var cancelTarget = session.Query<ArticleNewsgroup>().SingleOrDefault(an => an.Newsgroup.Name == newsgroup.Name && an.Article.MessageId == messageId);
                        if (cancelTarget != null)
                        {
                            cancelTarget.Cancelled = true;
                            session.SaveOrUpdate(cancelTarget);
                            foreach (var an in article.ArticleNewsgroups)
                            {
                                an.Cancelled = true;
                                session.SaveOrUpdate(an);
                            }
                            session.Flush();
                            _logger.InfoFormat("{0} cancelled message {1} ({2}) in {3}", Identity.Username, messageId, article.Subject, cancelTarget.Newsgroup.Name);
                        }

                        session.Close();
                    }
                }
            }
        }

        private static System.Tuple<int, int?> ParseRange(string input)
        {
            int low, high;
            if (input.IndexOf('-') == -1)
            {
                if (!int.TryParse(input, out low))
                    return default(System.Tuple<int, int?>);
                return new System.Tuple<int, int?>(low, low);
            }
            if (input.EndsWith("-", StringComparison.Ordinal))
            {
                if (!int.TryParse(input, out low))
                    return default(System.Tuple<int, int?>);
                return new System.Tuple<int, int?>(low, null);
            }

            if (!int.TryParse(input.Substring(0, input.IndexOf('-')), NumberStyles.Integer, CultureInfo.InvariantCulture, out low))
                return default(System.Tuple<int, int?>);
            if (!int.TryParse(input.Substring(input.IndexOf('-') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out high))
                return default(System.Tuple<int, int?>);

            return new System.Tuple<int, int?>(low, high);
        }
    }
}