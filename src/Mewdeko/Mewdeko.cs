﻿using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using Fergun.Interactive;
using KSoftNet;
using Mewdeko._Extensions;
using Mewdeko.Common;
using Mewdeko.Common.Configs;
using Mewdeko.Common.Extensions;
using Mewdeko.Common.ModuleBehaviors;
using Mewdeko.Common.PubSub;
using Mewdeko.Modules.CustomReactions.Services;
using Mewdeko.Modules.Gambling.Services;
using Mewdeko.Modules.Gambling.Services.Impl;
using Mewdeko.Modules.OwnerOnly.Services;
using Mewdeko.Services.Database.Models;
using Mewdeko.Services.Impl;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Serilog;
using StackExchange.Redis;
using Victoria;
using RunMode = Discord.Commands.RunMode;

namespace Mewdeko.Services;

public class Mewdeko
{
    private readonly DbService _db;
    private const string TOKEN = "95dd4f5d54692fc533bd1da43f1cab773c71d894";

    public Mewdeko(int shardId)
    {
        if (shardId < 0)
            throw new ArgumentOutOfRangeException(nameof(shardId));


        Credentials = new BotCredentials();
        Cache = new RedisCache(Credentials, shardId);
        _db = new DbService(Credentials);

        if (shardId == 0) _db.Setup();

        Client = new DiscordSocketClient(new DiscordSocketConfig
        {
            MessageCacheSize = 15,
            LogLevel = LogSeverity.Warning,
            ConnectionTimeout = int.MaxValue,
            TotalShards = Credentials.TotalShards,
            ShardId = shardId,
            AlwaysDownloadUsers = true,
            GatewayIntents = GatewayIntents.All,
            LogGatewayIntentWarnings = false
        });
        ;

        CommandService = new CommandService(new CommandServiceConfig
        {
            CaseSensitiveCommands = false,
            DefaultRunMode = RunMode.Async
        });
        #if DEBUG
            Client.Log += Client_Log;
        #endif
    }

    private BotCredentials Credentials { get; }
    public DiscordSocketClient Client { get; }
    private CommandService CommandService { get; }
    public ImmutableArray<GuildConfig> AllGuildConfigs { get; private set; }

    public static Color OkColor { get; set; }
    public static Color ErrorColor { get; set; }

    public TaskCompletionSource<bool> Ready { get; } = new();

    private IServiceProvider Services { get; set; }
    private IDataCache Cache { get; }


    public event Func<GuildConfig, Task> JoinedGuild = delegate { return Task.CompletedTask; };


    public List<ulong> GetCurrentGuildIds() => Client.Guilds.Select(x => x.Id).ToList();

    private void AddServices()
    {
        var startingGuildIdList = GetCurrentGuildIds();
        var sw = Stopwatch.StartNew();
        var bot = Client.CurrentUser;

        using (var uow = _db.GetDbContext())
        {
            uow.DiscordUsers.EnsureCreated(bot.Id, bot.Username, bot.Discriminator, bot.AvatarId);
            AllGuildConfigs = uow.GuildConfigs.GetAllGuildConfigs(startingGuildIdList).ToImmutableArray();
        }

        var s = new ServiceCollection()
            .AddSingleton<IBotCredentials>(Credentials);
            Log.Warning("Got to creds");
                s.AddSingleton(_db);
                Log.Warning("Got to db");
                s.AddSingleton(Client);
                Log.Warning("Got to client");
                s.AddSingleton(CommandService);
                Log.Warning("Got to commandservice");
                s.AddSingleton(this);
                Log.Warning("Got to Mewdeko");
                s.AddSingleton(Cache);
                Log.Warning("Got to Cache");
                s.AddSingleton(new KSoftApi(TOKEN));
                Log.Warning("Got to ksoft");
                s.AddSingleton(Cache.Redis);
                Log.Warning("Got to Redis");
                s.AddSingleton<ISeria, JsonSeria>();
                Log.Warning("Got to configs");
                s.AddSingleton<IPubSub, RedisPubSub>();
                Log.Warning("Got to pubsub");
                s.AddSingleton<IConfigSeria, YamlSeria>();
                Log.Warning("Got to gconfigs");
                s.AddSingleton<InteractiveService>();
                Log.Warning("Got to InteractiveService");
                s.AddSingleton<InteractionService>();
                Log.Warning("Got to InteractionService");
                s.AddConfigServices();
                Log.Warning("Got to ConfigService");
                s.AddBotStringsServices();
                Log.Warning("Got to BotStrings");
                s.AddMemoryCache();
                Log.Warning("Got to MemoryCache");
                s.AddSingleton<LavaNode>();
                Log.Warning("Got to lavanode");
                s.AddSingleton<LavaConfig>();
                Log.Warning("Got to LavaConfig");
            s.AddSingleton<IShopService, ShopService>();
            Log.Warning("Got to ShopService");
        s.AddLavaNode(x =>
        {
            x.SelfDeaf = true;
            x.Authorization = "Hope4a11";
            x.Port = 2333;
        });

        s.AddHttpClient();
        s.AddHttpClient("memelist").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        if (Environment.GetEnvironmentVariable("MEWDEKO_IS_COORDINATED") != "1")
            s.AddSingleton<ICoordinator, SingleProcessCoordinator>();
        else
            s.AddSingleton<RemoteGrpcCoordinator>()
                .AddSingleton<ICoordinator>(x => x.GetRequiredService<RemoteGrpcCoordinator>())
                .AddSingleton<IReadyExecutor>(x => x.GetRequiredService<RemoteGrpcCoordinator>());

        s.LoadFrom(Assembly.GetAssembly(typeof(CommandHandler))!);

        s.AddSingleton<IReadyExecutor>(x => x.GetService<OwnerOnlyService>());
        s.AddSingleton<IReadyExecutor>(x => x.GetService<CustomReactionsService>());
        //initialize Services
        Services = s.BuildServiceProvider();
        var commandHandler = Services.GetService<CommandHandler>();
        commandHandler.AddServices(s);
        _ = LoadTypeReaders(typeof(Mewdeko).Assembly);

        sw.Stop();
        Log.Information($"All services loaded in {sw.Elapsed.TotalSeconds:F2}s");
    }


