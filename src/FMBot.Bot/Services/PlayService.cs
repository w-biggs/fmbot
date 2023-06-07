using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Dapper;
using Discord;
using Discord.Commands;
using FMBot.Bot.Extensions;
using FMBot.Bot.Models;
using FMBot.Domain;
using FMBot.Domain.Models;
using FMBot.LastFM.Domain.Types;
using FMBot.LastFM.Extensions;
using FMBot.LastFM.Repositories;
using FMBot.Persistence.Domain.Models;
using FMBot.Persistence.EntityFrameWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;
using Genius.Models.User;

namespace FMBot.Bot.Services;

public class PlayService
{
    private readonly IDbContextFactory<FMBotDbContext> _contextFactory;
    private readonly GenreService _genreService;
    private readonly TimeService _timeService;
    private readonly BotSettings _botSettings;
    private readonly LastFmRepository _lastFmRepository;
    private readonly IMemoryCache _cache;

    public PlayService(IDbContextFactory<FMBotDbContext> contextFactory, GenreService genreService, TimeService timeService, IOptions<BotSettings> botSettings, LastFmRepository lastFmRepository, IMemoryCache cache)
    {
        this._contextFactory = contextFactory;
        this._genreService = genreService;
        this._timeService = timeService;
        this._lastFmRepository = lastFmRepository;
        this._cache = cache;
        this._botSettings = botSettings.Value;
    }

