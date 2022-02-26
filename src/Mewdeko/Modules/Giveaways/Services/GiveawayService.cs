﻿using Discord;
using Discord.WebSocket;
using Mewdeko._Extensions;
using Mewdeko.Database;
using Mewdeko.Database.Extensions;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Swan;
using System.Collections.Concurrent;

namespace Mewdeko.Modules.Giveaways.Services;

public class GiveawayService : INService
{
    private readonly DiscordSocketClient _client;
    private readonly IBotCredentials _creds;
    private readonly DbService _db;
    public ConcurrentDictionary<ulong, string> GiveawayEmotes;

    public GiveawayService(DiscordSocketClient client, DbService db, IBotCredentials creds, Mewdeko bot)
    {
        _client = client;
        _db = db;
        _creds = creds;
        _ = StartGiveawayLoop();
        GiveawayEmotes = bot.AllGuildConfigs
                            .ToDictionary(x => x.GuildId, x => x.GiveawayEmote)
                            .ToConcurrent();
    }

    private async Task StartGiveawayLoop()
    {
        while (true)
        {
            await Task.Delay(2000);
            try
            {
                var now = DateTime.UtcNow;
                var reminders = await GetGiveawaysBeforeAsync(now);
                if (reminders.Count == 0)
                    continue;

                Log.Information($"Executing {reminders.Count} giveaways.");

                // make groups of 5, with 1.5 second inbetween each one to ensure against ratelimits
                var i = 0;
                foreach (var group in reminders
                             .GroupBy(_ => ++i / ((reminders.Count / 5) + 1)))
                {
                    var executedGiveaways = group.ToList();
                    await Task.WhenAll(executedGiveaways.Select(GiveawayTimerAction));
                    await UpdateGiveaways(executedGiveaways);
                    await Task.Delay(1500);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Error in Giveaway loop: {ex.Message}");
                Log.Warning(ex.ToString());
            }
        }
    }

    public async Task SetGiveawayEmote(IGuild guild, string emote)
    {
        await using (var uow = _db.GetDbContext())
        {
            var gc = uow.ForGuildId(guild.Id, set => set);
            gc.GiveawayEmote = emote;
            await uow.SaveChangesAsync();
        }

        GiveawayEmotes.AddOrUpdate(guild.Id, emote, (_, _) => emote);
    }

    public string GetGiveawayEmote(ulong? id)
    {
        GiveawayEmotes.TryGetValue(id.Value, out var emote);
        return emote;
    }
    private async Task UpdateGiveaways(List<global::Mewdeko.Database.Models.Giveaways> g)
    {
        await using var uow = _db.GetDbContext();
        foreach (var i in g)
        {
            var toupdate = new global::Mewdeko.Database.Models.Giveaways
            {
                When = i.When,
                BlacklistRoles = i.BlacklistRoles,
                BlacklistUsers = i.BlacklistUsers,
                ChannelId = i.ChannelId,
                Ended = 1,
                MessageId = i.MessageId,
                RestrictTo = i.RestrictTo,
                Item = i.Item,
                ServerId = i.ServerId,
                UserId = i.UserId,
                Winners = i.Winners
            };
            uow.Giveaways.Remove(i);
            uow.Giveaways.Add(toupdate);
            await uow.SaveChangesAsync();
        }
    }

    private Task<List<global::Mewdeko.Database.Models.Giveaways>> GetGiveawaysBeforeAsync(DateTime now)
    {
        using var uow = _db.GetDbContext();
        return uow.Giveaways
            .FromSqlInterpolated(
                $"select * from giveaways where ((serverid >> 22) % {_creds.TotalShards}) == {_client.ShardId} and \"when\" < {now} and \"Ended\" == 0;")
            .ToListAsync();
    }

    public async Task GiveawaysInternal(ITextChannel chan, TimeSpan ts, string item, int winners, ulong host,
        ulong serverId, ITextChannel currentChannel, IGuild guild, string reqroles = null, string blacklistusers = null,
        string blacklistroles = null, IDiscordInteraction interaction = null)
    {
        var hostuser = await guild.GetUserAsync(host);
        var emote = GetGiveawayEmote(guild.Id).ToIEmote();
        var eb = new EmbedBuilder
        {
            Color = Mewdeko.OkColor,
            Title = item,
            Description =
                $"React with {emote} to enter!\n" +
                $"Hosted by {hostuser.Mention}\n" +
                $"End Time: <t:{DateTime.UtcNow.Add(ts).ToUnixEpochDate()}:R> (<t:{DateTime.UtcNow.Add(ts).ToUnixEpochDate()}>)\n",
            Footer = new EmbedFooterBuilder()
                .WithText($"{winners} Winners | Mewdeko Giveaways")
        };
        if (!string.IsNullOrEmpty(reqroles))
        {
            var splitreqs = reqroles.Split(" ");
            var reqrolesparsed = new List<IRole>();
            foreach (var i in splitreqs)
            {
                if (!ulong.TryParse(i, out var parsed)) continue;
                try
                {
                    reqrolesparsed.Add(guild.GetRole(parsed));
                }
                catch
                {
                    //ignored 
                }
            }
            if (reqrolesparsed.Any())
                eb.WithDescription($"React with {emote} to enter!\n"
                               + $"Hosted by {hostuser.Mention}\n"
                               + $"Required Roles: {string.Join("\n", reqrolesparsed.Select(x => x.Mention))}\n"
                               + $"End Time: <t:{DateTime.UtcNow.Add(ts).ToUnixEpochDate()}:R> (<t:{DateTime.UtcNow.Add(ts).ToUnixEpochDate()}>)\n");
        }
        var msg = await chan.SendMessageAsync(embed: eb.Build());
        await msg.AddReactionAsync(emote);
        var time = DateTime.UtcNow + ts;
        var rem = new global::Mewdeko.Database.Models.Giveaways
        {
            ChannelId = chan.Id,
            UserId = host,
            ServerId = serverId,
            Ended = 0,
            When = time,
            Item = item,
            MessageId = msg.Id,
            Winners = winners,
            Emote = emote.ToString()
        };
        if (!string.IsNullOrWhiteSpace(reqroles))
            rem.RestrictTo = reqroles;

        await using (var uow = _db.GetDbContext())
        {
            uow.Giveaways.Add(rem);
            await uow.SaveChangesAsync();
        }

        if (interaction is not null)
            await interaction.SendConfirmFollowupAsync($"Giveaway started in {chan.Mention}");
        else
            await currentChannel.SendConfirmAsync($"Giveaway started in {chan.Mention}");
    }
    
    public async Task GiveawayTimerAction(global::Mewdeko.Database.Models.Giveaways r)
    {
        if (_client.GetGuild(r.ServerId) is null)
            return;
        if (_client.GetGuild(r.ServerId).GetTextChannel(r.ChannelId) is null)
            return;
        var guild = _client.GetGuild(r.ServerId);
        IUserMessage ch;
        try
        {
            if (await _client.GetGuild(r.ServerId)?.GetTextChannel(r.ChannelId).GetMessageAsync(r.MessageId)! is not
                IUserMessage ch1)
                return;
            ch = ch1;
        }
        catch
        {
            return;
        }

        var uow = _db.GetDbContext();
        var emote = r.Emote.ToIEmote();
        if (emote.Name == null)
        {
            await ch.Channel.SendErrorAsync($"[This Giveaway]({ch.GetJumpUrl()}) failed because the emote used for it is invalid!");
            
        }
        var reacts = await ch.GetReactionUsersAsync(emote, 999999).FlattenAsync();
        if (reacts.Count() - 1 <= r.Winners)
        {
            var eb = new EmbedBuilder
            {
                Color = Mewdeko.ErrorColor,
                Description = "There were not enough participants!"
            };
            await ch.ModifyAsync(x => x.Embed = eb.Build());
            r.Ended = 1;
            uow.Giveaways.Update(r);
            await uow.SaveChangesAsync();
        }
        else
        {
            if (r.Winners == 1)
            {
                
                var users = reacts.Where(x => !x.IsBot).Select(x => guild.GetUser(x.Id)).ToList();
                if (r.RestrictTo is not null)
                {

                    var parsedreqs = new List<ulong>();
                    var split = r.RestrictTo.Split(" ");
                    foreach (var i in split)
                    {
                        if (ulong.TryParse(i, out var parsed))
                        {
                            parsedreqs.Add(parsed);
                        }
                    }

                    try
                    {
                        if (parsedreqs.Any())
                            users = users.Where(x => x.Roles.Select(x => x.Id).Intersect(parsedreqs).Count() == parsedreqs.Count)
                                         .ToList();
                    }
                    catch
                    {
                        return;
                    }
                }

                if (!users.Any())
                {
                    var eb1 = new EmbedBuilder().WithErrorColor()
                                                .WithDescription(
                                                    "Looks like nobody that actually met the role requirements joined..")
                                                .Build();
                    await ch.ModifyAsync(x => x.Embed = eb1);
                    return;
                }
                var rand = new Random();
                var index = rand.Next(users.Count);
                var user = users.ToList()[index];
                var eb = new EmbedBuilder
                {
                    Color = Mewdeko.OkColor,
                    Description = $"{user.Mention} won the giveaway for {r.Item}!"
                };
                await ch.ModifyAsync(x => x.Embed = eb.Build());
                await ch.Channel.SendMessageAsync($"{user.Mention} won the giveaway for {r.Item}!",
                    embed: new EmbedBuilder().WithOkColor().WithDescription($"[Jump To Giveaway]({ch.GetJumpUrl()})")
                        .Build());
                r.Ended = 1;
                uow.Giveaways.Update(r);
                await uow.SaveChangesAsync();
            }
            else
            {
                var rand = new Random();
                 var users = reacts.Where(x => !x.IsBot).Select(x => guild.GetUser(x.Id)).ToList();
                if (r.RestrictTo is not null)
                {

                    var parsedreqs = new List<ulong>();
                    var split = r.RestrictTo.Split(" ");
                    Console.Write(split.Length);
                    foreach (var i in split)
                    {
                        if (ulong.TryParse(i, out var parsed))
                        {
                            parsedreqs.Add(parsed);
                        }
                    }

                    try
                    {
                        if (parsedreqs.Any())
                            users = users.Where(x => x.Roles.Select(x => x.Id).Intersect(parsedreqs).Count() == parsedreqs.Count)
                                         .ToList();
                    }
                    catch
                    {
                        return;
                    }
                }

                if (!users.Any())
                {
                    var eb1 = new EmbedBuilder().WithErrorColor()
                                                .WithDescription(
                                                    "Looks like nobody that actually met the role requirements joined..")
                                                .Build();
                    await ch.ModifyAsync(x => x.Embed = eb1);
                }
                var winners = users.ToList().OrderBy(_ => rand.Next()).Take(r.Winners);
                var eb = new EmbedBuilder
                {
                    Color = Mewdeko.OkColor,
                    Description = $"{string.Join("", winners.Select(x => x.Mention))} won the giveaway for {r.Item}!"
                };
                await ch.ModifyAsync(x => x.Embed = eb.Build());
                foreach (var winners2 in winners.Chunk(50))
                {
                    await ch.Channel.SendMessageAsync(
                        $"{string.Join("", winners2.Select(x => x.Mention))} won the giveaway for {r.Item}!",
                        embed: new EmbedBuilder().WithOkColor().WithDescription($"[Jump To Giveaway]({ch.GetJumpUrl()})")
                                                 .Build());
                }
                r.Ended = 1;
                uow.Giveaways.Update(r);
                await uow.SaveChangesAsync();
            }
        }
    }

    public async Task GiveawayReroll(global::Mewdeko.Database.Models.Giveaways r)
    {
        if (_client.GetGuild(r.ServerId) is null)
            return;
        if (_client.GetGuild(r.ServerId).GetTextChannel(r.ChannelId) is null)
            return;
        var guild = _client.GetGuild(r.ServerId);
        IUserMessage ch;
        try
        {
            if (await _client.GetGuild(r.ServerId)?.GetTextChannel(r.ChannelId).GetMessageAsync(r.MessageId)! is not
                IUserMessage ch1)
                return;
            ch = ch1;
        }
        catch
        {
            return;
        }

        var uow = _db.GetDbContext();
        var emote = r.Emote.ToIEmote();
        if (emote.Name == null)
        {
            var eb = new EmbedBuilder().WithErrorColor()
                                       .WithDescription(
                                           "Giveaway failed because the emote used in this giveaway is invalid!");
            await ch.ModifyAsync(x => x.Embed = eb.Build());
            r.Ended = 1;
            uow.Giveaways.Update(r);
            await uow.SaveChangesAsync();
            return;
            
            
        }
        var reacts = await ch.GetReactionUsersAsync(emote, 999999).FlattenAsync();
        if (reacts.Count() - 1 <= r.Winners)
        {
            var eb = new EmbedBuilder
            {
                Color = Mewdeko.ErrorColor, Description = "There were not enough participants!"
            };
            await ch.ModifyAsync(x => x.Embed = eb.Build());
            r.Ended = 1;
            uow.Giveaways.Update(r);
            await uow.SaveChangesAsync();
        }
        else
        {
            if (r.Winners == 1)
            {

                var users = reacts.Where(x => !x.IsBot).Select(x => guild.GetUser(x.Id)).ToList();
                if (r.RestrictTo is not null)
                {

                    var parsedreqs = new List<ulong>();
                    var split = r.RestrictTo.Split(" ");
                    foreach (var i in split)
                    {
                        if (ulong.TryParse(i, out var parsed))
                        {
                            parsedreqs.Add(parsed);
                        }
                    }

                    try
                    {
                        if (parsedreqs.Any())
                            users = users.Where(x => x.Roles.Select(x => x.Id).Intersect(parsedreqs).Count() == parsedreqs.Count)
                                         .ToList();
                    }
                    catch
                    {
                        return;
                    }
                }

                if (!users.Any())
                {
                    var eb1 = new EmbedBuilder().WithErrorColor()
                                                .WithDescription(
                                                    "Looks like nobody that actually met the role requirements joined..")
                                                .Build();
                    await ch.ModifyAsync(x => x.Embed = eb1);
                }

                var rand = new Random();
                var index = rand.Next(users.Count);
                var user = users.ToList()[index];
                var eb = new EmbedBuilder
                {
                    Color = Mewdeko.OkColor,
                    Description = $"{user.Mention} won the giveaway for {r.Item}!"
                };
                await ch.ModifyAsync(x => x.Embed = eb.Build());
                await ch.Channel.SendMessageAsync($"{user.Mention} won the giveaway for {r.Item}!",
                    embed: new EmbedBuilder().WithOkColor().WithDescription($"[Jump To Giveaway]({ch.GetJumpUrl()})")
                                             .Build());
                r.Ended = 1;
                uow.Giveaways.Update(r);
                await uow.SaveChangesAsync();
            }
            else
            {
                var rand = new Random();
                var users = reacts.Where(x => !x.IsBot).Select(x => guild.GetUser(x.Id)).ToList();
                if (r.RestrictTo is not null)
                {

                    var parsedreqs = new List<ulong>();
                    var split = r.RestrictTo.Split(" ");
                    foreach (var i in split)
                    {
                        if (ulong.TryParse(i, out var parsed))
                        {
                            parsedreqs.Add(parsed);
                        }
                    }

                    try
                    {
                        if (parsedreqs.Any())
                            users = users.Where(x => x.Roles.Select(x => x.Id).Intersect(parsedreqs).Count() == parsedreqs.Count)
                                         .ToList();
                    }
                    catch
                    {
                        return;
                    }
                }

                if (!users.Any())
                {
                    var eb1 = new EmbedBuilder().WithErrorColor()
                                                .WithDescription(
                                                    "Looks like nobody that actually met the role requirements joined..")
                                                .Build();
                    await ch.ModifyAsync(x => x.Embed = eb1);
                }

                var winners = users.ToList().OrderBy(_ => rand.Next()).Take(r.Winners);
                var eb = new EmbedBuilder
                {
                    Color = Mewdeko.OkColor,
                    Description =
                        $"{string.Join("", winners.Select(x => x.Mention))} won the giveaway for {r.Item}!"
                };
                await ch.ModifyAsync(x => x.Embed = eb.Build());
                foreach (var winners2 in winners.Chunk(50))
                {
                    await ch.Channel.SendMessageAsync(
                        $"{string.Join("", winners2.Select(x => x.Mention))} won the giveaway for {r.Item}!",
                        embed: new EmbedBuilder().WithOkColor().WithDescription($"[Jump To Giveaway]({ch.GetJumpUrl()})")
                                                 .Build());
                }
                r.Ended = 1;
                uow.Giveaways.Update(r);
                await uow.SaveChangesAsync();
            }
        }
    }
}