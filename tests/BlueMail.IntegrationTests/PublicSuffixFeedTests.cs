using System.Net;
using Amazon.S3;
using BlueMail.Application.Scanning;
using BlueMail.Auth;
using BlueMail.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueMail.IntegrationTests;

/// <summary>The Public Suffix List is downloaded, checked, installed, kept in S3 and loaded again after a restart.</summary>
[Collection(StackCollection.Name)]
public class PublicSuffixFeedTests(StackFixture stack)
{
    private sealed class FakeServer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer(request));
        }
    }

    private static string Embedded()
    {
        using var s = typeof(PublicSuffixList).Assembly.GetManifestResourceStream("public_suffix_list.dat")!;
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public async Task A_complete_download_is_installed_kept_and_reloaded_and_a_broken_one_is_refused()
    {
        var before = PublicSuffixList.Default;
        var s3 = stack.Api.Services.GetRequiredService<IAmazonS3>();
        var feed = new PublicSuffixFeed(s3, new ConfigurationBuilder().Build(), NullLogger<PublicSuffixFeed>.Instance);
        var newer = Embedded() + "\npslfeed-test.example\n"; // a new private suffix, as a later list would add
        try
        {
            // A truncated download and an HTML error page are refused: the list in use does not change.
            var broken = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(newer[..20_000]) });
            (await feed.UpdateAsync("https://psl.test/list", default, broken)).Should().BeFalse();
            var html = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>maintenance</html>") });
            (await feed.UpdateAsync("https://psl.test/list", default, html)).Should().BeFalse();
            PublicSuffixList.Default.Should().BeSameAs(before);

            // A complete list is installed for DMARC: the new private suffix makes a.b.pslfeed-test.example its own organisation.
            var good = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(newer) { Headers = { LastModified = DateTimeOffset.UtcNow.AddHours(-1) } },
            });
            (await feed.UpdateAsync("https://psl.test/list", default, good)).Should().BeTrue();
            PublicSuffixList.Default.OrganizationalDomain("mail.shop.pslfeed-test.example").Should().Be("shop.pslfeed-test.example");
            before.OrganizationalDomain("mail.shop.pslfeed-test.example").Should().Be("pslfeed-test.example");

            // The next check sends If-Modified-Since; 304 keeps the list.
            var notModified = new FakeServer(r =>
            {
                r.Headers.IfModifiedSince.Should().NotBeNull();
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            });
            (await feed.UpdateAsync("https://psl.test/list", default, notModified)).Should().BeFalse();
            notModified.Calls.Should().Be(1);

            using (var saved = await s3.GetObjectAsync(ScannerFeeds.BackupBucket, PublicSuffixFeed.BackupKey))
            {
                saved.ContentLength.Should().BeGreaterThan(100_000, "a copy is kept in S3");
            }

            // After a restart the saved copy is loaded without the network.
            PublicSuffixList.Use(before);
            await new PublicSuffixFeed(s3, new ConfigurationBuilder().Build(), NullLogger<PublicSuffixFeed>.Instance).LoadBackupAsync(default);
            PublicSuffixList.Default.OrganizationalDomain("mail.shop.pslfeed-test.example").Should().Be("shop.pslfeed-test.example");
        }
        finally
        {
            PublicSuffixList.Use(before);
            await s3.DeleteObjectAsync(ScannerFeeds.BackupBucket, PublicSuffixFeed.BackupKey);
        }
    }
}
