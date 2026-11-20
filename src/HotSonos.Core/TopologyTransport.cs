using System.Collections.Concurrent;
using HotSonos.Core.Models;

namespace HotSonos.Core;

/// <summary>Reads each group coordinator's transport so a topology card can say Playing, Paused, or Stopped.</summary>
public static class TopologyTransport
{
    public static async Task<IReadOnlyDictionary<string, string>> ReadAsync(
        SonosSoapClient soap,
        SonosTopologySnapshot? snapshot,
        CancellationToken ct = default)
    {
        var leaders = (snapshot?.Members ?? [])
            .Where(m => m.IsCoordinator && !string.IsNullOrWhiteSpace(m.GroupId) && !string.IsNullOrWhiteSpace(m.CoordinatorIpAddress))
            .GroupBy(m => m.GroupId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        var results = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (leaders.Count == 0)
            return results;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        await Task.WhenAll(leaders.Select(async leader =>
        {
            var label = "Unknown";
            try
            {
                var controller = new SonosController(leader.CoordinatorIpAddress, leader.CoordinatorUuid, soap);
                var state = await controller.GetTransportStateAsync(budget.Token).ConfigureAwait(false);
                label = TopologyLayout.PlayLabel(state);
            }
            catch (Exception)
            {
                label = "Unknown";
            }

            results[leader.GroupId] = label;
        })).ConfigureAwait(false);
        return results;
    }
}
