using ConnectX.Server.Interfaces;
using ConnectX.Server.Models.ZeroTier;

namespace ConnectX.Server.Services;

public class ZeroTierApiService(HttpClient httpClient) : IZeroTierApiService
{
    public async Task<NodeStatusModel?> GetNodeStatusAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/status");
        using var res = await httpClient.SendAsync(req, cancellationToken);

        res.EnsureSuccessStatusCode();

        var result = await res.Content.ReadFromJsonAsync(ZeroTierModelContext.Default.NodeStatusModel, cancellationToken);

        return result;
    }

    public async Task<string[]> ListNetworkIds(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/controller/network");
        using var res = await httpClient.SendAsync(req, cancellationToken);

        res.EnsureSuccessStatusCode();

        var result = await res.Content.ReadFromJsonAsync(ZeroTierModelContext.Default.StringArray, cancellationToken);

        return result ?? [];
    }

    public async Task<NetworkDetailsModel?> CreateOrUpdateNetwork(string networkId, NetworkDetailsReqModel details, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/controller/network/{networkId}");

        req.Content = JsonContent.Create(details, ZeroTierModelContext.Default.NetworkDetailsReqModel);

        using var res = await httpClient.SendAsync(req, cancellationToken);

        res.EnsureSuccessStatusCode();

        var result = await res.Content.ReadFromJsonAsync(ZeroTierModelContext.Default.NetworkDetailsModel, cancellationToken);

        return result;
    }

    public async Task<NetworkDetailsModel?> GetNetworkDetailsAsync(string networkId, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/controller/network/{networkId}");
        using var res = await httpClient.SendAsync(req, cancellationToken);

        res.EnsureSuccessStatusCode();

        var result = await res.Content.ReadFromJsonAsync(ZeroTierModelContext.Default.NetworkDetailsModel, cancellationToken);

        return result;
    }

    public async Task<NetworkDetailsModel?> DeleteNetworkAsync(string networkId, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/controller/network/{networkId}");
        using var res = await httpClient.SendAsync(req, cancellationToken);

        res.EnsureSuccessStatusCode();

        var result = await res.Content.ReadFromJsonAsync(ZeroTierModelContext.Default.NetworkDetailsModel, cancellationToken);

        return result;
    }

    public async Task<NetworkDetailsModel?> DeleteNetworkMemberAsync(string networkId, string nodeId, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/controller/network/{networkId}/member/{nodeId}");
        using var res = await httpClient.SendAsync(req, cancellationToken);

        res.EnsureSuccessStatusCode();

        var result = await res.Content.ReadFromJsonAsync(ZeroTierModelContext.Default.NetworkDetailsModel, cancellationToken);

        return result;
    }

    public async Task<NetworkPeerModel[]?> GetNetworkPeersAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/peer");
        using var res = await httpClient.SendAsync(req, cancellationToken);

        res.EnsureSuccessStatusCode();

        var result = await res.Content.ReadFromJsonAsync(ZeroTierModelContext.Default.NetworkPeerModelArray, cancellationToken);

        return result;
    }
}