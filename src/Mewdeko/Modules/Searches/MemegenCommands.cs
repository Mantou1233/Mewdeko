using System.Collections.Immutable;
using System.Net.Http;
using System.Text;
using Discord.Commands;
using Fergun.Interactive;
using Fergun.Interactive.Pagination;
using Mewdeko._Extensions;
using Mewdeko.Common;
using Mewdeko.Common.Attributes;
using Newtonsoft.Json;

namespace Mewdeko.Modules.Searches;

public partial class Searches
{
    [Group]
    public class MemegenCommands : MewdekoSubmodule
    {
        private static readonly ImmutableDictionary<char, string> _map = new Dictionary<char, string>
        {
            {'?', "~q"},
            {'%', "~p"},
            {'#', "~h"},
            {'/', "~s"},
            {' ', "-"},
            {'-', "--"},
            {'_', "__"},
            {'"', "''"}
        }.ToImmutableDictionary();

        private readonly IHttpClientFactory _httpFactory;
        private readonly InteractiveService _interactivity;

        public MemegenCommands(IHttpClientFactory factory, InteractiveService serv)
        {
            _interactivity = serv;
            _httpFactory = factory;
        }

        [MewdekoCommand, Usage, Description, Aliases]
        public async Task Memelist(int page = 1)
        {
            if (--page < 0)
                return;

            using var http = _httpFactory.CreateClient("memelist");
            var res = await http.GetAsync("https://api.memegen.link/templates/")
                .ConfigureAwait(false);

            var rawJson = await res.Content.ReadAsStringAsync();

            var data = JsonConvert.DeserializeObject<List<MemegenTemplate>>(rawJson);

            var paginator = new LazyPaginatorBuilder()
                .AddUser(ctx.User)
                .WithPageFactory(PageFactory)
                .WithFooter(PaginatorFooter.PageNumber | PaginatorFooter.Users)
                .WithMaxPageIndex(data.Count / 15)
                .WithDefaultEmotes()
                .Build();

            await _interactivity.SendPaginatorAsync(paginator, Context.Channel, TimeSpan.FromMinutes(60));

            Task<PageBuilder> PageFactory(int page)
            {
                var templates = "";
                foreach (var template in data.Skip(page * 15).Take(15))
                    templates += $"**{template.Name}:**\n key: `{template.Id}`\n";
                var embed = new PageBuilder()
                    .WithOkColor()
                    .WithDescription(templates);

                return Task.FromResult(embed);
            }
        }

        [MewdekoCommand, Usage, Description, Aliases]
        public async Task Memegen(string meme, [Remainder] string memeText = null)
        {
            var memeUrl = $"http://api.memegen.link/{meme}";
            if (!string.IsNullOrWhiteSpace(memeText))
            {
                var memeTextArray = memeText.Split(';');
                foreach (var text in memeTextArray)
                {
                    var newText = Replace(text);
                    memeUrl += $"/{newText}";
                }
            }

            memeUrl += ".png";
            await ctx.Channel.SendMessageAsync(memeUrl)
                .ConfigureAwait(false);
        }

        private static string Replace(string input)
        {
            var sb = new StringBuilder();

            foreach (var c in input)
                if (_map.TryGetValue(c, out var tmp))
                    sb.Append(tmp);
                else
                    sb.Append(c);

            return sb.ToString();
        }

        private class MemegenTemplate
        {
            public string Name { get; set; }
            public string Id { get; set; }
        }
    }
}