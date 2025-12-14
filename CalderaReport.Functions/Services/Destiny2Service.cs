using API.Models.Responses;
using CalderaReport.Functions.Clients.Abstract;
using CalderaReport.Functions.Services.Abstract;
using Domain.Data;
using Domain.DB;
using Domain.DestinyApi;
using Domain.Manifest;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace CalderaReport.Functions.Services
{
    public class Destiny2Service : IDestiny2Service
    {
        private readonly IDestiny2ApiClient _client;
        private readonly IManifestClient _manifestClient;
        private readonly IDatabase _cache;
        private readonly AppDbContext _context;

        private static readonly DateTime ActivityCutoffUtc = new(2025, 7, 15, 0, 0, 0, DateTimeKind.Utc);

        public Destiny2Service(IDestiny2ApiClient client, IManifestClient manifestClient, IConnectionMultiplexer redis, AppDbContext context)
        {
            _client = client;
            _manifestClient = manifestClient;
            _cache = redis.GetDatabase();
            _context = context;
        }

        public async Task LoadPlayerActivityReports(Player player, DateTime lastPlayedActivityDate, string characterId)
        {
            var entries = await _cache.HashGetAllAsync("activityHashMappings");
            var activityHashMap = entries.ToDictionary(
                x => long.TryParse(x.Name.ToString(), out var nameHash) ? nameHash : 0,
                x => long.TryParse(x.Value.ToString(), out var valueHash) ? valueHash : 0
            );

            _context.Players.Update(player);
            await _context.SaveChangesAsync();

            var page = 0;
            var reportsToAdd = new List<ActivityReport>();
            var hasReachedLastUpdate = false;
            var searchToDate = player.NeedsFullCheck ? ActivityCutoffUtc : lastPlayedActivityDate;

            var activityCount = 250;

            Task<DestinyApiResponse<DestinyActivityHistoryResults>> inFlight =
            _client.GetHistoricalStatsForCharacter(player.Id, player.MembershipType, characterId, page, activityCount);

            try
            {
                while (!hasReachedLastUpdate)
                {
                    var newReportIds = new List<long>();

                    var response = await inFlight;
                    if (response.ErrorCode != 1 || response.Response?.activities == null || !response.Response.activities.Any())
                        break;

                    page++;
                    var prefetchNext = _client.GetHistoricalStatsForCharacter(player.Id, player.MembershipType, characterId, page, activityCount);

                    foreach (var activity in response.Response.activities)
                    {
                        hasReachedLastUpdate = activity.period <= (searchToDate);
                        if (activity.period < ActivityCutoffUtc || hasReachedLastUpdate)
                            break;

                        var rawHash = activity.activityDetails.referenceId;
                        if (!activityHashMap.TryGetValue(rawHash, out var canonicalId))
                            continue;

                        if (!long.TryParse(activity.activityDetails.instanceId, out var instanceId))
                            continue;

                        var existing = await _context.ActivityReports
                            .Include(ar => ar.Players)
                            .FirstOrDefaultAsync(ar => ar.Id == instanceId);

                        if (existing is null)
                        {
                            newReportIds.Add(instanceId);

                            reportsToAdd.Add(new ActivityReport
                            {
                                Id = instanceId,
                                ActivityId = canonicalId,
                                Date = activity.period,
                                NeedsFullCheck = activity.values["playerCount"].basic.value != 1,
                                Players = new List<ActivityReportPlayer>()
                                {
                                    new ActivityReportPlayer
                                    {
                                        PlayerId = player.Id,
                                        Score = (int)activity.values["score"].basic.value,
                                        Completed = activity.values["completed"].basic.value == 1 && activity.values["completionReason"].basic.value != 2.0,
                                        Duration = TimeSpan.FromSeconds(activity.values["activityDurationSeconds"].basic.value),
                                        ActivityId = canonicalId
                                    }
                                }
                            });
                        }
                        else if (existing.NeedsFullCheck && !existing.Players.Any(p => p.PlayerId == player.Id))
                        {
                            existing.Players.Add(new ActivityReportPlayer
                            {
                                PlayerId = player.Id,
                                Score = (int)activity.values["score"].basic.value,
                                Completed = activity.values["completed"].basic.value == 1 && activity.values["completionReason"].basic.value != 2.0,
                                Duration = TimeSpan.FromSeconds(activity.values["activityDurationSeconds"].basic.value),
                                ActivityId = canonicalId
                            });
                            existing.NeedsFullCheck = existing.Players.Count != activity.values["playerCount"].basic.value;
                        }
                    }

                    if (reportsToAdd.Any())
                    {
                        _context.ActivityReports.AddRange(reportsToAdd);
                        await _context.SaveChangesAsync();
                        reportsToAdd.Clear();
                    }

                    if (response.Response.activities.Last().period < ActivityCutoffUtc)
                        break;

                    inFlight = prefetchNext;
                }

                player.NeedsFullCheck = true;
                _context.Players.Update(player);
                await _context.SaveChangesAsync();
            }
            catch (Exception)
            {
                if (await _context.Players.AsNoTracking().AnyAsync(p => p.Id == player.Id))
                {
                    _context.Players.Update(player);
                    try { await _context.SaveChangesAsync(); } catch { /* ignore */ }
                }
                throw;
            }
        }




    }
}
