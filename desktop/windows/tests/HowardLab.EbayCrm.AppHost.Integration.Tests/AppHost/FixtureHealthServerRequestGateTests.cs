using System.Net;
using System.Net.Sockets;
using HowardLab.EbayCrm.AppHost.Fixture;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;

public sealed class FixtureHealthServerRequestGateTests
{
    [Fact]
    public async Task SuccessfulRequestCountGateWaitsForSecondAcceptedIdentityBoundRequest()
    {
        const long generation = 7;
        const string nonce = "request-count-nonce";
        var port = ReserveLoopbackPort();
        await using var server = new FixtureHealthServer(
            port,
            new HealthPayload(
                ControlProtocolConstants.CurrentVersion,
                ControlProtocolConstants.FixtureBuildIdentity,
                generation,
                nonce,
                "ready",
                0));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var secondAccepted = server.WaitForSuccessfulRequestCountAsync(
            requiredCount: 2,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, await SendAcceptedAsync(
            http,
            server.Endpoint,
            generation,
            nonce));
        Assert.False(secondAccepted.IsCompleted);

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(server.Endpoint)).StatusCode);
        Assert.False(secondAccepted.IsCompleted);

        Assert.Equal(HttpStatusCode.OK, await SendAcceptedAsync(
            http,
            server.Endpoint,
            generation,
            nonce));
        await secondAccepted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<HttpStatusCode> SendAcceptedAsync(
        HttpClient http,
        string endpoint,
        long generation,
        string nonce)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation(
            "X-AppHost-Protocol",
            ControlProtocolConstants.CurrentVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(
            "X-AppHost-Build",
            ControlProtocolConstants.FixtureBuildIdentity);
        request.Headers.TryAddWithoutValidation(
            "X-AppHost-Generation",
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-AppHost-Nonce", nonce);
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
