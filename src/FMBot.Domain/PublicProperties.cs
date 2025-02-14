using System.Collections.Concurrent;
using FMBot.Domain.Models;
using SpotifyAPI.Web;

namespace FMBot.Domain;

public static class PublicProperties
{
    public static bool IssuesAtLastFm = false;
    public static string IssuesReason = null;

    public static readonly ConcurrentDictionary<string, ulong> SlashCommands = new();
    public static readonly ConcurrentDictionary<ulong, int> PremiumServers = new();
    public static readonly ConcurrentDictionary<ulong, int> RegisteredUsers = new();

    public static readonly ConcurrentDictionary<ulong, CommandResponse> UsedCommandsResponses = new();
    public static readonly ConcurrentDictionary<ulong, string> UsedCommandsErrorReferences = new();

    public static SpotifyClientConfig SpotifyConfig = null;
}