    private IEnumerable<object> LoadTypeReaders(Assembly assembly)
    {
        Type[] allTypes;
        try
        {
            allTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            Log.Warning(ex.LoaderExceptions[0], "Error getting types");
            return Enumerable.Empty<object>();
        }

        var filteredTypes = allTypes
            .Where(x => x.IsSubclassOf(typeof(TypeReader))
                        && x.BaseType.GetGenericArguments().Length > 0
                        && !x.IsAbstract);

        var toReturn = new List<object>();
        foreach (var ft in filteredTypes)
        {
            var x = (TypeReader) Activator.CreateInstance(ft, Client, CommandService);
            var baseType = ft.BaseType;
            var typeArgs = baseType?.GetGenericArguments();
            if (typeArgs != null) CommandService.AddTypeReader(typeArgs[0], x);
            toReturn.Add(x);
        }

        return toReturn;
    }

    private async Task LoginAsync(string token)
    {
        var clientReady = new TaskCompletionSource<bool>();

        Task SetClientReady()
        {
            var _ = Task.Run(async () =>
            {
                clientReady.TrySetResult(true);
                try
                {
                    foreach (var chan in await Client.GetDMChannelsAsync().ConfigureAwait(false))
                        await chan.CloseAsync().ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            });
            return Task.CompletedTask;
        }

        //connect
        Log.Information("Shard {0} logging in ...", Client.ShardId);
        try
        {
            await Client.LoginAsync(TokenType.Bot, token).ConfigureAwait(false);
            await Client.StartAsync().ConfigureAwait(false);
        }
        catch (HttpException ex)
        {
            LoginErrorHandler.Handle(ex);
            Helpers.ReadErrorAndExit(3);
        }
        catch (Exception ex)
        {
            LoginErrorHandler.Handle(ex);
            Helpers.ReadErrorAndExit(4);
        }

        Client.Ready += SetClientReady;
        await clientReady.Task.ConfigureAwait(false);
        Client.Ready -= SetClientReady;
        Client.JoinedGuild += Client_JoinedGuild;
        Client.LeftGuild += Client_LeftGuild;
        Log.Information("Shard {0} logged in.", Client.ShardId);
    }

    private Task Client_LeftGuild(SocketGuild arg)
    {
        try
        {
            var chan = Client.Rest.GetChannelAsync(892789588739891250).Result as RestTextChannel;
            chan.SendErrorAsync($"Left server: {arg.Name} [{arg.Id}]");
        }
        catch
        {
            //ignored
        }

        Log.Information("Left server: {0} [{1}]", arg.Name, arg.Id);
        return Task.CompletedTask;
    }

    private Task Client_JoinedGuild(SocketGuild arg)
    {
        arg.DownloadUsersAsync();
        Log.Information("Joined server: {0} [{1}]", arg.Name, arg.Id);
        var _ = Task.Run(async () =>
        {
            GuildConfig gc;
            using (var uow = _db.GetDbContext())
            {
                gc = uow.GuildConfigs.ForId(arg.Id);
            }

            await JoinedGuild.Invoke(gc).ConfigureAwait(false);
        });

        var chan = Client.Rest.GetChannelAsync(892789588739891250).Result as RestTextChannel;
        var eb = new EmbedBuilder();
        eb.WithTitle($"Joined {Format.Bold(arg.Name)}");
        eb.AddField("Server ID", arg.Id);
        eb.AddField("Members", arg.MemberCount);
        eb.AddField("Boosts", arg.PremiumSubscriptionCount);
        eb.AddField("Owner", $"Name: {arg.Owner}\nID: {arg.OwnerId}");
        eb.AddField("Text Channels", arg.TextChannels.Count);
        eb.AddField("Voice Channels", arg.VoiceChannels.Count);
        eb.WithThumbnailUrl(arg.IconUrl);
        eb.WithColor(OkColor);
        chan.SendMessageAsync(embed: eb.Build());
        return Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        var sw = Stopwatch.StartNew();

        await LoginAsync(Credentials.Token).ConfigureAwait(false);

        Log.Information("Shard {ShardId} loading services...", Client.ShardId);
        try
        {
            AddServices();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error adding services");
            Helpers.ReadErrorAndExit(9);
        }

        sw.Stop();
        Log.Information("Shard {ShardId} connected in {Elapsed:F2}s", Client.ShardId, sw.Elapsed.TotalSeconds);
        var commandService = Services.GetService<CommandService>();
        var interactionService = Services.GetRequiredService<InteractionService>();
        var lava = Services.GetRequiredService<LavaNode>();
        await lava.ConnectAsync();
        await commandService!.AddModulesAsync(GetType().GetTypeInfo().Assembly, Services)
                             .ConfigureAwait(false);
        await interactionService.AddModulesAsync(GetType().GetTypeInfo().Assembly, Services)
            .ConfigureAwait(false);
#if  !DEBUG
        if (Client.ShardId == 0)
            await interactionService.RegisterCommandsGloballyAsync();
#endif
#if DEBUG
        await interactionService.RegisterCommandsToGuildAsync(900378009188565022);
#endif

        // start handling messages received in commandhandler


        HandleStatusChanges();
        Ready.TrySetResult(true);
        _ = Task.Run(ExecuteReadySubscriptions);
        Log.Information("Shard {ShardId} ready", Client.ShardId);
    }

    private Task ExecuteReadySubscriptions()
    {
        var readyExecutors = Services.GetServices<IReadyExecutor>();
        var tasks = readyExecutors.Select(async toExec =>
        {
            try
            {
                await toExec.OnReadyAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed running OnReadyAsync method on {Type} type: {Message}",
                    toExec.GetType().Name, ex.Message);
            }
        });

        return Task.WhenAll(tasks);
    }

