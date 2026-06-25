using System.Net;

namespace TLAHStudio.Core.Tests;

internal sealed class StaticHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;

    public StaticHttpClientFactory(HttpClient client)
    {
        _client = client;
    }

    public HttpClient CreateClient(string name) => _client;
}

internal sealed class MapHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public MapHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_handler(request));
    }

    public static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    public static HttpResponseMessage Bytes(HttpStatusCode statusCode, byte[] bytes) =>
        new(statusCode)
        {
            Content = new ByteArrayContent(bytes)
        };
}
