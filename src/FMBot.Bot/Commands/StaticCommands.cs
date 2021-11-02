using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Fergun.Interactive;
using Fergun.Interactive.Selection;
using FMBot.Bot.Attributes;
using FMBot.Bot.Extensions;
using FMBot.Bot.Interfaces;
using FMBot.Bot.Resources;
using FMBot.Bot.Services;
using FMBot.Bot.Services.Guild;
using FMBot.Domain;
using FMBot.Domain.Models;
using Microsoft.Extensions.Options;

namespace FMBot.Bot.Commands
{
    [Name("Static commands")]
    public class StaticCommands : BaseCommandModule
    {
        private readonly CommandService _service;
        private readonly FriendsService _friendService;
        private readonly GuildService _guildService;
        private readonly IPrefixService _prefixService;
        private readonly SupporterService _supporterService;
        private readonly UserService _userService;
        private InteractiveService Interactivity { get; }

        private static readonly List<DateTimeOffset> StackCooldownTimer = new();
        private static readonly List<SocketUser> StackCooldownTarget = new();

        public StaticCommands(
                CommandService service,
                FriendsService friendsService,
                GuildService guildService,
                IPrefixService prefixService,
                SupporterService supporterService,
                UserService userService,
                IOptions<BotSettings> botSettings,
                InteractiveService interactivity) : base(botSettings)
        {
            this._friendService = friendsService;
            this._guildService = guildService;
            this._prefixService = prefixService;
            this._service = service;
            this._supporterService = supporterService;
            this._userService = userService;
            this.Interactivity = interactivity;
        }

        [Command("invite", RunMode = RunMode.Async)]
        [Summary("Info for inviting the bot to a server")]
        [Alias("server", "info")]
        public async Task InviteAsync()
        {
            var socketCommandContext = (SocketCommandContext)this.Context;
            var selfId = socketCommandContext.Client.CurrentUser.Id.ToString();
            var embedDescription = new StringBuilder();

            embedDescription.AppendLine("- You can invite .fmbot to your own server by **[clicking here](" +
                "https://discord.com/oauth2/authorize?" +
                $"client_id={selfId}" +
                "&scope=bot%20applications.commands" +
                $"&permissions={Constants.InviteLinkPermissions}).**");

            embedDescription.AppendLine(
                "- Join the [.fmbot server](http://server.fmbot.xyz/) for support and updates.");

            embedDescription.AppendLine(
                "- Help us cover hosting and other costs on our [OpenCollective](https://opencollective.com/fmbot)");

            embedDescription.AppendLine(
                "- Check our [website](https://fmbot.xyz/) for more information.");

            this._embed.WithDescription(embedDescription.ToString());

            if (IsBotSelfHosted(socketCommandContext.Client.CurrentUser.Id))
            {
                this._embed.AddField("Note:",
                    "This instance of .fmbot is self-hosted and could differ from the 'official' .fmbot. " +
                    "The invite link linked here invites the self-hosted instance and not the official .fmbot.");
            }

            await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
            this.Context.LogCommandUsed();
        }

        [Command("source", RunMode = RunMode.Async)]
        [Summary("Info for inviting the bot to a server")]
        [Alias("github", "gitlab", "opensource", "sourcecode", "code")]
        public async Task SourceAsync()
        {
            var embedDescription = new StringBuilder();

            embedDescription.AppendLine(".fmbot is open-source, non-profit and maintained by volunteers.");
            embedDescription.AppendLine("The bot is written in C#, uses .NET 5 and Discord.Net.");

            this._embed.WithDescription(embedDescription.ToString());

            this._embed.AddField("Links",
                "[Main repository](https://github.com/fmbot-discord/fmbot/)\n" +
                "[Docs repository](https://github.com/fmbot-discord/docs)\n" +
                "[File an issue](https://github.com/fmbot-discord/fmbot/issues/new/choose)\n" +
                "[Development instructions](https://fmbot.xyz/setup.html)\n" +
                "[OpenCollective](https://opencollective.com/fmbot)");

            await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
            this.Context.LogCommandUsed();
        }

