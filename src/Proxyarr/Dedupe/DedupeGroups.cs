using Proxyarr.Configuration;

namespace Proxyarr.Dedupe;

/// <summary>
/// A set of dedupe-enabled client instances that share one upstream download client. Members are
/// the <see cref="ClientInstanceConfig"/>s; the managed tags/claims a torrent or NZB job carries
/// are exactly the member instance names. <see cref="Key"/> is a stable identifier used both for
/// the keyed async lock namespace and (for SABnzbd) the persisted <c>GroupKey</c>.
/// </summary>
public sealed record DedupeGroup(string Key, IReadOnlyList<ClientInstanceConfig> Members)
{
    /// <summary>The member instance names — i.e. the managed tag/claim vocabulary for this group.</summary>
    public IReadOnlyCollection<string> MemberNames { get; } =
        new HashSet<string>(
            Members.Select(member => member.Name),
            StringComparer.OrdinalIgnoreCase
        );

    public bool IsManagedTag(string tag) => MemberNames.Contains(tag);
}

/// <summary>
/// Derives dedupe groups from a <see cref="ProxyConfig"/>. Instances of the same client type land
/// in the same group when they reference the same named <see cref="DedupeConfig.Group"/>.
/// Registered as a singleton.
/// </summary>
public sealed class DedupeGroups
{
    private readonly Dictionary<string, DedupeGroup> _byInstance;

    private DedupeGroups(IReadOnlyList<DedupeGroup> groups)
    {
        All = groups;
        _byInstance = new Dictionary<string, DedupeGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            foreach (var member in group.Members)
            {
                _byInstance[InstanceKey(member)] = group;
            }
        }
    }

    /// <summary>Every group with at least one dedupe-enabled member.</summary>
    public IReadOnlyList<DedupeGroup> All { get; }

    /// <summary>The group the instance belongs to, or null when its dedupe is off.</summary>
    public DedupeGroup? For(ClientInstanceConfig instance) =>
        _byInstance.GetValueOrDefault(InstanceKey(instance));

    public static DedupeGroups Build(ProxyConfig config)
    {
        var byKey = new Dictionary<string, List<ClientInstanceConfig>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var client in config.ResolvedClients)
        {
            if (!client.DedupeEnabled)
            {
                continue;
            }

            var key = GroupKey(client);
            if (!byKey.TryGetValue(key, out var members))
            {
                members = [];
                byKey[key] = members;
                order.Add(key);
            }

            members.Add(client);
        }

        var groups = order.Select(key => new DedupeGroup(key, byKey[key])).ToList();
        return new DedupeGroups(groups);
    }

    /// <summary>Stable named-group identity, namespaced by client type.</summary>
    public static string GroupKey(ClientInstanceConfig client)
    {
        var type = client.Type.ToLowerInvariant();
        var group =
            client.Dedupe?.Group
            ?? throw new InvalidOperationException($"Client '{client.Name}' is not in a group.");
        return $"{type}|group:{group}";
    }

    private static string InstanceKey(ClientInstanceConfig client) =>
        $"{client.Type}|{client.Name}";
}
