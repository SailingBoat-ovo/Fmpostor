using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Fmpostor.Api.Config;
using Fmpostor.Api.Events;
using Fmpostor.Api.Events.Managers;
using Fmpostor.Api.Games;
using Fmpostor.Api.Games.Managers;
using Fmpostor.Api.Net.Custom;
using Fmpostor.Api.Net.Manager;
using Fmpostor.Api.Plugins;
using Fmpostor.Api.Utils;
using Impostor.Hazel.Extensions;
using Fmpostor.Server.Events;
using Fmpostor.Server.Http;
using Fmpostor.Server.Net;
using Fmpostor.Server.Net.Custom;
using Fmpostor.Server.Net.Factories;
using Fmpostor.Server.Net.Manager;
using Fmpostor.Server.Net.Messages;
using Fmpostor.Server.Plugins;
using Fmpostor.Server.Recorder;
using Fmpostor.Server.Utils;
using Fmpostor.Server.WebAdmin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.ObjectPool;
using Serilog;
using Serilog.Events;
using Serilog.Settings.Configuration;

namespace Fmpostor.Server
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .CreateBootstrapLogger();

            try
            {
                // Avoid thread-pool starvation under many concurrent clients: make sure
                // async continuations can always find worker threads on multi-core boxes.
                var minThreads = Math.Max(4, Environment.ProcessorCount);
                ThreadPool.SetMinThreads(minThreads, minThreads);

                // 企业级启动面板。所有信息都在此刻可知(配置已加载), 因此一次画完,
                // 不再用 Thread.Sleep 逐句等待 —— 那 12 秒纯粹是让服主干等。
                PrintStartupBanner();

                var host = CreateHostBuilder(args).Build();

                // Force the auth service to initialize at startup so webadmin_auth.json is
                // created (or rewritten to the current plaintext format) before the server
                // starts accepting requests.
                host.Services.GetRequiredService<WebAdminAuthService>();

                host.Run();
                return 0;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Fmpostor terminated unexpectedly");
                return 1;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static IConfiguration CreateConfiguration(string[] args)
        {
            var configurationBuilder = new ConfigurationBuilder();

            configurationBuilder.SetBasePath(Directory.GetCurrentDirectory());
            configurationBuilder.AddJsonFile("config.json", true);
            configurationBuilder.AddJsonFile("config.Development.json", true);
            configurationBuilder.AddEnvironmentVariables(prefix: "FMPOSTOR_");
            configurationBuilder.AddCommandLine(args);

            return configurationBuilder.Build();
        }


        private static string GetServerIp(ServerConfig? serverConfig)
        {            if (serverConfig != null && !string.IsNullOrEmpty(serverConfig.PublicIp) && serverConfig.PublicIp != "127.0.0.1" && serverConfig.PublicIp != "0.0.0.0")
                return serverConfig.PublicIp;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    var ip = ni.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address));
                    if (ip != null) return ip.Address.ToString();
                }
            }
            catch { }
            return "0.0.0.0";
        }

        private static IHostBuilder CreateHostBuilder(string[] args)
        {
            var configuration = CreateConfiguration(args);
            var pluginConfig = configuration.GetSection("PluginLoader")
                .Get<PluginConfig>() ?? new PluginConfig();
            var httpConfig = configuration.GetSection(HttpServerConfig.Section)
                .Get<HttpServerConfig>() ?? new HttpServerConfig();

            var hostBuilder = Host.CreateDefaultBuilder(args)
                .UseContentRoot(Directory.GetCurrentDirectory())
#if DEBUG
                .UseEnvironment(Environment.GetEnvironmentVariable("FMPOSTOR_ENV") ?? "Development")
#else
                .UseEnvironment("Production")
#endif
                .ConfigureAppConfiguration(builder =>
                {
                    builder.AddConfiguration(configuration);
                })
                .ConfigureServices((host, services) =>
                {
                    var debug = host.Configuration
                        .GetSection(DebugConfig.Section)
                        .Get<DebugConfig>() ?? new DebugConfig();

                    services.AddSingleton<ServerEnvironment>();
                    services.AddSingleton<IServerEnvironment>(p => p.GetRequiredService<ServerEnvironment>());
                    services.AddSingleton<IDateTimeProvider, RealDateTimeProvider>();

                    services.Configure<DebugConfig>(host.Configuration.GetSection(DebugConfig.Section));
                    services.Configure<AntiCheatConfig>(host.Configuration.GetSection(AntiCheatConfig.Section));
                    services.Configure<CompatibilityConfig>(host.Configuration.GetSection(CompatibilityConfig.Section));
                    services.Configure<ServerConfig>(host.Configuration.GetSection(ServerConfig.Section));
                    services.Configure<TimeoutConfig>(host.Configuration.GetSection(TimeoutConfig.Section));
                    services.Configure<ReconnectConfig>(host.Configuration.GetSection(ReconnectConfig.Section));
                    services.Configure<HttpServerConfig>(host.Configuration.GetSection(HttpServerConfig.Section));
                    services.Configure<WebAdminConfig>(host.Configuration.GetSection(WebAdminConfig.Section));

                    services.AddSingleton<ICompatibilityManager, CompatibilityManager>();
                    services.AddSingleton<ClientManager>();
                    services.AddSingleton<IClientManager>(p => p.GetRequiredService<ClientManager>());

                    if (debug.GameRecorderEnabled)
                    {
                        services.AddSingleton<ObjectPoolProvider>(new DefaultObjectPoolProvider());
                        services.AddSingleton<ObjectPool<PacketSerializationContext>>(serviceProvider =>
                        {
                            var provider = serviceProvider.GetRequiredService<ObjectPoolProvider>();
                            var policy = new PacketSerializationContextPooledObjectPolicy();
                            return provider.Create(policy);
                        });

                        services.AddSingleton<PacketRecorder>();
                        services.AddHostedService(sp => sp.GetRequiredService<PacketRecorder>());
                        services.AddSingleton<IClientFactory, ClientFactory<ClientRecorder>>();
                    }
                    else
                    {
                        services.AddSingleton<IClientFactory, ClientFactory<Client>>();
                    }

                    services.AddSingleton<GameManager>();
                    services.AddSingleton<IGameManager>(p => p.GetRequiredService<GameManager>());
                    services.AddSingleton<ListingManager>();

                    services.AddEventPools();
                    services.AddHazel();
                    services.AddSingleton<ICustomMessageManager<ICustomRootMessage>, CustomMessageManager<ICustomRootMessage>>();
                    services.AddSingleton<ICustomMessageManager<ICustomRpc>, CustomMessageManager<ICustomRpc>>();
                    services.AddSingleton<IMessageWriterProvider, MessageWriterProvider>();
                    services.AddSingleton<IGameCodeFactory>(p => p.GetRequiredService<CustomGameCodeService>());
                    services.AddSingleton<IEventManager, EventManager>();
                    services.AddSingleton<Matchmaker>();
                    services.AddSingleton<IDeltaListenerManager>(p => p.GetRequiredService<Matchmaker>());
                    services.AddHostedService<MatchmakerService>();

                    // WebAdmin services
                    services.AddSingleton<WebAdminAuthService>();
                    services.AddSingleton<BanDatabase>();
                    services.AddSingleton<BanService>();
                    services.AddSingleton<GameTrackerService>();
                    services.AddSingleton<ChatService>();
                    services.AddSingleton<LogService>();
                    services.AddSingleton<GameEventHandler>();
                    services.AddSingleton<PlayerIdentityService>();
                    services.AddSingleton<ConnectionLogger>();
                    services.AddSingleton<IEventListener>(p => p.GetRequiredService<GameEventHandler>());

                    // Turbo500 new services
                    services.AddSingleton<WebAdminSettingsService>();
                    services.AddSingleton<DeltaPortPoolService>();
                    services.AddSingleton<BadWordFilterService>();
                    services.AddSingleton<WelcomeService>();
                    services.AddSingleton<ReportService>();
                    services.AddSingleton<PlayerLogService>();
                    services.AddSingleton<ReconnectLogService>();
                    services.AddSingleton<LatencyProbeService>();
                    services.AddHostedService(p => p.GetRequiredService<LatencyProbeService>());
                    services.AddSingleton<NebulaResumeRpc>();
                    services.AddSingleton<PlayerStatsService>();
                    services.AddSingleton<ReactorModService>();
                    services.AddSingleton<CustomGameCodeService>();
                    services.AddSingleton<IEventListener>(p => p.GetRequiredService<CustomGameCodeService>());

                    // Turbo500 innovations
                    services.AddSingleton<BroadcastService>();
                    services.AddSingleton<GameReplayService>();
                    services.AddSingleton<IEventListener>(p => p.GetRequiredService<GameReplayService>());
                    services.AddSingleton<PlayerFootprintService>();
                    services.AddSingleton<RoomCleanupService>();
                    services.AddSingleton<DashboardService>();

                    // Turbo510 AI assistant (depends on PlayerLogService /
                    // PlayerStatsService / PlayerFootprintService / GameReplayService /
                    // ChatService / ReportService — all singletons above, no cycles).
                    services.AddSingleton<AiService>();

                    // Turbo520 integrated plugin features: titles (/title),
                    // play-time tracking (welcome data), auto-start (/auto),
                    // QQ room monitor (/m + OneBot), host transfer (/nexthost).
                    services.AddSingleton<PlayerTimeService>();
                    services.AddHostedService(p => p.GetRequiredService<PlayerTimeService>());
                    services.AddSingleton<TitleService>();
                    services.AddSingleton<IEventListener>(p => p.GetRequiredService<TitleService>());
                    services.AddSingleton<AutoStartService>();
                    services.AddSingleton<IEventListener>(p => p.GetRequiredService<AutoStartService>());
                    services.AddSingleton<AdminStatsService>();
                    services.AddSingleton<RoomMonitorService>();
                    // RoomMonitorService is NOT registered as IEventListener here:
                    // EventManager.GetHandlers enumerates BOTH DI IEventListener
                    // services and manually registered listeners, so keeping the
                    // DI registration while Start() also calls RegisterListener
                    // would fire every [EventListener] twice (duplicate /m pushes).
                    // Start() (called below with ScheduleService) owns registration,
                    // same pattern as PlayerTimeService.
                    services.AddSingleton<ScheduleService>();
            services.AddSingleton<UpdateService>();
                    services.AddSingleton<RoomTransferService>();
                    services.AddHttpClient();
                })
                .UseSerilog((context, loggerConfiguration) =>
                {
#if DEBUG
                    var logLevel = LogEventLevel.Debug;
#else
                    var logLevel = LogEventLevel.Information;
#endif

                    if (args.Contains("--verbose"))
                    {
                        logLevel = LogEventLevel.Verbose;
                    }
                    else if (args.Contains("--errors-only"))
                    {
                        logLevel = LogEventLevel.Error;
                    }

                    static Assembly? LoadSerilogAssembly(AssemblyLoadContext loadContext, AssemblyName name)
                    {
                        var paths = new[] { AppDomain.CurrentDomain.BaseDirectory, Directory.GetCurrentDirectory() };
                        foreach (var path in paths)
                        {
                            try
                            {
                                return loadContext.LoadFromAssemblyPath(Path.Combine(path, name.Name + ".dll"));
                            }
                            catch (FileNotFoundException)
                            {
                            }
                        }

                        return null;
                    }

                    AssemblyLoadContext.Default.Resolving += LoadSerilogAssembly;

                    loggerConfiguration
                        .MinimumLevel.Is(logLevel)
#if DEBUG
                        .MinimumLevel.Override("Microsoft", LogEventLevel.Debug)
#else
                        .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
#endif
                        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                        .Enrich.FromLogContext()
                        .WriteTo.Console()
                        .ReadFrom.Configuration(context.Configuration, new ConfigurationReaderOptions(ConfigurationAssemblySource.AlwaysScanDllFiles));

                    AssemblyLoadContext.Default.Resolving -= LoadSerilogAssembly;
                })
                .UseConsoleLifetime()
                .UsePluginLoader(pluginConfig);

            if (httpConfig.Enabled)
            {
                hostBuilder.ConfigureWebHostDefaults(builder =>
                {
                    builder.ConfigureServices(services =>
                    {
                        services.AddControllers();
                    });

                    builder.Configure(app =>
                    {
                        // CORS: when WebAdmin.CorsAllowedOrigins is configured, only those
                        // origins may call the API cross-origin; otherwise keep the legacy
                        // any-origin (credentials are never allowed) behaviour.
                        var corsOrigins = configuration.GetSection(WebAdminConfig.Section).Get<WebAdminConfig>()?.CorsAllowedOrigins ?? new System.Collections.Generic.List<string>();
                        var validOrigins = corsOrigins.Where(o => !string.IsNullOrWhiteSpace(o)).ToArray();
                        if (validOrigins.Length > 0)
                        {
                            app.UseCors(policy => policy.WithOrigins(validOrigins).AllowAnyMethod().AllowAnyHeader());
                        }
                        else
                        {
                            app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
                        }

                        // Security hardening (Turbo-620): response headers, request body caps,
                        // CSRF origin checks and the default-password lockdown.
                        app.UseMiddleware<SecurityHeadersMiddleware>();
                        app.UseMiddleware<RequestBodyLimitMiddleware>();
                        app.UseMiddleware<WebAdminCsrfMiddleware>();
                        app.UseMiddleware<WebAdminPendingPasswordMiddleware>();

                        // Start the scheduled-task engine with the panel.
                        app.ApplicationServices.GetRequiredService<ScheduleService>().Start();

                        // Turbo-620 fix: the QQ monitor receive loop was never
                        // started (Start() was never called), so group commands
                        // like #在线状态 were never detected. /m and panel
                        // broadcasts still worked because they are direct HTTP
                        // sends that do not depend on this loop.
                        app.ApplicationServices.GetRequiredService<RoomMonitorService>().Start();

                        // Mod-state RPC receiver for the reconnect / spectator features.
                        //
                        // Gated on the feature switches on purpose: registering a handler on
                        // the RPC id a mod uses also stops those packets from falling
                        // through to the "unregistered call" path (which reports mods as
                        // protocol-extension cheats). That side effect is desirable for
                        // modded servers, but it is still a change of anti-cheat posture,
                        // and the feature promises "disabled = behaviour unchanged".
                        // With both switches off, RPC 128 keeps the vanilla treatment.
                        //
                        // 观战也必须算在内: 房主的 kind=9 能力声明就走这个通道, 只开观战
                        // 不开重连的话, 声明永远收不到, 观战会被闸门一直拒掉。
                        var reconnectOptions = app.ApplicationServices
                            .GetRequiredService<Microsoft.Extensions.Options.IOptions<ReconnectConfig>>()
                            .Value;

                        if (reconnectOptions.Enabled || reconnectOptions.EnableSpectator)
                        {
                            var customRpcs = app.ApplicationServices.GetRequiredService<ICustomMessageManager<ICustomRpc>>();
                            customRpcs.Register(app.ApplicationServices.GetRequiredService<NebulaResumeRpc>());
                            Log.Information(
                                "[Reconnect] Mod-state RPC receiver registered (RPC {RpcId}); reconnect={Reconnect}, spectator={Spectator}.",
                                NebulaResumeRpc.RpcId,
                                reconnectOptions.Enabled,
                                reconnectOptions.EnableSpectator);
                        }

                        // 四句启动提示已移至 Main 开头（横幅后逐句弹出）
                        var pluginLoaderService = app.ApplicationServices.GetRequiredService<PluginLoaderService>();
                        foreach (var pluginInformation in pluginLoaderService.Plugins)
                        {
                            if (pluginInformation.Startup is IPluginHttpStartup httpStartup)
                            {
                                httpStartup.ConfigureWebApplication(app);
                            }
                        }

                        app.UseRouting();

                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapControllers();
                        });
                    });

                    builder.ConfigureKestrel(serverOptions =>
                    {
                        serverOptions.Listen(IPAddress.Parse(httpConfig.ListenIp), httpConfig.ListenPort);
                    });
                });
            }

            return hostBuilder;
        }

        /// <summary>Current build version shown in the startup panel and the web panel.</summary>
        private const string DisplayVersion = "Turbo-700.0-20261024";

        /// <summary>
        ///     Prints the startup panel.
        /// </summary>
        /// <remarks>
        ///     设计约束:
        ///       · 中文是双宽字符, 用 PadRight 数字符会让右边框错位 —— 所以按【显示宽度】补齐;
        ///       · 不写死任何运行期信息: 版本、端口、公网 IP、配置文件状态都从配置读;
        ///       · 一次画完并且立即返回, 不阻塞启动;
        ///       · 没有颜色时(重定向到文件)也能安全输出。
        /// </remarks>
        private static void PrintStartupBanner()
        {
            var version = DisplayVersion;
            var listenPort = 22023;
            var publicIp = "0.0.0.0";
            var httpEnabled = true;
            var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            var configExists = File.Exists(configPath);

            try
            {
                var config = CreateConfiguration(Array.Empty<string>());
                listenPort = config.GetValue("Server:ListenPort", listenPort);
                publicIp = config.GetValue("Server:PublicIp", publicIp) ?? publicIp;
                httpEnabled = config.GetValue("HttpServer:Enabled", httpEnabled);
            }
            catch (Exception)
            {
                // 读配置失败不该影响启动, 面板用默认值继续画。
            }

            const int width = 58; // 内框宽度(不含左右边框)

            Console.WriteLine();

            WriteBorder('╔', '═', '╗', width, ConsoleColor.DarkCyan);
            WritePanelLine("帆船 Impostor 服务端", width, ConsoleColor.Cyan, center: true);
            WritePanelLine(version, width, ConsoleColor.DarkGray, center: true);

            WriteBorder('╠', '═', '╣', width, ConsoleColor.DarkCyan);

            WritePanelLine($"  监听端口   {listenPort}", width, ConsoleColor.Gray);
            WritePanelLine($"  公网地址   {publicIp}:{listenPort}", width, ConsoleColor.Gray);
            WritePanelLine($"  HTTP 服务  {(httpEnabled ? "已启用" : "已禁用")}", width, ConsoleColor.Gray);
            WritePanelLine($"  配置文件   {(configExists ? "已加载" : "未找到(使用默认值)")}", width,
                configExists ? ConsoleColor.Gray : ConsoleColor.Yellow);

            WriteBorder('╠', '═', '╣', width, ConsoleColor.DarkCyan);

            WritePanelLine("  状态      正在启动…", width, ConsoleColor.Yellow);

            WriteBorder('╚', '═', '╝', width, ConsoleColor.DarkCyan);

            Console.WriteLine();
            WriteHint("提示", "首次运行请检查 config.json 里的 Reconnect 段", ConsoleColor.DarkGray);
            WriteHint("反馈", "admin@fanchuanovo.cn", ConsoleColor.DarkGray);
            Console.WriteLine();
        }

        /// <summary>Writes one horizontal border line with the given corner/joint characters.</summary>
        private static void WriteBorder(char left, char fill, char right, int width, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.Write(left);
            Console.Write(new string(fill, width));
            Console.WriteLine(right);
            Console.ResetColor();
        }

        /// <summary>
        ///     Writes one panel row, padding by <em>display width</em> so CJK characters keep
        ///     the right border aligned.
        /// </summary>
        private static void WritePanelLine(string text, int width, ConsoleColor color, bool center = false)
        {
            var displayWidth = DisplayWidth(text);
            var padding = Math.Max(0, width - displayWidth);

            var leftPad = center ? padding / 2 : 0;
            var rightPad = padding - leftPad;

            Console.ForegroundColor = color;
            Console.Write('║');
            Console.Write(new string(' ', leftPad));
            Console.Write(text);
            Console.Write(new string(' ', rightPad));
            Console.WriteLine('║');
            Console.ResetColor();
        }

        /// <summary>Writes a "label  value" hint line under the panel.</summary>
        private static void WriteHint(string label, string value, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.Write("  ");
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write(label.PadRight(6));
            Console.ForegroundColor = color;
            Console.WriteLine(value);
            Console.ResetColor();
        }

        /// <summary>
        ///     Terminal display width of a string: CJK / full-width characters count as 2,
        ///     combining marks as 0. Without this, every Chinese line shifts the border.
        /// </summary>
        private static int DisplayWidth(string text)
        {
            var width = 0;

            foreach (var rune in text.EnumerateRunes())
            {
                var value = rune.Value;

                if (value == 0)
                {
                    continue;
                }

                // Combining marks and zero-width characters.
                if (value is >= 0x0300 and <= 0x036F or 0x200B or 0xFEFF)
                {
                    continue;
                }

                width += IsFullWidth(value) ? 2 : 1;
            }

            return width;
        }

        /// <summary>True for CJK / full-width ranges that occupy two terminal cells.</summary>
        private static bool IsFullWidth(int value)
        {
            return value is >= 0x1100 and <= 0x115F      // Hangul Jamo
                or >= 0x2E80 and <= 0x303E               // CJK radicals, punctuation
                or >= 0x3041 and <= 0x33FF               // Hiragana, Katakana, CJK symbols
                or >= 0x3400 and <= 0x4DBF               // CJK Ext A
                or >= 0x4E00 and <= 0x9FFF               // CJK Unified
                or >= 0xA000 and <= 0xA4CF               // Yi
                or >= 0xAC00 and <= 0xD7A3               // Hangul syllables
                or >= 0xF900 and <= 0xFAFF               // CJK compatibility
                or >= 0xFE30 and <= 0xFE6F               // CJK compatibility forms
                or >= 0xFF00 and <= 0xFF60               // Full-width forms
                or >= 0xFFE0 and <= 0xFFE6               // Full-width signs
                or >= 0x1F300 and <= 0x1F64F             // Emoji
                or >= 0x1F900 and <= 0x1F9FF             // Supplemental emoji
                or >= 0x20000 and <= 0x3FFFD;            // CJK Ext B+
        }

        private static void ColorWrite(string text, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }
}
