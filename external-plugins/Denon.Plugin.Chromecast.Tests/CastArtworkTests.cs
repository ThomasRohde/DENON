using System.IO;
using System.Net;
using System.Net.Http;
using Denon.Plugin.Chromecast.Cast;
using Xunit;

namespace Denon.Plugin.Chromecast.Tests;

public sealed class CastArtworkTests
{
    [Theory]
    [InlineData("http://192.168.1.5:32400/photo/thumb.jpg")] // Plex or Jellyfin on the LAN.
    [InlineData("https://i.ytimg.com/vi/abc/hqdefault.jpg")]
    [InlineData("http://10.0.0.8/art.png")]
    [InlineData("http://[fd00::8]/art.png")]
    public void Lan_and_internet_artwork_is_allowed(string url)
    {
        Assert.True(CastArtwork.IsAllowedImageUri(new Uri(url)));
    }

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://127.10.0.1/")]
    [InlineData("http://localhost:8080/")]
    [InlineData("http://LOCALHOST/")]
    [InlineData("http://app.localhost/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // Cloud metadata service.
    [InlineData("http://169.254.1.1/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://224.0.0.251/")]
    [InlineData("http://255.255.255.255/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://192.168.1.5/art.jpg")]
    [InlineData("data:image/png;base64,AAAA")]
    public void Local_metadata_and_non_http_targets_are_refused(string url)
    {
        Assert.False(CastArtwork.IsAllowedImageUri(new Uri(url)));
    }

    [Fact]
    public async Task A_refused_url_is_never_requested()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var image = await CastArtwork.FetchAsync(new Uri("http://169.254.169.254/latest/meta-data/"), 1000, handler, default);

        Assert.Null(image);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task A_redirect_to_a_refused_target_is_not_followed()
    {
        var handler = new FakeHandler(_ => Redirect("http://127.0.0.1:8080/secret"));

        var image = await CastArtwork.FetchAsync(new Uri("http://192.168.1.5/art.jpg"), 1000, handler, default);

        Assert.Null(image);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_redirect_to_an_allowed_target_is_followed()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath == "/art.jpg"
            ? Redirect("/real.png")
            : Image([1, 2, 3], "image/png"));

        var image = await CastArtwork.FetchAsync(new Uri("http://192.168.1.5/art.jpg"), 1000, handler, default);

        Assert.NotNull(image);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Value.Bytes);
        Assert.Equal("image/png", image.Value.MediaType);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Endless_redirects_give_no_image()
    {
        var handler = new FakeHandler(_ => Redirect("http://192.168.1.5/again"));

        Assert.Null(await CastArtwork.FetchAsync(new Uri("http://192.168.1.5/art.jpg"), 1000, handler, default));
        Assert.Equal(4, handler.Requests);
    }

    [Fact]
    public async Task A_chunked_body_over_the_limit_is_refused_while_reading()
    {
        var body = new EndlessStream();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });

        var image = await CastArtwork.FetchAsync(new Uri("http://192.168.1.5/art.jpg"), 64 * 1024, handler, default);

        Assert.Null(image);
        Assert.InRange(body.BytesRead, 64 * 1024, 2 * 64 * 1024); // It stopped soon after the limit.
    }

    [Fact]
    public async Task A_declared_length_over_the_limit_is_refused_before_reading()
    {
        var handler = new FakeHandler(_ => Image(new byte[2000], "image/jpeg"));

        Assert.Null(await CastArtwork.FetchAsync(new Uri("http://192.168.1.5/art.jpg"), 1000, handler, default));
    }

    [Fact]
    public async Task A_failed_status_gives_no_image()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await CastArtwork.FetchAsync(new Uri("http://192.168.1.5/art.jpg"), 1000, handler, default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(40_000)]
    public async Task ReadBounded_returns_a_body_up_to_the_limit(int length)
    {
        var data = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();

        Assert.Equal(data, await CastArtwork.ReadBoundedAsync(new MemoryStream(data), 40_000, default));
    }

    [Fact]
    public async Task ReadBounded_returns_null_one_byte_over_the_limit()
    {
        Assert.Null(await CastArtwork.ReadBoundedAsync(new MemoryStream(new byte[40_001]), 40_000, default));
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpResponseMessage Image(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>A body with no length that never ends, like a hostile chunked response.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, 4096);
            BytesRead += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
