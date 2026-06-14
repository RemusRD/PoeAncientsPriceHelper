using System.Net;
using System.Net.Http;

namespace PoeAncientsPriceHelper.Tests;

internal sealed class FakeHttpMessageHandler(string response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response)
        });
}

// Records every request URI + Referer header so tests can assert how the request was built.
internal sealed class CapturingFakeHttpHandler(string response) : HttpMessageHandler
{
    public List<string> Urls { get; } = [];
    public List<string?> Referers { get; } = [];
    public DateTimeOffset? ResponseDate { get; init; }
    public TimeSpan? ResponseAge { get; init; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Urls.Add(request.RequestUri!.AbsoluteUri);
        Referers.Add(request.Headers.TryGetValues("Referer", out var v) ? string.Join("", v) : null);
        var responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response)
        };
        responseMessage.Headers.Date = ResponseDate;
        responseMessage.Headers.Age = ResponseAge;
        return Task.FromResult(responseMessage);
    }
}

internal sealed class CountingFakeHttpHandler(byte[] response) : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestCount++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(response)
        });
    }
}

internal sealed class FailingHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    public TempDir() => System.IO.Directory.CreateDirectory(Path);
    public void Dispose() { try { System.IO.Directory.Delete(Path, true); } catch { } }
}