        [Command("donate", RunMode = RunMode.Async)]
        [Summary("Please donate if you like this bot!")]
        [Alias("support", "patreon", "opencollective", "donations", "support")]
        public async Task DonateAsync()
        {
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id);

            var embedDescription = new StringBuilder();

            embedDescription.AppendLine(".fmbot is non-commercial and is hosted and maintained by volunteers.");
            embedDescription.AppendLine("You can help us cover hosting and other costs on our [OpenCollective](https://opencollective.com/fmbot).");
            embedDescription.AppendLine();
            embedDescription.AppendLine("We use OpenCollective so we can be transparent about our expenses. If you decide to sponsor us, you can see exactly where your money goes.");
            embedDescription.AppendLine();
            embedDescription.AppendLine($"Use `{prfx}supporters` to see everyone who has supported us so far!");
            embedDescription.AppendLine();
            embedDescription.AppendLine("**.fmbot supporter advantages include**:\n" +
                                        "- An emote behind their name (⭐)\n" +
                                        "- Their name added to the list of supporters\n" +
                                        "- A chance of sponsoring a chart\n" +
                                        "- Friend limit increased to 15 (up from 12)\n" +
                                        "- WhoKnows tracking increased to all your music (instead of top 4/5/6k artist/albums/tracks)");

            var socketCommandContext = (SocketCommandContext)this.Context;
            if (IsBotSelfHosted(socketCommandContext.Client.CurrentUser.Id))
            {
                this._embed.AddField("Note:",
                    "This instance of .fmbot is self-hosted and could differ from the 'official' .fmbot. Any supporter advantages will not apply on this bot.");
            }

            this._embed.WithDescription(embedDescription.ToString());

            var components = new ComponentBuilder().WithButton("Get .fmbot supporter", style: ButtonStyle.Link, url: "https://opencollective.com/fmbot/contribute");

            await this.Context.Channel.SendMessageAsync("", false, this._embed.Build(), component: components.Build());
            this.Context.LogCommandUsed();
        }

