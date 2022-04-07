﻿using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Fergun.Interactive;
using Fergun.Interactive.Pagination;
using Mewdeko.Common;
using Mewdeko.Common.Attributes;
using Mewdeko.Database;
using Mewdeko.Database.Extensions;
using Mewdeko.Database.Models;
using Mewdeko.Extensions;
using Mewdeko.Modules.Searches.Services;
using Microsoft.EntityFrameworkCore;

namespace Mewdeko.Modules.Searches;

public partial class Searches
{
    [Group]
    public class StreamNotificationCommands : MewdekoSubmodule<StreamNotificationService>
    {
        private readonly DbService _db;
        private readonly InteractiveService _interactivity;

        public StreamNotificationCommands(DbService db, InteractiveService serv)
        {
            _interactivity = serv;
            _db = db;
        }

        // private static readonly Regex picartoRegex = new Regex(@"picarto.tv/(?<name>.+[^/])/?",
        //     RegexOptions.Compiled | RegexOptions.IgnoreCase);

        [Cmd, Aliases, RequireContext(ContextType.Guild), UserPerm(GuildPermission.ManageMessages)]
        public async Task StreamAdd(string link)
        {
            var data = await Service.FollowStream(ctx.Guild.Id, ctx.Channel.Id, link);
            if (data is null)
            {
                await ReplyErrorLocalizedAsync("stream_not_added").ConfigureAwait(false);
                return;
            }

            var embed = Service.GetEmbed(ctx.Guild.Id, data);
            await ctx.Channel.EmbedAsync(embed, GetText("stream_tracked")).ConfigureAwait(false);
        }

        [Cmd, Aliases, RequireContext(ContextType.Guild),
         UserPerm(GuildPermission.ManageMessages), Priority(1)]
        public async Task StreamRemove(int index)
        {
            if (--index < 0)
                return;

            var fs = await Service.UnfollowStreamAsync(ctx.Guild.Id, index);
            if (fs is null)
            {
                await ReplyErrorLocalizedAsync("stream_no").ConfigureAwait(false);
                return;
            }

            await ReplyConfirmLocalizedAsync(
                "stream_removed",
                Format.Bold(fs.Username),
                fs.Type).ConfigureAwait(false);
        }

        [Cmd, Aliases, RequireContext(ContextType.Guild), UserPerm(GuildPermission.Administrator)]
        public async Task StreamsClear()
        {
            var count = await Service.ClearAllStreams(ctx.Guild.Id);
            await ReplyConfirmLocalizedAsync("streams_cleared", count).ConfigureAwait(false);
        }

        [Cmd, Aliases, RequireContext(ContextType.Guild)]
        public async Task StreamList()
        {

            var streams = new List<FollowedStream>();
            await using (var uow = _db.GetDbContext())
            {
                var all = uow
                    .ForGuildId(ctx.Guild.Id, set => set.Include(gc => gc.FollowedStreams))
                    .FollowedStreams
                    .OrderBy(x => x.Id)
                    .ToList();

                for (var index = all.Count - 1; index >= 0; index--)
                {
                    var fs = all[index];
                    if (((SocketGuild) ctx.Guild).GetTextChannel(fs.ChannelId) is null)
                        await Service.UnfollowStreamAsync(fs.GuildId, index);
                    else
                        streams.Insert(0, fs);
                }
            }

            var paginator = new LazyPaginatorBuilder()
                .AddUser(ctx.User)
                .WithPageFactory(PageFactory)
                .WithFooter(PaginatorFooter.PageNumber | PaginatorFooter.Users)
                .WithMaxPageIndex(streams.Count / 12)
                .WithDefaultEmotes()
                .Build();

            await _interactivity.SendPaginatorAsync(paginator, Context.Channel, TimeSpan.FromMinutes(60)).ConfigureAwait(false);;

            async Task<PageBuilder> PageFactory(int page)
            {
                await Task.CompletedTask;
                var elements = streams.Skip(page * 12).Take(12)
                                      .ToList();

                    if (elements.Count == 0)
                        return new PageBuilder()
                            .WithDescription(GetText("streams_none"))
                            .WithErrorColor();

                    var eb = new PageBuilder()
                        .WithTitle(GetText("streams_follow_title"))
                        .WithOkColor();
                    for (var index = 0; index < elements.Count; index++)
                    {
                        var elem = elements[index];
                        eb.AddField(
                            $"**#{index + 1 + (12 * page)}** {elem.Username.ToLower()}",
                            $"【{elem.Type}】\n<#{elem.ChannelId}>\n{elem.Message?.TrimTo(50)}",
                            true);
                    }

                    return eb;
            }
        }

        [Cmd, Aliases, RequireContext(ContextType.Guild), UserPerm(GuildPermission.ManageMessages)]
        public async Task StreamOffline()
        {
            var newValue = Service.ToggleStreamOffline(ctx.Guild.Id);
            if (newValue)
                await ReplyConfirmLocalizedAsync("stream_off_enabled").ConfigureAwait(false);
            else
                await ReplyConfirmLocalizedAsync("stream_off_disabled").ConfigureAwait(false);
        }

        [Cmd, Aliases, RequireContext(ContextType.Guild), UserPerm(GuildPermission.ManageMessages)]
        public async Task StreamMessage(int index, [Remainder] string message)
        {
            if (--index < 0)
                return;

            if (!Service.SetStreamMessage(ctx.Guild.Id, index, message, out var fs))
            {
                await ReplyConfirmLocalizedAsync("stream_not_following").ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(message))
                await ReplyConfirmLocalizedAsync("stream_message_reset", Format.Bold(fs.Username))
                    .ConfigureAwait(false);
            else
                await ReplyConfirmLocalizedAsync("stream_message_set", Format.Bold(fs.Username))
                    .ConfigureAwait(false);
        }

        [Cmd, Aliases, RequireContext(ContextType.Guild)]
        public async Task StreamCheck(string url)
        {
            try
            {
                var data = await Service.GetStreamDataAsync(url).ConfigureAwait(false);
                if (data is null)
                {
                    await ReplyErrorLocalizedAsync("no_channel_found").ConfigureAwait(false);
                    return;
                }

                if (data.IsLive)
                    await ReplyConfirmLocalizedAsync("streamer_online",
                            Format.Bold(data.Name),
                            Format.Bold(data.Viewers.ToString()))
                        .ConfigureAwait(false);
                else
                    await ReplyConfirmLocalizedAsync("streamer_offline", data.Name)
                        .ConfigureAwait(false);
            }
            catch
            {
                await ReplyErrorLocalizedAsync("no_channel_found").ConfigureAwait(false);
            }
        }
    }
}