    public async Task<DailyOverview> GetDailyOverview(int userId, int amountOfDays)
    {
        try
        {
            await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
            await connection.OpenAsync();

            var start = DateTime.UtcNow.AddDays(-amountOfDays);
            var plays = await PlayRepository.GetUserPlaysWithinTimeRange(userId, connection, start);

            if (!plays.Any())
            {
                return null;
            }

            var overview = new DailyOverview
            {
                Days = plays
                    .OrderByDescending(o => o.TimePlayed)
                    .GroupBy(g => g.TimePlayed.Date.AddHours(5))
                    .Select(s => new DayOverview
                    {
                        Date = s.Key,
                        Playcount = s.Count(),
                        Plays = s.ToList(),
                        TopTrack = GetTopTrackForPlays(s.ToList()),
                        TopAlbum = GetTopAlbumForPlays(s.ToList()),
                        TopArtist = GetTopArtistForPlays(s.ToList()),
                    }).Take(amountOfDays).ToList(),
                Playcount = plays.Count,
                Uniques = GetUniqueCount(plays.ToList()),
                AvgPerDay = GetAvgPerDayCount(plays.ToList()),
            };

            foreach (var day in overview.Days.Where(w => w.Plays.Any()))
            {
                day.TopGenres = await this._genreService.GetTopGenresForPlays(day.Plays);
            }
            foreach (var day in overview.Days.Where(w => w.Plays.Any()))
            {
                day.ListeningTime = await this._timeService.GetPlayTimeForPlays(day.Plays);
            }

            return overview;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    public async Task<YearOverview> GetYear(int userId, int year)
    {
        await using var db = await this._contextFactory.CreateDbContextAsync();
        var user = await db.Users
            .AsNoTracking()
            .FirstAsync(f => f.UserId == userId);

        var cacheKey = $"year-ov-{user.UserId}-{year}";

        var cachedYearAvailable = this._cache.TryGetValue(cacheKey, out YearOverview cachedYearOverview);
        if (cachedYearAvailable)
        {
            return cachedYearOverview;
        }

        var startDateTime = new DateTime(year, 01, 01);
        var endDateTime = startDateTime.AddYears(1).AddSeconds(-1);

        var yearOverview = new YearOverview
        {
            Year = year,
            LastfmErrors = false
        };

        var currentTopTracks =
            await this._lastFmRepository.GetTopTracksForCustomTimePeriodAsyncAsync(user.UserNameLastFM, startDateTime, endDateTime, 500);

        if (!currentTopTracks.Success)
        {
            yearOverview.LastfmErrors = true;
            return yearOverview;
        }

        yearOverview.TopTracks = currentTopTracks.Content;

        var currentTopAlbums =
            await this._lastFmRepository.GetTopAlbumsForCustomTimePeriodAsyncAsync(user.UserNameLastFM, startDateTime, endDateTime, 500);

        if (!currentTopAlbums.Success)
        {
            yearOverview.LastfmErrors = true;
            return yearOverview;
        }

        yearOverview.TopAlbums = currentTopAlbums.Content;

        var currentTopArtists =
            await this._lastFmRepository.GetTopArtistsForCustomTimePeriodAsync(user.UserNameLastFM, startDateTime, endDateTime, 500);

        if (!currentTopArtists.Success)
        {
            yearOverview.LastfmErrors = true;
            return yearOverview;
        }

        yearOverview.TopArtists = currentTopArtists.Content;

        if (user.RegisteredLastFm < endDateTime.AddYears(-1))
        {
            var previousTopTracks =
                await this._lastFmRepository.GetTopTracksForCustomTimePeriodAsyncAsync(user.UserNameLastFM, startDateTime.AddYears(-1), endDateTime.AddYears(-1), 800);

            if (previousTopTracks.Success)
            {
                yearOverview.PreviousTopTracks = previousTopTracks.Content;
            }
            else
            {
                yearOverview.LastfmErrors = true;
            }

            var previousTopAlbums =
                await this._lastFmRepository.GetTopAlbumsForCustomTimePeriodAsyncAsync(user.UserNameLastFM, startDateTime.AddYears(-1), endDateTime.AddYears(-1), 800);

            if (previousTopAlbums.Success)
            {
                yearOverview.PreviousTopAlbums = previousTopAlbums.Content;
            }
            else
            {
                yearOverview.LastfmErrors = true;
            }

            var previousTopArtists =
                await this._lastFmRepository.GetTopArtistsForCustomTimePeriodAsync(user.UserNameLastFM, startDateTime.AddYears(-1), endDateTime.AddYears(-1), 800);

            if (previousTopArtists.Success)
            {
                yearOverview.PreviousTopArtists = previousTopArtists.Content;
            }
            else
            {
                yearOverview.LastfmErrors = true;
            }
        }

        if (!yearOverview.LastfmErrors)
        {
            this._cache.Set(cacheKey, yearOverview, TimeSpan.FromHours(3));
        }

        return yearOverview;
    }

    private static int GetUniqueCount(IEnumerable<UserPlayTs> plays)
    {
        return plays
            .GroupBy(x => new { x.ArtistName, x.TrackName })
            .Count();
    }

    private static double GetAvgPerDayCount(IEnumerable<UserPlayTs> plays)
    {
        return plays
            .GroupBy(g => g.TimePlayed.Date)
            .Average(a => a.Count());
    }

    private static string GetTopTrackForPlays(IEnumerable<UserPlayTs> plays)
    {
        var topTrack = plays
            .GroupBy(x => new { x.ArtistName, x.TrackName })
            .MaxBy(o => o.Count());

        if (topTrack == null)
        {
            return "No top track for this day";
        }

        return $"`{topTrack.Count()}` {StringExtensions.GetPlaysString(topTrack.Count())} - {Format.Sanitize(topTrack.Key.ArtistName)} | {Format.Sanitize(topTrack.Key.TrackName)}";
    }

    private static string GetTopAlbumForPlays(IEnumerable<UserPlayTs> plays)
    {
        var topAlbum = plays
            .GroupBy(x => new { x.ArtistName, x.AlbumName })
            .MaxBy(o => o.Count());

        if (topAlbum == null)
        {
            return "No top album for this day";
        }

        return $"`{topAlbum.Count()}` {StringExtensions.GetPlaysString(topAlbum.Count())} - {Format.Sanitize(topAlbum.Key.ArtistName)} | {Format.Sanitize(topAlbum.Key.AlbumName)}";
    }

    private static string GetTopArtistForPlays(IEnumerable<UserPlayTs> plays)
    {
        var topArtist = plays
            .GroupBy(x => x.ArtistName)
            .MaxBy(o => o.Count());

        if (topArtist == null)
        {
            return "No top artist for this day";
        }

        return $"`{topArtist.Count()}` {StringExtensions.GetPlaysString(topArtist.Count())} - {Format.Sanitize(topArtist.Key)}";
    }

    private async Task<IReadOnlyCollection<UserPlayTs>> GetWeekPlays(int userId)
    {
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        var start = DateTime.UtcNow.AddDays(-7);
        return await PlayRepository.GetUserPlaysWithinTimeRange(userId, connection, start);
    }

    public async Task<int> GetWeekTrackPlaycountAsync(int userId, string trackName, string artistName)
    {
        var plays = await GetWeekPlays(userId);

        return plays.Count(t => t.TrackName.ToLower() == trackName.ToLower() &&
                                t.ArtistName.ToLower() == artistName.ToLower());
    }

    public async Task<int> GetWeekAlbumPlaycountAsync(int userId, string albumName, string artistName)
    {
        var plays = await GetWeekPlays(userId);
        return plays.Count(ab => ab.AlbumName != null &&
                                 ab.AlbumName.ToLower() == albumName.ToLower() &&
                                 ab.ArtistName.ToLower() == artistName.ToLower());
    }

    public async Task<int> GetArtistPlaycountForTimePeriodAsync(int userId, string artistName, int daysToGoBack = 7)
    {
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        var start = DateTime.UtcNow.AddDays(-daysToGoBack);
        var plays = await PlayRepository.GetUserPlaysWithinTimeRange(userId, connection, start);

        return plays.Count(a => a.ArtistName.ToLower() == artistName.ToLower());
    }

    public async Task<List<UserStreak>> GetStreaks(int userId)
    {
        await using var db = await this._contextFactory.CreateDbContextAsync();
        return await db.UserStreaks
            .Where(w => w.UserId == userId)
            .Where(w => w.ArtistName != null || w.AlbumName != null || w.TrackName != null)
            .OrderByDescending(o => o.ArtistPlaycount)
            .ToListAsync();
    }

    public async Task DeleteStreak(long streakId)
    {
        await using var db = await this._contextFactory.CreateDbContextAsync();
        var streak = await db.UserStreaks
            .AsQueryable()
            .FirstOrDefaultAsync(f => f.UserStreakId == streakId);

        db.UserStreaks.Remove(streak);
        await db.SaveChangesAsync();
    }

    public static UserStreak GetCurrentStreak(int userId, RecentTrack lastPlay,
        IReadOnlyList<UserPlayTs> lastPlays)
    {
        if (!lastPlays.Any() || lastPlay == null)
        {
            return null;
        }

        lastPlays = lastPlays
            .OrderByDescending(o => o.TimePlayed)
            .Where(w => !lastPlay.TimePlayed.HasValue || w.TimePlayed < lastPlay.TimePlayed.Value)
            .ToList();

        var streak = new UserStreak
        {
            ArtistPlaycount = 1,
            AlbumPlaycount = 1,
            TrackPlaycount = 1,
            StreakEnded = lastPlay.TimePlayed ?? DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
            StreakStarted = lastPlay.TimePlayed ?? DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc),
            UserId = userId
        };

        for (var i = 1; i < lastPlays.Count; i++)
        {
            var currentPlay = lastPlays[i];

            if (lastPlay.ArtistName.ToLower() == currentPlay.ArtistName.ToLower())
            {
                streak.ArtistPlaycount++;
                streak.ArtistName = currentPlay.ArtistName;
                if (currentPlay.TimePlayed < streak.StreakStarted)
                {
                    streak.StreakStarted = currentPlay.TimePlayed;
                }
            }
            else
            {
                break;
            }
        }

        for (var i = 1; i < lastPlays.Count; i++)
        {
            var currentPlay = lastPlays[i];

            if (lastPlay.AlbumName != null &&
                currentPlay.AlbumName != null &&
                lastPlay.AlbumName.ToLower() == currentPlay.AlbumName.ToLower())
            {
                streak.AlbumPlaycount++;
                streak.AlbumName = currentPlay.AlbumName;
                if (currentPlay.TimePlayed < streak.StreakStarted)
                {
                    streak.StreakStarted = currentPlay.TimePlayed;
                }
            }
            else
            {
                break;
            }
        }

        for (var i = 1; i < lastPlays.Count; i++)
        {
            var currentPlay = lastPlays[i];

            if (lastPlay.TrackName.ToLower() == currentPlay.TrackName.ToLower() &&
                lastPlay.ArtistName.ToLower() == currentPlay.ArtistName.ToLower())
            {
                streak.TrackPlaycount++;
                streak.TrackName = currentPlay.TrackName;
                if (currentPlay.TimePlayed < streak.StreakStarted)
                {
                    streak.StreakStarted = currentPlay.TimePlayed;
                }
            }
            else
            {
                break;
            }
        }

        return streak;
    }

    public static bool StreakExists(UserStreak streak)
    {
        if (streak.ArtistName == null &&
            streak.AlbumName == null &&
            streak.TrackName == null)
        {
            return false;
        }

        return true;
    }

    public static bool ShouldSaveStreak(UserStreak streak)
    {
        if (!StreakExists(streak))
        {
            return false;
        }

        if (streak.ArtistPlaycount is < Constants.StreakSaveThreshold &&
            streak.AlbumPlaycount is < Constants.StreakSaveThreshold &&
            streak.TrackPlaycount is < Constants.StreakSaveThreshold)
        {
            return false;
        }

        return true;
    }

    public static string StreakToText(UserStreak streak, bool includeStart = true)
    {
        var description = new StringBuilder();
        if (streak.ArtistName != null && streak.ArtistPlaycount.HasValue)
        {
            description.AppendLine($"Artist: **[{streak.ArtistName}]({LastfmUrlExtensions.GetArtistUrl(streak.ArtistName)})** - " +
                                   $"{GetEmojiForStreakCount(streak.ArtistPlaycount.Value)}*{streak.ArtistPlaycount} plays in a row*");
        }
        if (streak.AlbumName != null && streak.AlbumPlaycount.HasValue)
        {
            description.AppendLine($"Album: **[{streak.AlbumName}](https://www.last.fm/music/{HttpUtility.UrlEncode(streak.ArtistName)}/{HttpUtility.UrlEncode(streak.AlbumName)})** - " +
                                   $"{GetEmojiForStreakCount(streak.AlbumPlaycount.Value)}*{streak.AlbumPlaycount} plays in a row*");
        }
        if (streak.TrackName != null && streak.TrackPlaycount.HasValue)
        {
            description.AppendLine($"Track: **[{streak.TrackName}](https://www.last.fm/music/{HttpUtility.UrlEncode(streak.ArtistName)}/_/{HttpUtility.UrlEncode(streak.TrackName)})** - " +
                                   $"{GetEmojiForStreakCount(streak.TrackPlaycount.Value)}*{streak.TrackPlaycount} plays in a row*");
        }

        if (description.Length == 0)
        {
            return "No active streak found.";
        }

        if (includeStart)
        {
            var specifiedDateTime = DateTime.SpecifyKind(streak.StreakStarted, DateTimeKind.Utc);
            var dateValue = ((DateTimeOffset)specifiedDateTime).ToUnixTimeSeconds();

            description.AppendLine();
            description.AppendLine($"Streak started <t:{dateValue}:R>.");
        }

        return description.ToString();
    }

    public async Task<string> UpdateOrInsertStreak(UserStreak currentStreak)
    {
        if (!ShouldSaveStreak(currentStreak))
        {
            return null;
        }

        await using var db = await this._contextFactory.CreateDbContextAsync();
        var existingStreak = await db.UserStreaks.FirstOrDefaultAsync(f =>
            f.UserId == currentStreak.UserId &&
            f.StreakStarted == currentStreak.StreakStarted &&
            (f.ArtistName != null || f.AlbumName != null || f.TrackName != null));

        if (existingStreak == null)
        {
            await db.UserStreaks.AddAsync(currentStreak);
            await db.SaveChangesAsync();
            return "Streak has been saved!";
        }

        existingStreak.ArtistName = currentStreak.ArtistName;
        existingStreak.ArtistPlaycount = currentStreak.ArtistPlaycount;
        existingStreak.AlbumName = currentStreak.AlbumName;
        existingStreak.AlbumPlaycount = currentStreak.AlbumPlaycount;
        existingStreak.TrackName = currentStreak.TrackName;
        existingStreak.TrackPlaycount = currentStreak.TrackPlaycount;
        
        db.Entry(existingStreak).State = EntityState.Modified;
        await db.SaveChangesAsync();

        return "Saved streak has been updated!";
    }

    private static string GetEmojiForStreakCount(int count)
    {
        return count switch
        {
            > 25000 => "🌌 ",
            > 15000 => "🌠 ",
            > 10000 => "🪐 ",
            > 7500 => "🌚 ",
            > 5000 => "🚀 ",
            > 2500 => "😵 ",
            1337 => "🦹‍ ",
            > 1000 => "😲 ",
            666 => "‍😈 ",
            420 => "🍃 ",
            100 => "💯 ",
            69 => "😎 ",
            > 50 => "🔥 ",
            _ => null
        };
    }

    public async Task<TopArtistList> GetUserTopArtists(int userId, int days)
    {
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        var start = DateTime.UtcNow.AddDays(-days);
        var plays = await PlayRepository.GetUserPlaysWithinTimeRange(userId, connection, start);
        var topArtists = plays
            .GroupBy(x => x.ArtistName)
            .Select(s => new TopArtist
            {
                ArtistName = s.Key,
                UserPlaycount = s.Count()
            })
            .OrderByDescending(o => o.UserPlaycount);

        return new TopArtistList
        {
            TotalAmount = topArtists.Count(),
            TopArtists = topArtists.ToList()
        };
    }

    public async Task<List<UserTrack>> GetUserTopTracksForArtist(int userId, int days, string artistName)
    {
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        var start = DateTime.UtcNow.AddDays(-days);
        var plays = await PlayRepository.GetUserPlaysWithinTimeRange(userId, connection, start);

        return plays.Where(w => w.ArtistName.ToLower() == artistName.ToLower())
            .GroupBy(x => new { x.ArtistName, x.TrackName })
            .Select(s => new UserTrack
            {
                ArtistName = s.Key.ArtistName,
                Name = s.Key.TrackName,
                Playcount = s.Count()
            })
            .OrderByDescending(o => o.Playcount)
            .ToList();
    }

    public static List<GuildTrack> GetGuildTopTracks(IEnumerable<UserPlayTs> plays, DateTime startDateTime, OrderType orderType, string artistName)
    {
        return plays
            .Where(w => w.TimePlayed > startDateTime)
            .Where(w => string.IsNullOrWhiteSpace(artistName) || w.ArtistName.ToLower() == artistName.ToLower())
            .GroupBy(x => new
            {
                ArtistName = x.ArtistName.ToLower(),
                TrackName = x.TrackName.ToLower()
            })
            .Select(s => new GuildTrack
            {
                TrackName = s.First().TrackName,
                ArtistName = s.First().ArtistName,
                ListenerCount = s.Select(se => se.UserId).Distinct().Count(),
                TotalPlaycount = s.Count()
            })
            .OrderByDescending(o => orderType == OrderType.Listeners ? o.ListenerCount : o.TotalPlaycount)
            .ThenByDescending(o => orderType == OrderType.Listeners ? o.TotalPlaycount : o.ListenerCount)
            .Take(120)
            .ToList();
    }

    public static List<GuildAlbum> GetGuildTopAlbums(IEnumerable<UserPlayTs> plays, DateTime startDateTime, OrderType orderType, string artistName)
    {
        return plays
            .Where(w => w.TimePlayed > startDateTime && w.AlbumName != null)
            .Where(w => string.IsNullOrWhiteSpace(artistName) || w.ArtistName.ToLower() == artistName.ToLower())
            .GroupBy(x => new
            {
                ArtistName = x.ArtistName.ToLower(),
                AlbumName = x.AlbumName.ToLower()
            }).Select(s => new GuildAlbum
            {
                AlbumName = s.First().AlbumName,
                ArtistName = s.First().ArtistName,
                ListenerCount = s.Select(se => se.UserId).Distinct().Count(),
                TotalPlaycount = s.Count()
            })
            .OrderByDescending(o => orderType == OrderType.Listeners ? o.ListenerCount : o.TotalPlaycount)
            .ThenByDescending(o => orderType == OrderType.Listeners ? o.TotalPlaycount : o.ListenerCount)
            .Take(120)
            .ToList();
    }

    public static List<GuildArtist> GetGuildTopArtists(IEnumerable<UserPlayTs> plays, DateTime startDateTime, OrderType orderType, int limit = 120, bool includeListeners = false)
    {
        return plays
            .Where(w => w.TimePlayed > startDateTime)
            .GroupBy(x => x.ArtistName.ToLower())
            .Select(s => new GuildArtist
            {
                ArtistName = s.First().ArtistName,
                ListenerCount = s.Select(se => se.UserId).Distinct().Count(),
                TotalPlaycount = s.Count(),
                ListenerUserIds = includeListeners ? s.Select(se => se.UserId).ToList() : null
            })
            .OrderByDescending(o => orderType == OrderType.Listeners ? o.ListenerCount : o.TotalPlaycount)
            .ThenByDescending(o => orderType == OrderType.Listeners ? o.TotalPlaycount : o.ListenerCount)
            .Take(limit)
            .ToList();
    }

    public async Task<List<WhoKnowsObjectWithUser>> GetGuildUsersTotalPlaycount(ICommandContext context,
        IDictionary<int, FullGuildUser> guildUsers,
        int guildId)
    {
        const string sql = "SELECT u.total_playcount AS playcount, " +
                           "u.user_id " +
                           "FROM users AS u " +
                           "INNER JOIN guild_users AS gu ON gu.user_id = u.user_id " +
                           "WHERE gu.guild_id = @guildId AND u.total_playcount is not null " +
                           "ORDER BY u.total_playcount DESC ";

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        var userAlbums = (await connection.QueryAsync<WhoKnowsAlbumDto>(sql, new
        {
            guildId,
        })).ToList();

        var whoKnowsAlbumList = new List<WhoKnowsObjectWithUser>();

        for (var i = 0; i < userAlbums.Count; i++)
        {
            var userAlbum = userAlbums[i];

            if (!guildUsers.TryGetValue(userAlbum.UserId, out var guildUser))
            {
                continue;
            }

            var userName = guildUser.UserName ?? guildUser.UserNameLastFM;

            if (i <= 10)
            {
                var discordUser = await context.Guild.GetUserAsync(guildUser.DiscordUserId);
                if (discordUser != null)
                {
                    userName = discordUser.Nickname ?? discordUser.Username;
                }
            }

            whoKnowsAlbumList.Add(new WhoKnowsObjectWithUser
            {
                DiscordName = userName,
                Playcount = userAlbum.Playcount,
                LastFMUsername = guildUser.UserNameLastFM,
                UserId = userAlbum.UserId,
                LastUsed = guildUser.LastUsed,
                LastMessage = guildUser.LastMessage,
                Roles = guildUser.Roles
            });
        }

        return whoKnowsAlbumList;
    }

    public async Task<int> GetWeekArtistPlaycountForGuildAsync(int guildId, string artistName)
    {
        var minDate = DateTime.UtcNow.AddDays(-7);

        const string sql = "SELECT coalesce(count(up.time_played), 0) " +
                           "FROM user_play_ts AS up " +
                           "INNER JOIN users AS u ON up.user_id = u.user_id " +
                           "INNER JOIN guild_users AS gu ON gu.user_id = u.user_id " +
                           "WHERE gu.guild_id = @guildId AND " +
                           "UPPER(up.artist_name) = UPPER(CAST(@artistName AS CITEXT)) AND " +
                           "up.time_played >= @minDate";

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        return await connection.QuerySingleAsync<int>(sql, new
        {
            guildId,
            artistName,
            minDate
        });
    }

    public async Task<DateTime?> GetArtistFirstPlayDate(int userId, string artistName)
    {
        const string sql = "SELECT first(time_played, time_played) FROM user_play_ts " +
                           "WHERE user_id = @userId AND " +
                           "UPPER(artist_name) = UPPER(CAST(@artistName AS CITEXT)) ";

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<DateTime?>(sql, new
        {
            userId,
            artistName,
        });
    }

    public async Task<DateTime?> GetAlbumFirstPlayDate(int userId, string artistName, string albumName)
    {
        const string sql = "SELECT first(time_played, time_played) FROM user_play_ts " +
                           "WHERE user_id = @userId AND " +
                           "UPPER(artist_name) = UPPER(CAST(@artistName AS CITEXT)) AND " +
                           "UPPER(album_name) = UPPER(CAST(@albumName AS CITEXT))";

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<DateTime?>(sql, new
        {
            userId,
            artistName,
            albumName
        });
    }

    public async Task<DateTime?> GetTrackFirstPlayDate(int userId, string artistName, string trackName)
    {
        const string sql = "SELECT first(time_played, time_played) FROM user_play_ts " +
                           "WHERE user_id = @userId AND " +
                           "UPPER(artist_name) = UPPER(CAST(@artistName AS CITEXT)) AND " +
                           "UPPER(track_name) = UPPER(CAST(@trackName AS CITEXT))";

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<DateTime?>(sql, new
        {
            userId,
            artistName,
            trackName
        });
    }

    public async Task<IList<UserPlayTs>> GetGuildUsersPlays(int guildId, int amountOfDays)
    {
        var cacheKey = $"guild-user-plays-{guildId}-{amountOfDays}";

        var cachedPlaysAvailable = this._cache.TryGetValue(cacheKey, out List<UserPlayTs> userPlays);
        if (cachedPlaysAvailable)
        {
            return userPlays;
        }

        var sql = "SELECT up.* " +
                  "FROM user_play_ts AS up " +
                  "INNER JOIN users AS u ON up.user_id = u.user_id  " +
                  "INNER JOIN guild_users AS gu ON gu.user_id = u.user_id " +
                  $"WHERE gu.guild_id = @guildId  AND gu.bot != true AND time_played > current_date - interval '{amountOfDays}' day " +
                  "AND NOT up.user_id = ANY(SELECT user_id FROM guild_blocked_users WHERE blocked_from_who_knows = true AND guild_id = @guildId) " +
                  "AND (gu.who_knows_whitelisted OR gu.who_knows_whitelisted IS NULL) ";

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        userPlays = (await connection.QueryAsync<UserPlayTs>(sql, new
        {
            guildId
        })).ToList();

        this._cache.Set(cacheKey, userPlays, TimeSpan.FromMinutes(10));

        return userPlays;
    }

    public async Task<List<UserPlayTs>> GetGuildUsersPlaysForTimeLeaderBoard(int guildId)
    {
        const string sql = "SELECT up.user_id, up.track_name, up.album_name, up.artist_name, up.time_played " +
                           "FROM public.user_play_ts AS up " +
                           "INNER JOIN users AS u ON up.user_id = u.user_id " +
                           "INNER JOIN guild_users AS gu ON gu.user_id = u.user_id " +
                           "WHERE gu.guild_id = @guildId " +
                           "AND time_played > current_date - interval '9' day  AND time_played < current_date - interval '2' day  " +
                           "AND NOT up.user_id = ANY(SELECT user_id FROM guild_blocked_users WHERE blocked_from_who_knows = true AND guild_id = @guildId) " +
                           "AND (gu.who_knows_whitelisted OR gu.who_knows_whitelisted IS NULL) ";

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        var userPlays = (await connection.QueryAsync<UserPlayTs>(sql, new
        {
            guildId,
        })).ToList();

        return userPlays;
    }

    public bool UserHasImported(IEnumerable<UserPlayTs> userPlays)
    {
        return userPlays
            .GroupBy(g => g.TimePlayed.Date)
            .Count(w => w.Count() > 2500) >= 7;
    }

    public async Task<IReadOnlyList<UserPlayTs>> GetAllUserPlays(int userId)
    {
        await using var connection = new NpgsqlConnection(this._botSettings.Database.ConnectionString);
        await connection.OpenAsync();

        return await PlayRepository.GetUserPlays(userId, connection, 9999999);
    }
}
