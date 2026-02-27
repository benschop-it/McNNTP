// --------------------------------------------------------------------------------------------------------------------
// <copyright file="Program.cs" company="Sean McElroy">
//   Copyright Sean McElroy, 2014.  All rights reserved.
// </copyright>
// <summary>
//   A console host for the NNTP server process
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace McNNTP.Server.Console
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Security;
    using System.Threading;
    using System.Threading.Tasks;

    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    using McNNTP.Core.Database;
    using McNNTP.Core.Server.NNTP;
    using McNNTP.Data;
    using McNNTP.Server.Console.Configuration;

    using NHibernate.Linq;
    using Serilog;
    using Serilog.Events;

    /// <summary>
    /// A console host for the NNTP server process
    /// </summary>
    public class Program
    {
        /// <summary>
        /// A dictionary of commands and function pointers to command handlers for console commands
        /// </summary>
        private static readonly Dictionary<string, Func<string, bool>> _CommandDirectory = new Dictionary<string, Func<string, bool>>
        {
            { "?", s => Help() },
            { "HELP", s => Help() },
            { "DB", DatabaseCommand },
            { "DEBUG", DebugCommand },
            { "GROUP", GroupCommand },
            { "PEER", PeerCommand },
            { "PURGEDB", DatabaseUtility.RebuildSchema },
            { "SHOWCONN", s => ShowConn() },
            { "EXIT", s => Quit() },
            { "QUIT", s => Quit() },
            { "USER", UserCommand }
        };

        /// <summary>
        /// The strings that evaluate to 'true' for a Boolean value
        /// </summary>
        private static readonly string[] _YesStrings = ["ENABLE", "T", "TRUE", "ON", "Y", "YES", "1"];

        /// <summary>
        /// The strings that evaluate to 'false' for a Boolean value
        /// </summary>
        private static readonly string[] _NoStrings = ["DISABLE", "F", "FALSE", "OFF", "N", "NO", "0"];

        /// <summary>
        /// The NNTP server object instance
        /// </summary>
        private static NntpServer? _nntpServer;

        /// <summary>
        /// The logger instance
        /// </summary>
        private static ILogger<Program>? _logger;

        /// <summary>
        /// The main program message loop
        /// </summary>
        /// <param name="args">Command line arguments</param>
        /// <returns>An error code, indicating an error condition when the value returned is non-zero.</returns>
        public static int Main(string[] args)
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            Console.WriteLine("McNNTP Server Console Harness v{0}", version);

            try
            {
                // Build configuration early so Serilog can read settings (and so we can adapt file path)
                var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: true)
                    .AddEnvironmentVariables()
                    .Build();

                // Determine file sink parameters from configuration (fall back to sensible defaults)
                var filePath = configuration["Logging:File:Path"] ?? "logs/mcnntp-.log";
                var maxFileSizeBytesStr = configuration["Logging:File:MaxFileSizeBytes"];
                var maxRollingFilesStr = configuration["Logging:File:MaxRollingFiles"];
                var minLevelStr = configuration["Logging:File:MinLevel"];

                long.TryParse(maxFileSizeBytesStr, out var maxFileSizeBytes);
                if (maxFileSizeBytes <= 0) maxFileSizeBytes = 50 * 1024 * 1024; // default 50MB

                int.TryParse(maxRollingFilesStr, out var retainedCount);
                if (retainedCount <= 0) retainedCount = 10;

                // If user provided a plain filename like "McNTTP.ERROR.log", adapt it to Serilog rolling pattern "name-.log"
                if (Path.GetExtension(filePath).Equals(".log", StringComparison.OrdinalIgnoreCase) && !filePath.Contains("-."))
                {
                    var dir = Path.GetDirectoryName(filePath);
                    var fileName = Path.GetFileNameWithoutExtension(filePath);
                    filePath = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, fileName + "-.log");
                }

                // Ensure directory exists
                try
                {
                    var dirName = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dirName))
                    {
                        Directory.CreateDirectory(dirName);
                    }
                }
                catch
                {
                    // ignore; Serilog will surface I/O errors when writing
                }

                // Minimum level for file sink
                var fileMinLevel = LogEventLevel.Warning;
                if (!string.IsNullOrWhiteSpace(minLevelStr) && Enum.TryParse<LogEventLevel>(minLevelStr, true, out var parsedLevel))
                {
                    fileMinLevel = parsedLevel;
                }

                // Build logger
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .Enrich.FromLogContext()
                    .WriteTo.Console() // console sink
                    .WriteTo.File(
                        path: filePath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: retainedCount,
                        fileSizeLimitBytes: maxFileSizeBytes,
                        rollOnFileSizeLimit: true,
                        restrictedToMinimumLevel: fileMinLevel,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .ReadFrom.Configuration(configuration) // allow overrides from Serilog section if present
                    .CreateLogger();

                Log.Information("Starting host");

                var host = CreateHostBuilder(args).Build();

                using var scope = host.Services.CreateScope();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
                var mcnntpSettings = scope.ServiceProvider.GetRequiredService<IOptions<McNNTPSettings>>().Value;
                //var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

                _logger = logger;

                logger.LogInformation("Starting McNNTP Server Console with modern configuration");
                logger.LogInformation("Configuration loaded from appsettings.json");

                // Create and configure NNTP server using modern configuration
                var nntpServerLogger = scope.ServiceProvider.GetRequiredService<ILogger<NntpServer>>();
                var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

                // Set up DatabaseUtility logger
                DatabaseUtility.SetLogger(loggerFactory.CreateLogger("DatabaseUtility"));

                // Verify Database
                if (DatabaseUtility.VerifyDatabase()) 
                {
                    DatabaseUtility.UpdateSchema();
                }
                else if (DatabaseUtility.UpdateSchema() && !DatabaseUtility.VerifyDatabase(true))
                {
                    Console.WriteLine("Unable to verify a database. Would you like to create and initialize a database?");
                    var resp = Console.ReadLine();
                    if (resp != null && new[] { "y", "yes" }.Contains(resp.ToLowerInvariant().Trim()))
                    {
                        DatabaseUtility.RebuildSchema();
                    }
                }

                // Create performance indexes for optimal query performance
                logger.LogInformation("Creating/verifying database performance indexes...");
                try
                {
                    McNNTP.Core.Database.DatabaseIndexes.CreatePerformanceIndexes(loggerFactory.CreateLogger("DatabaseIndexes"));
                    McNNTP.Core.Database.DatabaseIndexes.AnalyzeDatabase(loggerFactory.CreateLogger("DatabaseIndexes"));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to create some indexes, continuing anyway");
                }

                _nntpServer = new NntpServer(nntpServerLogger, loggerFactory)
                {
                    AllowPosting = true,
                    NntpClearPorts = mcnntpSettings.Ports
                        .Where(p => p.Ssl == "ClearText" && p.Protocol == "nntp")
                        .Select(p => p.Number)
                        .ToArray(),
                    NntpExplicitTLSPorts = mcnntpSettings.Ports
                        .Where(p => p.Ssl == "ExplicitTLS" && p.Protocol == "nntp")
                        .Select(p => p.Number)
                        .ToArray(),
                    NntpImplicitTLSPorts = mcnntpSettings.Ports
                        .Where(p => p.Ssl == "ImplicitTLS" && p.Protocol == "nntp")
                        .Select(p => p.Number)
                        .ToArray(),
                    PathHost = mcnntpSettings.PathHost,
                    SslGenerateSelfSignedServerCertificate = mcnntpSettings.Ssl.GenerateSelfSignedServerCertificate,
                    SslServerCertificateThumbprint = mcnntpSettings.Ssl.ServerCertificateThumbprint
                };

                _nntpServer.Start();

                Console.WriteLine("Type QUIT and press Enter to end the server.");

                while (true)
                {
                    Console.Write("\r\n> ");
                    var input = Console.ReadLine();
                    if (input == null || !_CommandDirectory.ContainsKey(input.Split(' ')[0].ToUpperInvariant()))
                    {
                        Console.WriteLine("Unrecognized command. Type HELP for a list of available commands.");
                        continue;
                    }

                    if (!_CommandDirectory[input.Split(' ')[0].ToUpperInvariant()].Invoke(input)) 
                        continue;

                    _nntpServer.Stop();
                    return 0;
                }
            }
            catch (AggregateException ae)
            {
                foreach (var ex in ae.InnerExceptions) 
                {
                    Console.WriteLine(ex.ToString());
                    _logger?.LogError(ex, "Aggregate exception occurred");
                }
                Console.ReadLine();
                return -2;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                _logger?.LogError(ex, "Unhandled exception occurred");
                Console.ReadLine();
                return -1;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        /// <summary>
        /// Creates the host builder with modern configuration
        /// </summary>
        /// <param name="args">Command line arguments</param>
        /// <returns>The configured host builder</returns>
        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.SetBasePath(AppContext.BaseDirectory);
                    config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                    config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
                    config.AddEnvironmentVariables();
                    config.AddCommandLine(args);
                })
                .ConfigureServices((context, services) =>
                {
                    services.Configure<McNNTPSettings>(
                        context.Configuration.GetSection(McNNTPSettings.SectionName));
                })
                .UseSerilog() // replace Microsoft logging with Serilog so framework logs are captured
                .ConfigureLogging((context, logging) =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                    logging.AddConfiguration(context.Configuration.GetSection("Logging"));
                });

        #region Commands

        /// <summary>
        /// The Help command handler, which shows a help banner on the console
        /// </summary>
        /// <returns>A value indicating whether the server should terminate</returns>
        private static bool Help()
        {
            Console.WriteLine("DB ANNIHILATE                    : Completely wipe and rebuild the news database");
            Console.WriteLine("DB UPDATE                        : Updates the database schema integrity to\r\n" +
                              "                                   match the code object definitions");
            Console.WriteLine("DB VERIFY                        : Verify the database schema integrity against\r\n" +
                              "                                   the code object definitions");
            Console.WriteLine("DEBUG BYTES <value>              : Toggles showing bytes and destinations");
            Console.WriteLine("DEBUG COMMANDS <value>           : Toggles showing commands and responses");
            Console.WriteLine("DEBUG DATA <value>               : Toggles showing all data in and out");
            Console.WriteLine("GROUP <name> CREATE <desc>       : Creates a new news group on the server");
            Console.WriteLine("GROUP <name> CREATOR <value>     : Sets the addr-spec (name and email)");
            Console.WriteLine("GROUP <name> DENYLOCAL <value>   : Toggles denial of local postings to a group");
            Console.WriteLine("GROUP <name> DENYPEER <value>    : Toggles denial of peer postings to a group");
            Console.WriteLine("GROUP <name> MODERATION <value>  : Toggles moderation of a group (true or false)");
            Console.WriteLine("PEER <host>[:port] CREATE        : Creates a peer server for article exchange");
            Console.WriteLine("PEER <host> SUCK [wildmat]       : Configures the active receive (suck)\r\n" +
                              "                                   distribution wildmat. If the wildmat is\r\n" +
                              "                                   blank, the current wildmat will be displayed");
            Console.WriteLine("SHOWCONN                         : Show active connections");
            Console.WriteLine("QUIT                             : Exit the program, killing all connections");
            Console.WriteLine("USER <name> CREATE <pass>        : Creates a new news administrator");
            return false;
        }

        /// <summary>
        /// The Admin command handler, which handles console commands for administration management
        /// </summary>
        /// <param name="input">The full console command input.</param>
        /// <returns>A value indicating whether the server should terminate</returns>
        private static bool UserCommand(string input)
        {
            var parts = input.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                Console.WriteLine("Two parameters are required.");
                return false;
            }

            var name = parts[1].ToLowerInvariant();

            switch (parts[2].ToLowerInvariant())
            {
                case "create":
                    {
                        var pass = new SecureString();
                        foreach (var c in parts.Skip(3).Aggregate((c, n) => c + " " + n))
                            pass.AppendChar(c);

                        var admin = new User
                        {
                            Username = name,
                            CanApproveAny = true,
                            CanCancel = true,
                            CanCheckCatalogs = true,
                            CanCreateCatalogs = true,
                            CanDeleteCatalogs = true,
                            CanInject = false
                        };

                        admin.SetPassword(pass);

                        using (var session = SessionUtility.OpenSession())
                        {
                            session.Save(admin);
                            session.Flush();
                            session.Close();
                        }

                        Console.WriteLine("User {0} created with all privileges.", name);
                        _logger?.LogInformation("User {UserName} created with all privileges", name);
                        break;
                    }

                default:
                    Console.WriteLine("Unknown parameter {0} specified for Admin command", parts[2]);
                    break;
            }

            return false;
        }

        /// <summary>
        /// The Group command handler, which handles console commands for group management
        /// </summary>
        /// <param name="input">The full console command input</param>
        /// <returns>A value indicating whether the server should terminate</returns>
        private static bool GroupCommand(string input)
        {
            var parts = input.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                Console.WriteLine("Two parameters are required.");
                return false;
            }

            var name = parts[1].ToLowerInvariant();
            if (!name.Contains('.'))
            {
                Console.WriteLine("The <name> parameter must contain a '.' to enforce a news hierarchy");
                return false;
            }

            switch (parts[2].ToLowerInvariant())
            {
                case "create":
                    {
                        var desc = parts.Skip(3).Aggregate((c, n) => c + " " + n);
                        using (var session = SessionUtility.OpenSession())
                        {
                            session.Save(new Newsgroup
                            {
                                Name = name,
                                Description = desc,
                                CreateDate = DateTime.UtcNow
                            });
                            session.Close();
                            Console.WriteLine("Newsgroup {0} created.", name);
                            _logger?.LogInformation("Newsgroup {NewsgroupName} created with description: {Description}", name, desc);
                        }

                        break;
                    }

                case "creator":
                    {
                        var creator = parts.Skip(3).Aggregate((c, n) => c + " " + n);

                        using (var session = SessionUtility.OpenSession())
                        {
                            var newsgroup = session.Query<Newsgroup>().SingleOrDefault(n => n.Name == name);
                            if (newsgroup == null)
                                Console.WriteLine("No newsgroup named '{0}' exists.", name);
                            else
                            {
                                newsgroup.CreatorEntity = creator;
                                session.SaveOrUpdate(newsgroup);
                                session.Flush();
                                Console.WriteLine("Creator entity of newsgroup {0} set to {1}", name, creator);
                                _logger?.LogInformation("Creator entity of newsgroup {NewsgroupName} set to {Creator}", name, creator);
                            }

                            session.Close();
                        }

                        break;
                    }

                case "denylocal":
                    {
                        var val = parts[3];

                        using (var session = SessionUtility.OpenSession())
                        {
                            var newsgroup = session.Query<Newsgroup>().SingleOrDefault(n => n.Name == name);
                            if (newsgroup == null)
                                Console.WriteLine("No newsgroup named '{0}' exists.", name);
                            else if (_YesStrings.Contains(val, StringComparer.OrdinalIgnoreCase))
                            {
                                newsgroup.DenyLocalPosting = true;
                                session.SaveOrUpdate(newsgroup);
                                session.Flush();
                                Console.WriteLine("Local posting to newsgroup {0} denied.", name);
                                _logger?.LogInformation("Local posting to newsgroup {NewsgroupName} denied", name);
                            }
                            else if (_NoStrings.Contains(val, StringComparer.OrdinalIgnoreCase))
                            {
                                newsgroup.DenyLocalPosting = false;
                                session.SaveOrUpdate(newsgroup);
                                session.Flush();
                                Console.WriteLine("Local posting to newsgroup {0} re-enabled.", name);
                                _logger?.LogInformation("Local posting to newsgroup {NewsgroupName} re-enabled", name);
                            }
                            else
                                Console.WriteLine("Unable to parse '{0}' value. Please use 'on' or 'off' to change this value.", val);

                            session.Close();
                        }

                        break;
                    }

                case "denypeer":
                    {
                        var val = parts[3];

                        using (var session = SessionUtility.OpenSession())
                        {
                            var newsgroup = session.Query<Newsgroup>().SingleOrDefault(n => n.Name == name);
                            if (newsgroup == null)
                                Console.WriteLine("No newsgroup named '{0}' exists.", name);
                            else if (_YesStrings.Contains(val, StringComparer.OrdinalIgnoreCase))
                            {
                                newsgroup.DenyPeerPosting = true;
                                session.SaveOrUpdate(newsgroup);
                                session.Flush();
                                Console.WriteLine("Peer posting to newsgroup {0} denied.", name);
                                _logger?.LogInformation("Peer posting to newsgroup {NewsgroupName} denied", name);
                            }
                            else if (_NoStrings.Contains(val, StringComparer.OrdinalIgnoreCase))
                            {
                                newsgroup.DenyPeerPosting = false;
                                session.SaveOrUpdate(newsgroup);
                                session.Flush();
                                Console.WriteLine("Peer posting to newsgroup {0} re-enabled.", name);
                                _logger?.LogInformation("Peer posting to newsgroup {NewsgroupName} re-enabled", name);
                            }
                            else
                                Console.WriteLine("Unable to parse '{0}' value. Please use 'on' or 'off' to change this value.", val);

                            session.Close();
                        }

                        break;
                    }

                case "moderation":
                    {
                        var val = parts[3];

                        using (var session = SessionUtility.OpenSession())
                        {
                            var newsgroup = session.Query<Newsgroup>().SingleOrDefault(n => n.Name == name);
                            if (newsgroup == null)
                                Console.WriteLine("No newsgroup named '{0}' exists.", name);
                            else if (_YesStrings.Contains(val, StringComparer.OrdinalIgnoreCase))
                            {
                                newsgroup.Moderated = true;
                                session.SaveOrUpdate(newsgroup);
                                session.Flush();
                                Console.WriteLine("Moderation of newsgroup {0} enabled.", name);
                                _logger?.LogInformation("Moderation of newsgroup {NewsgroupName} enabled", name);
                            }
                            else if (_NoStrings.Contains(val, StringComparer.OrdinalIgnoreCase))
                            {
                                newsgroup.Moderated = false;
                                session.SaveOrUpdate(newsgroup);
                                session.Flush();
                                Console.WriteLine("Moderation of newsgroup {0} disabled.", name);
                                _logger?.LogInformation("Moderation of newsgroup {NewsgroupName} disabled", name);
                            }
                            else
                                Console.WriteLine("Unable to parse '{0}' value. Please use 'on' or 'off' to change this value.", val);

                            session.Close();
                        }

                        break;
                    }

                default:
                    Console.WriteLine("Unknown parameter {0} specified for Group command", parts[2]);
                    break;
            }

            return false;
        }

        /// <summary>
        /// Shows all open connections to the server process
        /// </summary>
        /// <returns>A value indicating whether the server should terminate</returns>
        private static bool ShowConn()
        {
            if (_nntpServer == null) return false;
            
            _nntpServer.ShowBytes = !_nntpServer.ShowBytes;
            Console.WriteLine("\r\nConnections ({0})", _nntpServer.Connections.Count);
            Console.WriteLine("-----------");
            foreach (var connection in _nntpServer.Connections)
            {
                if (connection.AuthenticatedUsername == null)
                    Console.WriteLine("{0}:{1} (nntp)", connection.RemoteAddress, connection.RemotePort);
                else
                    Console.WriteLine("{0}:{1} (nntp:{2})", connection.RemoteAddress, connection.RemotePort, connection.AuthenticatedUsername);
            }

            return false;
        }

        /// <summary>
        /// The Database command handler, which handles console commands for database file and schema management
        /// </summary>
        /// <param name="input">The full console command input</param>
        /// <returns>A value indicating whether the server should terminate</returns>
        private static bool DatabaseCommand(string input)
        {
            var parts = input.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                Console.WriteLine("One parameter is required.");
                return false;
            }

            switch (parts[1].ToLowerInvariant())
            {
                case "annihilate":
                    {
                        DatabaseUtility.RebuildSchema();
                        Console.WriteLine("Database recreated");
                        _logger?.LogInformation("Database recreated via ANNIHILATE command");
                        break;
                    }

                case "update":
                    {
                        DatabaseUtility.UpdateSchema();
                        _logger?.LogInformation("Database schema updated");
                        break;
                    }

                case "verify":
                    {
                        DatabaseUtility.VerifyDatabase();
                        _logger?.LogInformation("Database verification completed");
                        break;
                    }

                default:
                    Console.WriteLine("Unknown parameter {0} specified for Database command", parts[1]);
                    break;
            }

            return false;
        }

        /// <summary>
        /// The Debug command handler, which handles console commands for enabling verbose debug log display
        /// </summary>
        /// <param name="input">The full console command input</param>
        /// <returns>A value indicating whether the server should terminate</returns>
        private static bool DebugCommand(string input)
        {
            var parts = input.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                Console.WriteLine("One parameter is required.");
                return false;
            }

            if (_nntpServer == null) return false;

            switch (parts[1].ToLowerInvariant())
            {
                case "bytes":
                    {
                        var val = parts[2];

                        if (_YesStrings.Contains(val, StringComparer.OrdinalIgnoreCase))
                            _nntpServer.ShowBytes = true;
                        else if (_NoStrings.Contains(val, StringComparer.OrdinalIgnoreCase))
                            _nntpServer.ShowBytes = false;
                        else
                            Console.WriteLine("Unable to parse '{0}' value. Please use 'on' or 'off' to change this value.", val);

                        Console.Write("[DEBUG BYTES: ");
                        var orig = Console.ForegroundColor;
                        Console.ForegroundColor = _nntpServer.ShowBytes ? ConsoleColor.Green : ConsoleColor.Red;
                        Console.Write(_nntpServer.ShowBytes ? "ON" : "OFF");
                        Console.ForegroundColor = orig;
                        Console.Write("]");

                        break;
                    }

                case "commands":
                    {
                        var val = parts[2];

                        if (_YesStrings.Contains(val, StringComparer.OrdinalIgnoreCase))
                            _nntpServer.ShowCommands = true;
                        else if (_NoStrings.Contains(val, StringComparer.OrdinalIgnoreCase))
                            _nntpServer.ShowCommands = false;
                        else
                            Console.WriteLine("Unable to parse '{0}' value. Please use 'on' or 'off' to change this value.", val);

                        Console.Write("[DEBUG COMMANDS: ");
                        var orig = Console.ForegroundColor;
                        Console.ForegroundColor = _nntpServer.ShowCommands ? ConsoleColor.Green : ConsoleColor.Red;
                        Console.Write(_nntpServer.ShowCommands ? "ON" : "OFF");
                        Console.ForegroundColor = orig;
                        Console.Write("]");

                        break;
                    }

                case "data":
                    {
                        var val = parts[2];

                        if (_YesStrings.Contains(val, StringComparer.OrdinalIgnoreCase))
                            _nntpServer.ShowData = true;
                        else if (_NoStrings.Contains(val, StringComparer.OrdinalIgnoreCase))
                            _nntpServer.ShowData = false;
                        else
                            Console.WriteLine("Unable to parse '{0}' value. Please use 'on' or 'off' to change this value.", val);

                        Console.Write("[DEBUG DATA: ");
                        var orig = Console.ForegroundColor;
                        Console.ForegroundColor = _nntpServer.ShowData ? ConsoleColor.Green : ConsoleColor.Red;
                        Console.Write(_nntpServer.ShowData ? "ON" : "OFF");
                        Console.ForegroundColor = orig;
                        Console.Write("]");

                        break;
                    }

                default:
                    Console.WriteLine("Unknown parameter {0} specified for Debug command", parts[1]);
                    break;
            }

            return false;
        }

        /// <summary>
        /// The Peer command handler, which handles peer server management functions
        /// </summary>
        /// <param name="input">The full console command input</param>
        /// <returns>A value indicating whether the server should terminate</returns>
        private static bool PeerCommand(string input)
        {
            var parts = input.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                Console.WriteLine("Three parameters are required.");
                return false;
            }

            var hostname = parts[1].IndexOf(':') > -1
                ? parts[1].Substring(0, parts[1].IndexOf(':'))
                : parts[1];

            switch (parts[2].ToLowerInvariant())
            {
                case "create":
                    {
                        int port;
                        port = parts[1].IndexOf(':') > -1 ? int.TryParse(parts[1].AsSpan(parts[1].IndexOf(':') + 1), out port) ? port : 119 : 119;

                        using var session = SessionUtility.OpenSession();
                        session.Save(new Peer
                        {
                            Hostname = hostname,
                            Port = port,
                            ActiveReceiveDistribution = null,
                            PassiveReceiveDistribution = null,
                            SendDistribution = null
                        });
                        session.Close();
                        Console.WriteLine("Peer {0}:{1} created.", hostname, port);
                        _logger?.LogInformation("Peer {Hostname}:{Port} created", hostname, port);

                        break;
                    }

                case "suck":
                    {
                        var wildmat = parts.Skip(3).Aggregate((c, n) => c + " " + n);

                        using (var session = SessionUtility.OpenSession())
                        {
                            var peer = session.Query<Peer>().SingleOrDefault(n => n.Hostname == hostname);
                            if (peer == null)
                                Console.WriteLine("No peer '{0}' exists.", hostname);
                            else
                            {
                                peer.ActiveReceiveDistribution = string.IsNullOrWhiteSpace(wildmat) ? null : wildmat;
                                session.SaveOrUpdate(peer);
                                session.Flush();
                                Console.WriteLine("Peer {0}'s active 'suck' for newsgroups is wildmat: {1}", hostname, wildmat);
                                _logger?.LogInformation("Peer {Hostname} active 'suck' for newsgroups set to wildmat: {Wildmat}", hostname, wildmat);
                            }

                            session.Close();
                        }

                        break;
                    }

                default:
                    Console.WriteLine("Unknown parameter {0} specified for Peer command", parts[2]);
                    break;
            }

            return false;
        }

        /// <summary>
        /// The Quit command handler, which causes the server process to stop and a value to be returned as 'true',
        /// indicating to the caller the server should terminate.
        /// </summary>
        /// <returns><c>true</c></returns>
        private static bool Quit()
        {
            _nntpServer?.Stop();
            _logger?.LogInformation("Server shutdown requested via QUIT command");
            return true;
        }
        #endregion
    }
}