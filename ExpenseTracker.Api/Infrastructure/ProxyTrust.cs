using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace ExpenseTracker.Api.Infrastructure;

public static class ProxyTrust
{
    public static void Configure(ForwardedHeadersOptions options, string? configured)
    {
        var cidrs = string.IsNullOrWhiteSpace(configured)
            ? new[] { "127.0.0.1/32", "::1/128" }
            : configured.Split(',', StringSplitOptions.TrimEntries);
        // Parse everything before changing options. Invalid or empty entries must
        // not accidentally leave an empty list, which means trusting all proxies.
        var networks = cidrs.Select(cidr =>
        {
            if (!System.Net.IPNetwork.TryParse(cidr, out var network) || network.PrefixLength == 0)
                throw new InvalidOperationException("ForwardedHeaders:TrustedNetworks requires explicit, valid nonzero CIDRs");
            return network;
        }).ToArray();
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.RequireHeaderSymmetry = true;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var network in networks)
            options.KnownIPNetworks.Add(network);
    }
}