        [Command("status", RunMode = RunMode.Async)]
        [Summary("Displays bot stats.")]
        public async Task StatusAsync()
        {
            var socketCommandContext = (SocketCommandContext)this.Context;
            var selfUser = socketCommandContext.Client.CurrentUser;

            this._embedAuthor.WithIconUrl(selfUser.GetAvatarUrl());
            this._embedAuthor.WithName(selfUser.Username);
            this._embedAuthor.WithUrl("https://fmbot.xyz/");

            this._embed.WithAuthor(this._embedAuthor);

            var currentProcess = Process.GetCurrentProcess();

            var startTime = DateTime.Now - currentProcess.StartTime;
            var currentMemoryUsage = currentProcess.WorkingSet64;
            var peakMemoryUsage = currentProcess.PeakWorkingSet64;

            var client = this.Context.Client as DiscordShardedClient;

            var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();

            var ticks = Stopwatch.GetTimestamp();
            var upTime = (double)ticks / Stopwatch.Frequency;
            var upTimeInSeconds = TimeSpan.FromSeconds(upTime);

            var description = "";
            description += $"**Bot Uptime:** `{startTime.ToReadableString()}`\n";
            description += $"**Server Uptime:** `{upTimeInSeconds.ToReadableString()}`\n";
            description += $"**Usercount:** `{await this._userService.GetTotalUserCountAsync()}`  (Authorized: `{await this._userService.GetTotalAuthorizedUserCountAsync()}` | Discord: `{client.Guilds.Select(s => s.MemberCount).Sum()}`)\n";
            description += $"**Friendcount:** `{await this._friendService.GetTotalFriendCountAsync()}`\n";
            description += $"**Servercount:** `{client.Guilds.Count}`  (Shards: `{client.Shards.Count}` (`{client.GetShardIdFor(this.Context.Guild)}`))\n";
            description += $"**Commands used:** `{Statistics.CommandsExecuted.Value}`\n";
            description += $"**Last.fm API calls:** `{Statistics.LastfmApiCalls.Value}`  (Ex. authorized: `{Statistics.LastfmAuthorizedApiCalls.Value}`)\n";
            description += $"**MusicBrainz API calls:** `{Statistics.MusicBrainzApiCalls.Value}`\n";
            description += $"**Botscrobbles:** `{Statistics.LastfmScrobbles.Value}`  (Now playing updates: `{Statistics.LastfmNowPlayingUpdates.Value}`)\n";
            description += $"**Memory usage:** `{currentMemoryUsage.ToFormattedByteString()}`  (Peak: `{peakMemoryUsage.ToFormattedByteString()}`)\n";
            description += $"**Average shard latency:** `{Math.Round(client.Shards.Select(s => s.Latency).Average(), 2) + "ms`"}\n";
            //description += $"**Bot version:** `{assemblyVersion}`\n";
            //description += $"**Self-hosted:** `{IsBotSelfHosted(this.Context.Client.CurrentUser.Id).ToString()}`\n";

            this._embed.WithDescription(description);

            await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
            this.Context.LogCommandUsed();
        }

        [Command("shards", RunMode = RunMode.Async)]
        [Summary("Displays bot sharding info.")]
        [GuildOnly]
        [ExcludeFromHelp]
        public async Task ShardsAsync()
        {
            this._embed.WithTitle("Bot instance shards");

            var client = this.Context.Client as DiscordShardedClient;

            var onlineShards = new StringBuilder();
            foreach (var shard in client.Shards.Where(w => w.Latency > 0))
            {
                onlineShards.Append($"`{shard.ShardId}` - `{shard.Latency}ms`, ");
            }
            this._embed.AddField("Online shards", onlineShards.Length > 0 ? onlineShards.ToString() : "No online shards");

            var offlineShards = new StringBuilder();
            foreach (var shard in client.Shards.Where(w => w.Latency == 0))
            {
                offlineShards.Append($"`{shard.ShardId}` - `{shard.Latency}ms`, ");
            }
            this._embed.AddField("Offline shards", offlineShards.Length > 0 ? offlineShards.ToString() : "No offline shards");

            this._embed.WithFooter(
                $"Guild {this.Context.Guild.Name} | {this.Context.Guild.Id} is on shard {client.GetShardIdFor(this.Context.Guild)}");

            await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
            this.Context.LogCommandUsed();
        }

        [Command("shard", RunMode = RunMode.Async)]
        [Summary("Displays shard info for a specific guild")]
        [GuildOnly]
        [ExcludeFromHelp]
        [Alias("shardinfo")]
        public async Task ShardInfoAsync(ulong? guildId = null)
        {
            if (!guildId.HasValue)
            {
                await this.Context.Channel.SendMessageAsync($"Enter a server id please (this server is `{this.Context.Guild.Id}`)");
                this.Context.LogCommandUsed(CommandResponse.WrongInput);
                return;
            }

            var client = this.Context.Client as DiscordShardedClient;

            var guild = client.GetGuild(guildId.Value);

            if (guild != null)
            {
                var shard = client.GetShardFor(guild);


                this._embed.WithDescription($"Guild `{guildId}` is on the following shard:\n\n" +
                                            $"Shard id: `{shard.ShardId}`\n" +
                                            $"Latency: `{shard.Latency}ms`\n" +
                                            $"Guilds: `{shard.Guilds.Count}`\n" +
                                            $"Connection state: `{shard.ConnectionState}`");

                this._embed.WithFooter($"{guild.Name} - {guild.MemberCount} members");
            }
            else
            {
                await this.Context.Channel.SendMessageAsync("Server could not be found.");
                this.Context.LogCommandUsed(CommandResponse.NotFound);
                return;
            }

            await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
            this.Context.LogCommandUsed();
        }

        [Command("help", RunMode = RunMode.Async)]
        [Summary("Quick help summary to get started.")]
        [Alias("bot")]
        public async Task HelpAsync([Remainder] string extraValues = null)
        {
            var customPrefix = true;
            var prefix = this._prefixService.GetPrefix(this.Context.Guild?.Id);
            if (prefix == this._botSettings.Bot.Prefix)
            {
                customPrefix = false;
            }

            if (!string.IsNullOrWhiteSpace(extraValues))
            {
                if (extraValues.Length > prefix.Length && extraValues.Contains(prefix))
                {
                    extraValues = extraValues.Replace(prefix, "");
                }

                var searchResult = this._service.Search(extraValues);
                if (searchResult.IsSuccess && searchResult.Commands != null && searchResult.Commands.Any())
                {
                    var userName = (this.Context.Message.Author as SocketGuildUser)?.Nickname ?? this.Context.User.Username;
                    this._embed.HelpResponse(searchResult.Commands[0].Command, prefix, userName);
                    await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());
                    this.Context.LogCommandUsed(CommandResponse.Help);
                    return;
                }
            }

            this._embed.WithTitle(".fmbot Quick Start Guide");

            this._embed.AddField($"Main command `{prefix}fm`",
                "Displays last scrobbles, and looks different depending on the mode you've set.");

            var contextUser = await this._userService.GetUserSettingsAsync(this.Context.User);
            if (contextUser == null)
            {
                this._embed.AddField($"Connecting your Last.fm account: `{prefix}login`",
                    $"Not receiving a DM from .fmbot when logging in? Please check if you have DMs enabled in this servers privacy settings.");
            }
            else
            {
                this._embed.AddField("Customizing your .fm",
                    $"For changing how your .fm command looks, use `{prefix}mode`.");

                this._embed.WithFooter($"Logged in to .fmbot with the Last.fm account '{contextUser.UserNameLastFM}'");
            }

            this._embed.AddField("Command information",
                $"To view information about specific commands add `help` after the command.\n" +
                $"Some examples are: `{prefix}chart help` and `{prefix}whoknows help`.");

            if (customPrefix)
            {
                this._embed.AddField("Custom prefix:",
                    $"This server has the `{prefix}` prefix.\n" +
                    $"Some examples of commands with this prefix are `{prefix}whoknows`, `{prefix}chart` and `{prefix}artisttracks`.");
            }

            this._embed.WithFooter($"To view a complete list of all commands, use '{prefix}fullhelp'.");

            var socketCommandContext = (SocketCommandContext)this.Context;
            if (IsBotSelfHosted(socketCommandContext.Client.CurrentUser.Id))
            {
                this._embed.AddField("Note:",
                    "This instance of .fmbot is self-hosted and could differ from the 'official' .fmbot. \n" +
                    "Keep in mind that the instance might not be fully up to date or other users might not be registered.");
            }

            if (PublicProperties.IssuesAtLastFm)
            {
                this._embed.AddField("Note:", "⚠️ [Last.fm](https://twitter.com/lastfmstatus) is currently experiencing issues.\n" +
                                              ".fmbot is not affiliated with Last.fm.");
            }

            var components = new ComponentBuilder().WithButton("All commands", style: ButtonStyle.Link, url: "https://fmbot.xyz/commands/");

            await this.Context.Channel.SendMessageAsync("", false, this._embed.Build(), component: components.Build());
            this.Context.LogCommandUsed();
        }

        [Command("menu", RunMode = RunMode.Async)]
        public async Task MenuAsync()
        {
            // Create CancellationTokenSource that will be canceled after 10 minutes.
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            var options = new[]
            {
                "Cache messages",
                "Cache users",
                "Allow using mentions as prefix",
                "Ignore command errors"
            };

            var values = new[]
            {
                true,
                false,
                true,
                false
            };

            // Dynamically create the number emotes
            var emotes = Enumerable.Range(1, options.Length)
                .ToDictionary(x => new Emoji($"{x}\ufe0f\u20e3") as IEmote, y => y);

            // Add the cancel emote at the end of the dictionary
            emotes.Add(new Emoji("❌"), -1);


            // If we can use interactions, prefer disabling the input (buttons, select menus) instead of removing them from the message.

            InteractiveMessageResult<KeyValuePair<IEmote, int>> result = null;
            IUserMessage message = null;

            while (result is null || result.Status == InteractiveStatus.Success)
            {
                var pageBuilder = new PageBuilder()
                    .WithTitle("Bot Control Panel")
                    .WithDescription("Use the reactions/buttons to enable or disable an option.")
                    .AddField("Option", string.Join('\n', options.Select((x, i) => $"**{i + 1}**. {x}")), true)
                    .AddField("Value", string.Join('\n', values), true);

                var selection = new EmoteSelectionBuilder<int>()
                    .AddUser(Context.User)
                    .WithSelectionPage(pageBuilder)
                    .WithOptions(emotes)
                    .WithAllowCancel(true)
                    .WithActionOnTimeout(ActionOnStop.DisableInput)
                    .Build();

                // if message is null, SendSelectionAsync() will send a message, otherwise it will modify the message.
                // The cancellation token persists here, so it will be canceled after 10 minutes no matter how many times the selection is used.
                result = await this.Interactivity.SendSelectionAsync(selection, Context.Channel, TimeSpan.FromMinutes(10), message, cancellationToken: cts.Token);

                // Store the used message.
                message = result.Message;

                // Break the loop if the result isn't successful
                if (!result.IsSuccess) break;

                int selected = result.Value.Value;

                // Invert the value of the selected option
                values[selected - 1] = !values[selected - 1];

                // Do stuff with the selected option
            }
        }

        [Command("multiselecthelp", RunMode = RunMode.Async)]
        public async Task MultiSelect()
        {
            try
            {
                IUserMessage message = null;
                InteractiveMessageResult<MultiSelectionOption<string>> selectedResult = null;

                var options = new List<MultiSelectionOption<string>>();
                foreach (var module in this._service.Modules
                             .OrderByDescending(o => o.Commands.Count(w => !w.Attributes.OfType<ExcludeFromHelp>().Any() &&
                                                                           !w.Attributes.OfType<ServerStaffOnly>().Any()))
                             .Where(w =>
                                 !w.Attributes.OfType<ExcludeFromHelp>().Any() &&
                                 !w.Attributes.OfType<ServerStaffOnly>().Any()))
                {
                    options.Add(new MultiSelectionOption<string>(module.Name, 1));
                }

                while (selectedResult is null || selectedResult.Status == InteractiveStatus.Success)
                {
                    var commands = "";
                    var selectedModule = selectedResult?.Value.Option;

                    options = options.Where(w => w.Row == 1).ToList();
                    if (selectedModule != null)
                    {
                        var selectedModuleResult = this._service.Modules.FirstOrDefault(f => f.Name == selectedModule);
                        if (selectedModuleResult != null)
                        {
                            foreach (var cmd in selectedModuleResult.Commands.Where(w =>
                                         !w.Attributes.OfType<ExcludeFromHelp>().Any()))
                            {
                                var result = await cmd.CheckPreconditionsAsync(this.Context);
                                if (result.IsSuccess)
                                {
                                    options.Add(new MultiSelectionOption<string>(cmd.Name, 2));
                                    commands += $"{cmd.Name}, ";
                                }
                            }
                        }
                    }

                    var multiSelection = new MultiSelectionBuilder<string>()
                        .WithOptions(options)
                        .WithSelectionPage(new PageBuilder()
                            .WithText(commands))
                        .Build();

                    selectedResult =
                        await this.Interactivity.SendSelectionAsync(multiSelection, this.Context.Channel, message: message);
                    message = selectedResult.Message;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        // Builder for multi selection
        public class MultiSelectionBuilder<T> : BaseSelectionBuilder<MultiSelection<T>, MultiSelectionOption<T>, MultiSelectionBuilder<T>>
        {
            public override InputType InputType => InputType.SelectMenus;

            public override MultiSelection<T> Build() => new(EmoteConverter, StringConverter,
            EqualityComparer, AllowCancel, SelectionPage?.Build(), Users?.ToArray(), Options?.ToArray(),
            CanceledPage?.Build(), TimeoutPage?.Build(), SuccessPage?.Build(), Deletion, InputType,
            ActionOnCancellation, ActionOnTimeout, ActionOnSuccess);
        }

        // A selection that uses multiple select menus
        public class MultiSelection<T> : BaseSelection<MultiSelectionOption<T>>
        {
            // Selections have a bunch of properties
            public MultiSelection(Func<MultiSelectionOption<T>, IEmote> emoteConverter, Func<MultiSelectionOption<T>, string> stringConverter,
                IEqualityComparer<MultiSelectionOption<T>> equalityComparer, bool allowCancel, Page selectionPage, IReadOnlyCollection<IUser> users,
                IReadOnlyCollection<MultiSelectionOption<T>> options, Page canceledPage, Page timeoutPage, Page successPage, DeletionOptions deletion,
                InputType inputType, ActionOnStop actionOnCancellation, ActionOnStop actionOnTimeout, ActionOnStop actionOnSuccess)
                : base(emoteConverter, stringConverter, equalityComparer, allowCancel, selectionPage, users, options, canceledPage,
                      timeoutPage, successPage, deletion, inputType, actionOnCancellation, actionOnTimeout, actionOnSuccess)
            {
            }

            public override MessageComponent BuildComponents(bool disableAll)
            {
                var builder = new ComponentBuilder();
                var selectMenus = new Dictionary<int, SelectMenuBuilder>();

                foreach (var option in Options)
                {
                    if (!selectMenus.ContainsKey(option.Row))
                    {
                        selectMenus[option.Row] = new SelectMenuBuilder()
                            .WithCustomId($"selectmenu{option.Row}")
                            .WithDisabled(disableAll);
                    }

                    var emote = EmoteConverter?.Invoke(option);
                    string label = StringConverter?.Invoke(option);
                    if (emote is null && label is null)
                    {
                        throw new InvalidOperationException($"Neither {nameof(EmoteConverter)} nor {nameof(StringConverter)} returned a valid emote or string.");
                    }

                    var optionBuilder = new SelectMenuOptionBuilder()
                        .WithLabel(label)
                        .WithEmote(emote)
                        .WithValue(emote?.ToString() ?? label);

                    selectMenus[option.Row].AddOption(optionBuilder);
                }

                foreach ((int row, var selectMenu) in selectMenus)
                {
                    builder.WithSelectMenu(selectMenu, row);
                }

                return builder.Build();
            }
        }

        public readonly struct MultiSelectionOption<T>
        {
            public MultiSelectionOption(T option, int row)
            {
                Option = option;
                Row = row;
            }

            public T Option { get; }

            public int Row { get; }

            public override string ToString() => Option.ToString();

            public override int GetHashCode() => Option.GetHashCode();

            public override bool Equals(object obj) => Equals(Option, obj);
        }

        [Command("supporters", RunMode = RunMode.Async)]
        [Summary("Displays all .fmbot supporters.")]
        [Alias("donators", "donors", "backers")]
        public async Task AllSupportersAsync()
        {
            var prefix = this._prefixService.GetPrefix(this.Context.Guild?.Id);

            var supporters = await this._supporterService.GetAllVisibleSupporters();

            var supporterLists = supporters.ChunkBy(10);

            var description = new StringBuilder();
            description.AppendLine("Thank you to all our supporters that help keep .fmbot running. If you would like to be on this list too, please check out our [OpenCollective](https://opencollective.com/fmbot). \n" +
                                   $"For all information on donating to .fmbot you can check out `{prefix}donate`.");
            description.AppendLine();

            var pages = new List<PageBuilder>();
            foreach (var supporterList in supporterLists)
            {
                var supporterString = new StringBuilder();
                supporterString.Append(description.ToString());

                foreach (var supporter in supporterList)
                {
                    var type = supporter.SupporterType switch
                    {
                        SupporterType.Guild => " (server)",
                        SupporterType.User => "",
                        SupporterType.Company => " (business)",
                        _ => ""
                    };

                    supporterString.AppendLine($" - **{supporter.Name}** {type}");
                }

                pages.Add(new PageBuilder()
                    .WithDescription(supporterString.ToString())
                    .WithAuthor(this._embedAuthor)
                    .WithTitle(".fmbot supporters overview"));
            }

            this._embed.WithDescription(description.ToString());

            var paginator = StringService.BuildStaticPaginator(pages);

            _ = this.Interactivity.SendPaginatorAsync(
                paginator,
                this.Context.Channel,
                TimeSpan.FromMinutes(DiscordConstants.PaginationTimeoutInSeconds));

            this.Context.LogCommandUsed();
        }

        [Command("countdown", RunMode = RunMode.Async)]
        [Summary("Counts down. Doesn't work that well above 3 seconds.")]
        [ExcludeFromHelp]
        public async Task CountdownAsync(int countdown = 3)
        {
            if (this._guildService.CheckIfDM(this.Context))
            {
                await ReplyAsync("Command is not supported in DMs.");
                this.Context.LogCommandUsed(CommandResponse.NotSupportedInDm);
                return;
            }

            if (countdown > 5)
            {
                countdown = 5;
            }

            if (countdown < 1)
            {
                countdown = 1;
            }

            var msg = this.Context.Message as SocketUserMessage;
            if (StackCooldownTarget.Contains(this.Context.Message.Author))
            {
                if (StackCooldownTimer[StackCooldownTarget.IndexOf(msg.Author)].AddSeconds(countdown + 20) >= DateTimeOffset.Now)
                {
                    var secondsLeft = (int)(StackCooldownTimer[
                            StackCooldownTarget.IndexOf(this.Context.Message.Author as SocketGuildUser)]
                        .AddSeconds(countdown + 30) - DateTimeOffset.Now).TotalSeconds;
                    if (secondsLeft <= 20)
                    {
                        var secondString = secondsLeft == 1 ? "second" : "seconds";
                        await ReplyAsync($"Please wait {secondsLeft} {secondString} before starting another countdown.");
                        this.Context.LogCommandUsed(CommandResponse.Cooldown);
                    }

                    return;
                }

                StackCooldownTimer[StackCooldownTarget.IndexOf(msg.Author)] = DateTimeOffset.Now;
            }
            else
            {
                StackCooldownTarget.Add(msg.Author);
                StackCooldownTimer.Add(DateTimeOffset.Now);
            }

            await ReplyAsync($"Countdown for `{countdown}` seconds starting!");
            await Task.Delay(4000);

            for (var i = countdown; i > 0; i--)
            {
                _ = ReplyAsync(i.ToString());
                await Task.Delay(1000);
            }

            await ReplyAsync("Go!");
            this.Context.LogCommandUsed();
        }

        [Command("fullhelp", RunMode = RunMode.Async)]
        [Summary("Displays all available commands.")]
        public async Task FullHelpAsync()
        {
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id);

            this._embed.WithDescription("**See a list of all available commands below.**\n" +
                                        $"Use `{prfx}serverhelp` to view all your configurable server settings.");

            foreach (var module in this._service.Modules
                .OrderByDescending(o => o.Commands.Count(w => !w.Attributes.OfType<ExcludeFromHelp>().Any() &&
                                                              !w.Attributes.OfType<ServerStaffOnly>().Any()))
                .Where(w =>
                !w.Attributes.OfType<ExcludeFromHelp>().Any() &&
                !w.Attributes.OfType<ServerStaffOnly>().Any()))
            {
                var moduleCommands = "";
                foreach (var cmd in module.Commands.Where(w =>
                    !w.Attributes.OfType<ExcludeFromHelp>().Any()))
                {
                    var result = await cmd.CheckPreconditionsAsync(this.Context);
                    if (result.IsSuccess)
                    {
                        if (!string.IsNullOrEmpty(moduleCommands))
                        {
                            moduleCommands += ", ";
                        }

                        var name = $"`{prfx}{cmd.Name}`";
                        name = name.Replace("fmfm", "fm");

                        moduleCommands += name;
                    }
                }

                var moduleSummary = string.IsNullOrEmpty(module.Summary) ? "" : $" - {module.Summary}";

                if (!string.IsNullOrEmpty(module.Name) && !string.IsNullOrEmpty(moduleCommands))
                {
                    this._embed.AddField(
                        module.Name + moduleSummary,
                        moduleCommands,
                        true);
                }

            }

            this._embed.WithFooter($"Add 'help' after a command to get more info. For example: '{prfx}chart help'");
            await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());

            this.Context.LogCommandUsed();
        }

        [Command("settinghelp", RunMode = RunMode.Async)]
        [Summary("Displays a list of all server settings.")]
        [Alias("serverhelp", "serversettings", "settings", "help server")]
        public async Task ServerHelpAsync()
        {
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id);

            this._embed.WithDescription("**See all server settings below.**\n" +
            "These commands require either the `Admin` or the `Ban Members` permission.");

            foreach (var module in this._service.Modules
                .OrderByDescending(o => o.Commands.Count)
                .Where(w =>
                !w.Attributes.OfType<ExcludeFromHelp>().Any() &&
                w.Attributes.OfType<ServerStaffOnly>().Any()))
            {
                var moduleCommands = "";
                foreach (var cmd in module.Commands)
                {
                    var result = await cmd.CheckPreconditionsAsync(this.Context);
                    if (result.IsSuccess)
                    {
                        if (!string.IsNullOrEmpty(moduleCommands))
                        {
                            moduleCommands += ", ";
                        }

                        moduleCommands += $"`{prfx}{cmd.Name}`";
                    }
                }

                var moduleSummary = string.IsNullOrEmpty(module.Summary) ? "" : $" - {module.Summary}";

                if (!string.IsNullOrEmpty(module.Name) && !string.IsNullOrEmpty(moduleCommands))
                {
                    this._embed.AddField(
                        module.Name + moduleSummary,
                        moduleCommands,
                        true);
                }

            }

            this._embed.WithFooter($"Add 'help' after a command to get more info. For example: '{prfx}prefix help'");
            await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());

            this.Context.LogCommandUsed();
        }

        [Command("staffhelp", RunMode = RunMode.Async)]
        [Summary("Displays this list.")]
        [ExcludeFromHelp]
        public async Task StaffHelpAsync()
        {
            var prfx = this._prefixService.GetPrefix(this.Context.Guild?.Id);

            this._embed.WithDescription("**See all .fmbot staff commands below.**\n" +
            "These commands require .fmbot admin or owner.");

            foreach (var module in this._service.Modules
                .OrderByDescending(o => o.Commands.Count)
                .Where(w =>
                w.Attributes.OfType<ExcludeFromHelp>().Any()))
            {
                var moduleCommands = "";
                foreach (var cmd in module.Commands)
                {
                    var result = await cmd.CheckPreconditionsAsync(this.Context);
                    if (result.IsSuccess)
                    {
                        if (!string.IsNullOrEmpty(moduleCommands))
                        {
                            moduleCommands += ", ";
                        }

                        moduleCommands += $"`{prfx}{cmd.Name}`";
                    }
                }

                var moduleSummary = string.IsNullOrEmpty(module.Summary) ? "" : $" - {module.Summary}";

                if (!string.IsNullOrEmpty(module.Name) && !string.IsNullOrEmpty(moduleCommands))
                {
                    this._embed.AddField(
                        module.Name + moduleSummary,
                        moduleCommands,
                        true);
                }

            }

            await this.Context.Channel.SendMessageAsync("", false, this._embed.Build());

            this.Context.LogCommandUsed();
        }

        private static bool IsBotSelfHosted(ulong botId)
        {
            return !botId.Equals(Constants.BotProductionId) && !botId.Equals(Constants.BotDevelopId);
        }
    }
}