    private static Task Client_Log(LogMessage arg)
    {
        if (arg.Exception != null)
            Log.Warning(arg.Exception, arg.Source + " | " + arg.Message);
        else
            Log.Warning(arg.Source + " | " + arg.Message);

        return Task.CompletedTask;
    }

    public async Task RunAndBlockAsync()
    {
        await RunAsync().ConfigureAwait(false);
        await Task.Delay(-1).ConfigureAwait(false);
    }


    private void HandleStatusChanges()
    {
        var sub = Services.GetService<IDataCache>()!.Redis.GetSubscriber();
        sub.Subscribe(Client.CurrentUser.Id + "_status.game_set", async (_, game) =>
        {
            try
            {
                var obj = new {Name = default(string), Activity = ActivityType.Playing};
                obj = JsonConvert.DeserializeAnonymousType(game, obj);
                await Client.SetGameAsync(obj.Name, type: obj.Activity).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error setting game");
            }
        }, CommandFlags.FireAndForget);

        sub.Subscribe(Client.CurrentUser.Id + "_status.stream_set", async (_, streamData) =>
        {
            try
            {
                var obj = new {Name = "", Url = ""};
                obj = JsonConvert.DeserializeAnonymousType(streamData, obj);
                await Client.SetGameAsync(obj?.Name, obj!.Url, ActivityType.Streaming).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error setting stream");
            }
        }, CommandFlags.FireAndForget);
    }

    public Task SetGameAsync(string game, ActivityType type)
    {
        var obj = new {Name = game, Activity = type};
        var sub = Services.GetService<IDataCache>()!.Redis.GetSubscriber();
        return sub.PublishAsync(Client.CurrentUser.Id + "_status.game_set", JsonConvert.SerializeObject(obj));
    }
}