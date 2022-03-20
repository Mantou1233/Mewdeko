﻿using Discord;
using Discord.WebSocket;
using Mewdeko.Database;
using Mewdeko.Database.Extensions;
using Serilog;

namespace Mewdeko.Modules.Administration.Services;

public class GameVoiceChannelService : INService
{
    private readonly DbService _db;
    private readonly Mewdeko _bot;

    public GameVoiceChannelService(DiscordSocketClient client, DbService db, Mewdeko bot)
    {
        _db = db;
        _bot = bot;

        client.UserVoiceStateUpdated += Client_UserVoiceStateUpdated;
        client.GuildMemberUpdated += _client_GuildMemberUpdated;
    }
    

    private Task _client_GuildMemberUpdated(Cacheable<SocketGuildUser, ulong> cacheable, SocketGuildUser after)
    {
        var _ = Task.Run(async () =>
        {
            try
            {
                if (after is null)
                    return;
                
                if (_bot.AllGuildConfigs[after?.Guild?.Id ?? 0].GameVoiceChannel != after?.VoiceChannel?.Id)
                    return;
                //if the user is in the voice channel and that voice channel is gvc
                //if the activity has changed, and is a playing activity
                if (!Equals(cacheable.Value.Activities, after.Activities)
                    && after.Activities != null
                    && after.Activities.FirstOrDefault()?.Type == ActivityType.Playing)
                    //trigger gvc
                    await TriggerGvc(after, after.Activities.FirstOrDefault()?.Name);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error running GuildMemberUpdated in gvc");
            }
        });
        return Task.CompletedTask;
    }

    public ulong? ToggleGameVoiceChannel(ulong guildId, ulong vchId)
    {
        ulong? id;
        using var uow = _db.GetDbContext();
        var gc = uow.ForGuildId(guildId, set => set);

        if (gc.GameVoiceChannel == vchId)
        {
            _bot.AllGuildConfigs[guildId] = null;
            id = gc.GameVoiceChannel = null;
        }
        else
        {
            _bot.AllGuildConfigs[guildId].GameVoiceChannel = vchId;
            id = gc.GameVoiceChannel = vchId;
        }

        uow.SaveChanges();

        return id;
    }

    private Task Client_UserVoiceStateUpdated(SocketUser usr, SocketVoiceState oldState, SocketVoiceState newState)
    {
        var _ = Task.Run(async () =>
        {
            try
            {
                if (usr is not SocketGuildUser gUser)
                    return;

                var game = gUser.Activities.FirstOrDefault()?.Name;

                if (oldState.VoiceChannel == newState.VoiceChannel ||
                    newState.VoiceChannel == null)
                    return;

                if (_bot.AllGuildConfigs[gUser.Guild.Id].GameVoiceChannel != newState.VoiceChannel.Id ||
                    string.IsNullOrWhiteSpace(game))
                    return;

                await TriggerGvc(gUser, game);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error running VoiceStateUpdate in gvc");
            }
        });

        return Task.CompletedTask;
    }

    private static async Task TriggerGvc(SocketGuildUser gUser, string game)
    {
        if (string.IsNullOrWhiteSpace(game))
            return;

        game = game.TrimTo(50).ToLowerInvariant();
        var vch = gUser.Guild.VoiceChannels
            .FirstOrDefault(x => x.Name.ToLowerInvariant() == game);

        if (vch == null)
            return;

        await Task.Delay(1000).ConfigureAwait(false);
        await gUser.ModifyAsync(gu => gu.Channel = vch).ConfigureAwait(false);
    }
}