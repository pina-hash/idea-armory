using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Armory.Storage;
using Armory.Storage.Tests;

namespace Armory.TestSupport.Tests;

// The fake ideabosco.com must answer exactly like docs/agent/CONTRACT.md sections 2 and 3.
public sealed class FakeIdeaBoscoTests(TestDatabaseFixture fixture) : IClassFixture<TestDatabaseFixture>
{
    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string? error = null)
    {
        Assert.Equal(status, response.StatusCode);
        if (error is not null) Assert.Equal(error, (string?)(await Harness.Json(response))!["error"]);
    }

    // ---- Section 2: blob URLs ----

    [PostgresFact]
    public async Task BlobUrlPutAnswers200AndExistsFollowsTheStore()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var (project, _, student) = await h.SeedProjectAsync();
        var token = h.Supabase.IssueSession(student).AccessToken;
        var bytes = Encoding.UTF8.GetBytes("armory blob");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = ContentObjectKey.FromHash(hash);

        using var first = await h.BlobUrl(project, hash, bytes.Length, "PUT", token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = (await Harness.Json(first))!.AsObject();
        Assert.Equal(["exists", "expiresAt", "headers", "url"], body.Select(p => p.Key).Order(StringComparer.Ordinal));
        var url = (string)body["url"]!;
        Assert.StartsWith($"https://fake-s3.armory.test/blobs/sha256/{hash[..2]}/{hash[2..4]}/{hash}?X-Amz-Algorithm=AWS4-HMAC-SHA256&", url);
        Assert.StartsWith($"https://fake-s3.armory.test/{key}?", url);
        Assert.StartsWith(FakeIdeaBosco.ObjectUrlFor(hash) + "?", url);
        Assert.Contains("&X-Amz-Expires=900&", url);
        Assert.Contains("&X-Amz-SignedHeaders=content-length%3Bhost&", url); // a PUT is bound to its byte count
        Assert.Empty(body["headers"]!.AsObject());
        var expiresAt = DateTimeOffset.Parse((string)body["expiresAt"]!, CultureInfo.InvariantCulture);
        Assert.Equal(h.Clock.GetUtcNow().AddMinutes(15), expiresAt, TimeSpan.FromSeconds(1));
        Assert.True(expiresAt <= h.Clock.GetUtcNow().AddMinutes(15));
        Assert.EndsWith("Z", (string)body["expiresAt"]!);
        Assert.False((bool)body["exists"]!);

        using (var network = new HttpClient(new FakeNetworkHandler(h.S3)))
        using (var put = await network.PutAsync((string)body["url"]!, new ByteArrayContent(bytes)))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(1, h.S3.Puts);
        Assert.Equal(bytes, h.S3.Objects[key]);

        using var second = await h.BlobUrl(project, hash, bytes.Length, "PUT", token);
        Assert.True((bool)(await Harness.Json(second))!["exists"]!);

        using var largest = await h.BlobUrl(project, Harness.RandomHash(), FakeIdeaBosco.MaxPutBytes, "PUT", token); // exactly 2 GiB is allowed
        Assert.Equal(HttpStatusCode.OK, largest.StatusCode);
        Assert.Equal(3, h.Site.RequestCount(FakeIdeaBosco.BlobUrlPath));
    }

    [PostgresFact]
    public async Task BlobUrlGetNeedsTheHashInTheProjectsVersionsOrSideVersions()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var (project, _, student) = await h.SeedProjectAsync();
        var (other, otherMentor, _) = await h.SeedProjectAsync();
        var token = h.Supabase.IssueSession(student).AccessToken;
        var bytes = Encoding.UTF8.GetBytes("v1");
        var version = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var side = Harness.RandomHash();
        var elsewhere = Harness.RandomHash();
        await h.AddVersionsAsync(project, version, side);
        await h.AddVersionsAsync(other, elsewhere, Harness.RandomHash());

        using (var get = await h.BlobUrl(project, version, 2, "GET", token))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            var body = (await Harness.Json(get))!;
            Assert.StartsWith($"https://fake-s3.armory.test/{ContentObjectKey.FromHash(version)}?X-Amz-Algorithm=", (string?)body["url"]);
            Assert.Contains("&X-Amz-SignedHeaders=host&", (string?)body["url"]);
            Assert.False((bool)body["exists"]!);
        }
        h.S3.Objects[ContentObjectKey.FromHash(version)] = bytes;
        using (var get = await h.BlobUrl(project, version, 2, "GET", token))
        {
            var body = (await Harness.Json(get))!;
            Assert.True((bool)body["exists"]!);
            using var network = new HttpClient(new FakeNetworkHandler(h.S3));
            Assert.Equal(bytes, await network.GetByteArrayAsync((string)body["url"]!));
        }
        using (var sideGet = await h.BlobUrl(project, side, 3, "GET", token)) Assert.Equal(HttpStatusCode.OK, sideGet.StatusCode);
        using (var missing = await h.BlobUrl(project, Harness.RandomHash(), 3, "GET", token)) await AssertError(missing, HttpStatusCode.Forbidden);
        using (var foreign = await h.BlobUrl(project, elsewhere, 3, "GET", token)) await AssertError(foreign, HttpStatusCode.Forbidden);
        var otherToken = h.Supabase.IssueSession(otherMentor).AccessToken;
        using (var own = await h.BlobUrl(other, elsewhere, 3, "GET", otherToken)) Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [PostgresFact]
    public async Task BlobUrlRejectsBadBodiesWith400()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var (project, _, student) = await h.SeedProjectAsync();
        var auth = "Bearer " + h.Supabase.IssueSession(student).AccessToken;
        var hash = Harness.RandomHash();
        string Body(object? projectId = null, object? hashValue = null, object? bytes = null, object? method = null, string? omit = null)
        {
            var body = new JsonObject
            {
                ["projectId"] = JsonValue.Create(projectId ?? project.ToString()),
                ["hash"] = JsonValue.Create(hashValue ?? hash),
                ["bytes"] = JsonValue.Create(bytes ?? 10),
                ["method"] = JsonValue.Create(method ?? "PUT"),
            };
            if (omit is not null) body.Remove(omit);
            return body.ToJsonString();
        }

        using (var ok = await h.BlobUrl(Body(), auth)) Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var bad = new[]
        {
            "not json", "[]", "\"text\"",
            Body(projectId: "nope"), Body(projectId: 42), Body(omit: "projectId"),
            Body(hashValue: hash.ToUpperInvariant()), Body(hashValue: hash[..63]), Body(hashValue: hash + "a"),
            Body(hashValue: new string('g', 64)), Body(hashValue: hash + "\n"), Body(hashValue: 7), Body(omit: "hash"),
            Body(bytes: -1), Body(bytes: 1.5), Body(bytes: "10"), Body(bytes: true), Body(omit: "bytes"),
            Body(method: "POST"), Body(method: "put"), Body(method: 1), Body(omit: "method"),
            Body(bytes: FakeIdeaBosco.MaxPutBytes + 1),
        };
        foreach (var json in bad)
        {
            using var response = await h.BlobUrl(json, auth);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{json} answered {(int)response.StatusCode}");
        }
        using (var bigGet = await h.BlobUrl(Body(bytes: FakeIdeaBosco.MaxPutBytes + 1, method: "GET"), auth))
            await AssertError(bigGet, HttpStatusCode.Forbidden); // the size limit is for PUT; this GET fails only the project check
    }

    [PostgresFact]
    public async Task BlobUrlNeedsALiveToken401()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var (project, _, student) = await h.SeedProjectAsync();
        var json = new JsonObject { ["projectId"] = project.ToString(), ["hash"] = Harness.RandomHash(), ["bytes"] = 1, ["method"] = "PUT" }.ToJsonString();
        var expired = h.Supabase.IssueSession(student);
        h.Supabase.ExpireAccessToken(expired.AccessToken);
        var timedOut = h.Supabase.IssueSession(student, TimeSpan.FromMinutes(1));
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        foreach (var authorization in new[] { null, "Bearer access-never-issued", "Basic abc", "Bearer " + expired.AccessToken, "Bearer " + timedOut.AccessToken, "Bearer " + h.Supabase.AnonKey })
        {
            using var response = await h.BlobUrl(json, authorization);
            await AssertError(response, HttpStatusCode.Unauthorized);
        }
        using var unauthenticatedGarbage = await h.BlobUrl("not json", null); // 401 comes before 400
        await AssertError(unauthenticatedGarbage, HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task BlobUrlRefusesNonMembers403()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var (project, _, _) = await h.SeedProjectAsync();
        var outsider = h.Supabase.IssueSession(Harness.Email("outsider")).AccessToken;
        using (var put = await h.BlobUrl(project, Harness.RandomHash(), 1, "PUT", outsider)) await AssertError(put, HttpStatusCode.Forbidden);
        using (var noProject = await h.BlobUrl(Guid.NewGuid(), Harness.RandomHash(), 1, "PUT", outsider)) await AssertError(noProject, HttpStatusCode.Forbidden);
    }

    [PostgresFact]
    public async Task BlobUrlAnswers503WhenStorageIsNotConfigured()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var (project, _, student) = await h.SeedProjectAsync();
        h.Site.StorageConfigured = false;
        using var response = await h.BlobUrl(project, Harness.RandomHash(), 1, "PUT", h.Supabase.IssueSession(student).AccessToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("""{"error":"armory_storage_not_configured"}""", await response.Content.ReadAsStringAsync());
    }

    // ---- Section 3: connecting a computer ----

    [PostgresFact]
    public async Task ConnectPageAsksToConnectTheDeviceAsTheSignedInUser()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var email = Harness.Email("student");
        var url = h.Site.ConnectUrl(50000, Pkce.NewSecret(), FakeIdeaBosco.ChallengeFor(Pkce.NewSecret()), "Lab <PC> 3");
        using (var signedIn = new HttpRequestMessage(HttpMethod.Get, url))
        {
            signedIn.Headers.Add(FakeIdeaBosco.SiteUserHeader, email);
            using var page = await h.Http.SendAsync(signedIn);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains($"Connect Lab &lt;PC&gt; 3 to Armory as {email}?", html);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        }
        using (var anonymous = await h.Http.GetAsync(url)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var badLink = new HttpRequestMessage(HttpMethod.Get, h.Site.ConnectUrl(80, "s", "c", "d")))
        {
            badLink.Headers.Add(FakeIdeaBosco.SiteUserHeader, email);
            using var page = await h.Http.SendAsync(badLink);
            Assert.Equal(HttpStatusCode.BadRequest, page.StatusCode);
        }
    }

    [PostgresFact]
    public async Task ConnectStartRedirectsToTheLoopbackCallbackWithAHashedOneTimeCode()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var state = Pkce.NewSecret();
        using var response = await h.Start(51234, state, FakeIdeaBosco.ChallengeFor(Pkce.NewSecret()), "Lab PC 3", Harness.Email("student"));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("http", location.Scheme);
        Assert.Equal("127.0.0.1", location.Host);
        Assert.Equal(51234, location.Port);
        Assert.Equal("/callback", location.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        Assert.Equal(state, query["state"]);
        var code = query["code"]!;
        Assert.Matches("^[A-Za-z0-9_-]{43}$", code);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code))), Assert.Single(h.Site.StoredCodeHashes));
        Assert.DoesNotContain(code, h.Site.StoredCodeHashes);
    }

    [PostgresFact]
    public async Task ConnectStartRejectsBadInputWith400()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        h.Site.RateLimits.StartPerIp = 1000;
        h.Site.RateLimits.StartPerUser = 1000;
        var email = Harness.Email("student");
        var challenge = FakeIdeaBosco.ChallengeFor(Pkce.NewSecret());
        string Body(object? port = null, object? state = null, object? challengeValue = null, object? device = null, string? omit = null)
        {
            var body = new JsonObject
            {
                ["port"] = JsonValue.Create(port ?? 50000),
                ["state"] = JsonValue.Create(state ?? "state_-1"),
                ["challenge"] = JsonValue.Create(challengeValue ?? challenge),
                ["device"] = JsonValue.Create(device ?? "Lab PC 3"),
            };
            if (omit is not null) body.Remove(omit);
            return body.ToJsonString();
        }

        foreach (var good in new[] { Body(port: 1024), Body(port: 65535), Body(port: "50000"), Body(state: new string('a', 256)), Body(device: new string('d', 100)) })
        {
            using var response = await h.Start(good, email);
            Assert.True(response.StatusCode == HttpStatusCode.SeeOther, $"{good} answered {(int)response.StatusCode}");
        }
        var bad = new[]
        {
            "not json", "[]",
            Body(port: 1023), Body(port: 65536), Body(port: 0), Body(port: -1), Body(port: 1.5), Body(port: "abc"), Body(port: true), Body(omit: "port"),
            Body(state: ""), Body(state: new string('a', 257)), Body(state: "has space"), Body(state: "a+b"), Body(state: "abc="), Body(state: "abc\n"), Body(state: 5), Body(omit: "state"),
            Body(challengeValue: challenge[..42]), Body(challengeValue: challenge + "A"), Body(challengeValue: "+" + challenge[1..]), Body(challengeValue: 1), Body(omit: "challenge"),
            Body(device: ""), Body(device: "   "), Body(device: new string('d', 101)), Body(device: 3), Body(omit: "device"),
        };
        foreach (var json in bad)
        {
            using var response = await h.Start(json, email);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{json} answered {(int)response.StatusCode}");
        }
        Assert.Equal(5, h.Site.StoredCodeHashes.Count);
    }

    [PostgresFact]
    public async Task ConnectStartRequiresSignIn401()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        using var response = await h.Start(50000, Pkce.NewSecret(), FakeIdeaBosco.ChallengeFor(Pkce.NewSecret()), "Lab PC", siteUser: null!);
        await AssertError(response, HttpStatusCode.Unauthorized);
        using var blank = await h.Start(50000, Pkce.NewSecret(), FakeIdeaBosco.ChallengeFor(Pkce.NewSecret()), "Lab PC", siteUser: " ");
        await AssertError(blank, HttpStatusCode.Unauthorized);
        Assert.Empty(h.Site.StoredCodeHashes);
    }

    [PostgresFact]
    public async Task ConnectStartIsRateLimitedPerIpAndPerUser429()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var challenge = FakeIdeaBosco.ChallengeFor(Pkce.NewSecret());
        async Task<HttpStatusCode> Start(string user, string? ip = null)
        {
            using var response = await h.Start(50000, Pkce.NewSecret(), challenge, "Lab PC", user, ip);
            return response.StatusCode;
        }

        var busy = Harness.Email("busy");
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.SeeOther, await Start(busy)); // default: 10 a minute
        Assert.Equal((HttpStatusCode)429, await Start(busy));

        h.Site.RateLimits.StartPerIp = 1000;
        h.Site.RateLimits.StartPerUser = 2;
        var user = Harness.Email("user");
        Assert.Equal(HttpStatusCode.SeeOther, await Start(user));
        Assert.Equal(HttpStatusCode.SeeOther, await Start(user));
        Assert.Equal((HttpStatusCode)429, await Start(user, "10.0.0.9")); // per user, from any address
        Assert.Equal(HttpStatusCode.SeeOther, await Start(Harness.Email("someone")));

        h.Site.RateLimits.StartPerIp = 2;
        h.Site.RateLimits.StartPerUser = 1000;
        Assert.Equal(HttpStatusCode.SeeOther, await Start(Harness.Email("a"), "10.0.0.1"));
        Assert.Equal(HttpStatusCode.SeeOther, await Start(Harness.Email("b"), "10.0.0.1"));
        Assert.Equal((HttpStatusCode)429, await Start(Harness.Email("c"), "10.0.0.1")); // per address, for any user
        Assert.Equal(HttpStatusCode.SeeOther, await Start(Harness.Email("d"), "10.0.0.2"));

        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.SeeOther, await Start(Harness.Email("e"), "10.0.0.1"));
        Assert.Equal(HttpStatusCode.SeeOther, await Start(user, "10.0.0.3"));
    }

    [PostgresFact]
    public async Task ExchangeMintsAnIndependentSessionAndRegistersTheDevice()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var email = Harness.Email("student");
        var browserSession = h.Supabase.IssueSession(email);
        using var callback = new LoopbackCallback();
        var state = Pkce.NewSecret();
        var verifier = Pkce.NewSecret();
        var connectUrl = h.Site.ConnectUrl(callback.Port, state, FakeIdeaBosco.ChallengeFor(verifier), "Lab PC 3");

        using var browser = new FakeBrowser();
        var seen = await browser.SignInAndApproveAsync(connectUrl, email);
        Assert.Equal(HttpStatusCode.OK, seen.Status);
        Assert.Equal(HttpStatusCode.SeeOther, seen.StartStatus);
        Assert.Equal("This computer is connected. You can close this tab.", seen.Body);
        var received = await callback.Received;
        Assert.Equal(seen.CallbackUri, received);
        var query = System.Web.HttpUtility.ParseQueryString(received.Query);
        Assert.Equal(state, query["state"]);

        using var response = await h.ExchangeCode(query["code"]!, verifier);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await Harness.Json(response))!.AsObject();
        Assert.Equal(["access_token", "anon_key", "device_id", "email", "expires_at", "refresh_token", "supabase_url"], body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(email, (string?)body["email"]);
        Assert.Equal(h.Supabase.AnonKey, (string?)body["anon_key"]);
        Assert.Equal(h.Supabase.BaseUri.ToString().TrimEnd('/'), (string?)body["supabase_url"]);
        Assert.False(((string)body["supabase_url"]!).EndsWith('/'));
        Assert.Equal(h.Clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds(), (long)body["expires_at"]!);
        Assert.NotEqual(browserSession.AccessToken, (string?)body["access_token"]);

        await using (var connection = await h.Database.OpenAsync())
        await using (var reader = await Harness.Command(connection, "select owner_email, name from armory_devices where id=$1", Guid.Parse((string)body["device_id"]!)).ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(email, reader.GetString(0));
            Assert.Equal("Lab PC 3", reader.GetString(1));
        }

        using (var rpc = await h.Rpc("armory_my_projects", "{}", (string)body["access_token"]!)) Assert.Equal(HttpStatusCode.OK, rpc.StatusCode);
        using (var refreshed = await h.RefreshToken((string)body["refresh_token"]!)) Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        // The browser's own session is independent: neither logs the other out.
        Assert.Equal(email, h.Supabase.EmailForAccessToken(browserSession.AccessToken));
        using (var browserRefresh = await h.RefreshToken(browserSession.RefreshToken)) Assert.Equal(HttpStatusCode.OK, browserRefresh.StatusCode);
    }

    [PostgresFact]
    public async Task ExchangeRejectsBadInput400()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        foreach (var json in new[] { "not json", "[]", "{}", """{"code":"abc"}""", """{"verifier":"abc"}""", """{"code":1,"verifier":"abc"}""", """{"code":"abc","verifier":null}""", """{"code":"","verifier":"abc"}""", """{"code":"abc","verifier":""}""" })
        {
            using var response = await h.Exchange(json);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{json} answered {(int)response.StatusCode}");
        }
    }

    [PostgresFact]
    public async Task ExchangeRefusesUnknownReusedAndWrongVerifierCodes401()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var email = Harness.Email("student");
        using (var unknown = await h.ExchangeCode(Pkce.NewSecret(), Pkce.NewSecret())) await AssertError(unknown, HttpStatusCode.Unauthorized);

        var (code, verifier) = await h.IssueCodeAsync(email);
        using (var first = await h.ExchangeCode(code, verifier)) Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var reused = await h.ExchangeCode(code, verifier)) await AssertError(reused, HttpStatusCode.Unauthorized);

        var (guarded, rightVerifier) = await h.IssueCodeAsync(email);
        using (var wrong = await h.ExchangeCode(guarded, Pkce.NewSecret())) await AssertError(wrong, HttpStatusCode.Unauthorized);
        using (var late = await h.ExchangeCode(guarded, rightVerifier)) await AssertError(late, HttpStatusCode.Unauthorized); // consumed by the wrong guess

        await using var connection = await h.Database.OpenAsync();
        Assert.Equal(1L, (long)(await Harness.Command(connection, "select count(*) from armory_devices where owner_email=$1", email).ExecuteScalarAsync())!);
    }

    [PostgresFact]
    public async Task ExpiredCodesAre410EvenWithAWrongVerifier()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var email = Harness.Email("student");
        var (code, verifier) = await h.IssueCodeAsync(email);
        var (other, _) = await h.IssueCodeAsync(email);
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        using (var wrong = await h.ExchangeCode(other, Pkce.NewSecret())) await AssertError(wrong, HttpStatusCode.Gone, "code_expired");
        using (var expired = await h.ExchangeCode(code, verifier)) await AssertError(expired, HttpStatusCode.Gone, "code_expired");
        using (var unknown = await h.ExchangeCode(Pkce.NewSecret(), verifier)) await AssertError(unknown, HttpStatusCode.Unauthorized);

        var (fresh, freshVerifier) = await h.IssueCodeAsync(email);
        h.Clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        using var inTime = await h.ExchangeCode(fresh, freshVerifier);
        Assert.Equal(HttpStatusCode.OK, inTime.StatusCode);
    }

    [PostgresFact]
    public async Task ExchangeIsRateLimitedPerIp429()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        h.Site.RateLimits.ExchangePerIp = 2;
        var json = new JsonObject { ["code"] = "nope", ["verifier"] = "nope" }.ToJsonString();
        using (var a = await h.Exchange(json)) Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode);
        using (var b = await h.Exchange("{}")) Assert.Equal(HttpStatusCode.BadRequest, b.StatusCode);
        using (var c = await h.Exchange(json)) await AssertError(c, (HttpStatusCode)429, "rate_limited");
        using (var d = await h.Exchange(json, clientIp: "10.0.0.7")) Assert.Equal(HttpStatusCode.Unauthorized, d.StatusCode);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        using var e = await h.Exchange(json);
        Assert.Equal(HttpStatusCode.Unauthorized, e.StatusCode);
    }

    [PostgresFact]
    public async Task OfflineSiteAbortsConnectionsAndCountsRequests()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        h.Site.Offline = true;
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => h.Exchange("{}"));
        h.Site.Offline = false;
        using var back = await h.Exchange("{}");
        Assert.Equal(HttpStatusCode.BadRequest, back.StatusCode);
        Assert.Equal(2, h.Site.RequestCount(FakeIdeaBosco.ConnectExchangePath));
    }

    [PostgresFact]
    public async Task FakeBrowserReportsTheStepThatFailed()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        using var browser = new FakeBrowser();
        var badPort = await browser.SignInAndApproveAsync(h.Site.ConnectUrl(80, Pkce.NewSecret(), FakeIdeaBosco.ChallengeFor(Pkce.NewSecret()), "Lab PC"), Harness.Email("student"));
        Assert.Equal(HttpStatusCode.BadRequest, badPort.Status);
        Assert.Null(badPort.CallbackUri);

        h.Site.RateLimits.StartPerUser = 0;
        var limited = await browser.SignInAndApproveAsync(h.Site.ConnectUrl(50000, Pkce.NewSecret(), FakeIdeaBosco.ChallengeFor(Pkce.NewSecret()), "Lab PC"), Harness.Email("student"));
        Assert.Equal((HttpStatusCode)429, limited.Status);
        Assert.Equal((HttpStatusCode)429, limited.StartStatus);
        Assert.Null(limited.CallbackUri);
    }

    [PostgresFact]
    public async Task FakeNetworkHandlerRoutesS3AndPassesEverythingElseThrough()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var key = ContentObjectKey.FromHash(Harness.RandomHash());

        // A FakeS3 with no site is public, as Armory.Storage.Tests uses it.
        using var publicS3 = new FakeS3();
        using (var open = new HttpClient(new FakeNetworkHandler(publicS3)))
        using (var head = new HttpRequestMessage(HttpMethod.Head, $"https://{FakeNetworkHandler.S3Host}/{key}"))
        using (var missing = await open.SendAsync(head))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(1, publicS3.Heads);

        // The site's FakeS3 is private: an unsigned request never reaches it.
        using var network = new HttpClient(new FakeNetworkHandler(h.S3));
        using (var head = new HttpRequestMessage(HttpMethod.Head, $"https://{FakeNetworkHandler.S3Host}/{key}"))
        using (var refused = await network.SendAsync(head))
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using (var put = await network.PutAsync($"https://{FakeNetworkHandler.S3Host}/{key}", new ByteArrayContent([1, 2, 3])))
            await AssertS3Error(put, HttpStatusCode.Forbidden, "AccessDenied");
        Assert.Equal((0, 0), (h.S3.Heads, h.S3.Puts));
        Assert.Empty(h.S3.Objects);

        using var passthrough = await network.GetAsync(new Uri(h.Supabase.BaseUri, "nowhere"));
        Assert.Equal(HttpStatusCode.NotFound, passthrough.StatusCode);
        Assert.Equal(1, h.Supabase.RequestCount("/nowhere"));
    }

    private static async Task AssertS3Error(HttpResponseMessage response, HttpStatusCode status, string code, string? message = null)
    {
        Assert.Equal(status, response.StatusCode);
        var error = System.Xml.Linq.XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!;
        Assert.Equal("Error", error.Name.LocalName);
        Assert.Equal(code, error.Element("Code")!.Value);
        if (message is not null) Assert.Equal(message, error.Element("Message")!.Value);
    }

    private static async Task<string> SignedUrlAsync(Harness h, Guid project, string hash, long bytes, string method, string token)
    {
        using var response = await h.BlobUrl(project, hash, bytes, method, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string)(await Harness.Json(response))!["url"]!;
    }

    [PostgresFact]
    public async Task BlobUrlsAreSignedForOneMethodObjectAndLength()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var (project, _, student) = await h.SeedProjectAsync();
        var token = h.Supabase.IssueSession(student).AccessToken;
        using var network = new HttpClient(new FakeNetworkHandler(h.S3));
        var bytes = Encoding.UTF8.GetBytes("signed bytes");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var put = await SignedUrlAsync(h, project, hash, bytes.Length, "PUT", token);

        // The PUT URL does not read, and a GET or HEAD is a different signature.
        using (var get = await network.GetAsync(put)) await AssertS3Error(get, HttpStatusCode.Forbidden, "SignatureDoesNotMatch");
        using (var head = await network.SendAsync(new HttpRequestMessage(HttpMethod.Head, put))) Assert.Equal(HttpStatusCode.Forbidden, head.StatusCode);
        // Another object, a longer life, or a different length all break the signature.
        var otherKey = put.Replace(hash, Harness.RandomHash(), StringComparison.Ordinal);
        using (var elsewhere = await network.PutAsync(otherKey, new ByteArrayContent(bytes))) await AssertS3Error(elsewhere, HttpStatusCode.Forbidden, "SignatureDoesNotMatch");
        using (var longer = await network.PutAsync(put.Replace("X-Amz-Expires=900", "X-Amz-Expires=86400", StringComparison.Ordinal), new ByteArrayContent(bytes)))
            await AssertS3Error(longer, HttpStatusCode.Forbidden, "SignatureDoesNotMatch");
        using (var bigger = await network.PutAsync(put, new ByteArrayContent([.. bytes, 0]))) await AssertS3Error(bigger, HttpStatusCode.Forbidden, "SignatureDoesNotMatch");
        using (var unsigned = await network.PutAsync(put[..put.IndexOf('?')], new ByteArrayContent(bytes))) await AssertS3Error(unsigned, HttpStatusCode.Forbidden, "AccessDenied");
        using (var forged = await network.PutAsync(put[..^1] + (put[^1] == '0' ? "1" : "0"), new ByteArrayContent(bytes))) await AssertS3Error(forged, HttpStatusCode.Forbidden, "SignatureDoesNotMatch");

        // Streaming without a length, or a body that is not the declared length, is refused.
        var chunked = new StreamContent(new NonSeekableStream(bytes));
        using (var noLength = await network.PutAsync(put, chunked)) await AssertS3Error(noLength, HttpStatusCode.LengthRequired, "MissingContentLength");
        var lying = new StreamContent(new MemoryStream([.. bytes, 1, 2]));
        lying.Headers.ContentLength = bytes.Length;
        using (var mismatch = await network.PutAsync(put, lying)) await AssertS3Error(mismatch, HttpStatusCode.BadRequest, "IncompleteBody");
        Assert.Equal(0, h.S3.Puts);
        Assert.Empty(h.S3.Objects);

        using (var stored = await network.PutAsync(put, new ByteArrayContent(bytes))) Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        Assert.Equal(bytes, h.S3.Objects[ContentObjectKey.FromHash(hash)]);

        // A URL from another site's bucket is not this bucket's credential.
        await using var otherSupabase = new FakeSupabase(h.Database);
        using var otherS3 = new FakeS3();
        await using var otherSite = new FakeIdeaBosco(otherSupabase, h.Database, otherS3);
        using var otherNetwork = new HttpClient(new FakeNetworkHandler(otherS3));
        using (var foreign = await otherNetwork.PutAsync(put, new ByteArrayContent(bytes))) await AssertS3Error(foreign, HttpStatusCode.Forbidden, "InvalidAccessKeyId");
    }

    [PostgresFact]
    public async Task BlobUrlsExpire15MinutesAfterIssue()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        h.Clock.Set(new DateTimeOffset(2026, 10, 1, 12, 0, 0, 400, TimeSpan.Zero));
        var (project, _, student) = await h.SeedProjectAsync();
        var token = h.Supabase.IssueSession(student, TimeSpan.FromHours(2)).AccessToken;
        using var network = new HttpClient(new FakeNetworkHandler(h.S3));
        var bytes = Encoding.UTF8.GetBytes("expiring");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var answer = await h.BlobUrl(project, hash, bytes.Length, "PUT", token);
        var body = (await Harness.Json(answer))!;
        Assert.Equal("2026-10-01T12:15:00.000Z", (string?)body["expiresAt"]); // whole seconds, like the SigV4 date
        var put = (string)body["url"]!;
        Assert.Contains("X-Amz-Date=20261001T120000Z", put);

        h.Clock.Set(new DateTimeOffset(2026, 10, 1, 12, 15, 0, TimeSpan.Zero));
        using (var late = await network.PutAsync(put, new ByteArrayContent(bytes))) await AssertS3Error(late, HttpStatusCode.Forbidden, "AccessDenied", "Request has expired");
        h.Clock.Set(new DateTimeOffset(2026, 10, 1, 12, 14, 59, 999, TimeSpan.Zero));
        using (var inTime = await network.PutAsync(put, new ByteArrayContent(bytes))) Assert.Equal(HttpStatusCode.OK, inTime.StatusCode);
        Assert.Equal(1, h.S3.Puts);

        await h.AddVersionsAsync(project, hash, Harness.RandomHash());
        var get = await SignedUrlAsync(h, project, hash, bytes.Length, "GET", token);
        h.Clock.Advance(TimeSpan.FromMinutes(15));
        using (var stale = await network.GetAsync(get)) await AssertS3Error(stale, HttpStatusCode.Forbidden, "AccessDenied", "Request has expired");
        using (var fresh = await network.GetAsync(await SignedUrlAsync(h, project, hash, bytes.Length, "GET", token))) Assert.Equal(bytes, await fresh.Content.ReadAsByteArrayAsync());
    }

    [PostgresFact]
    public async Task BlobUrlHeadersAreSignedAndMustBeSent()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        var (project, _, student) = await h.SeedProjectAsync();
        var token = h.Supabase.IssueSession(student).AccessToken;
        h.Site.BlobUrlHeaders["x-amz-meta-armory"] = "v1";
        h.Site.BlobUrlHeaders["Content-Type"] = "application/octet-stream";
        var bytes = Encoding.UTF8.GetBytes("with headers");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var answer = await h.BlobUrl(project, hash, bytes.Length, "PUT", token);
        var body = (await Harness.Json(answer))!;
        Assert.Equal("v1", (string?)body["headers"]!["x-amz-meta-armory"]);
        var put = (string)body["url"]!;
        Assert.Contains("X-Amz-SignedHeaders=content-length%3Bcontent-type%3Bhost%3Bx-amz-meta-armory&", put);

        using var network = new HttpClient(new FakeNetworkHandler(h.S3));
        using (var bare = await network.PutAsync(put, new ByteArrayContent(bytes))) await AssertS3Error(bare, HttpStatusCode.Forbidden, "SignatureDoesNotMatch");
        var content = new ByteArrayContent(bytes);
        content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Put, put) { Content = content };
        request.Headers.TryAddWithoutValidation("x-amz-meta-armory", "v1");
        using var sent = await network.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal(1, h.S3.Puts);
    }

    [Fact]
    public async Task FakeS3KeepsEachMultipartUploadsPartsApart()
    {
        using var s3 = new FakeS3();
        using var http = new HttpMessageInvoker(s3, disposeHandler: false);
        async Task<string> Create(string key)
        {
            using var created = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"https://fake/{key}?uploads"), default);
            return System.Xml.Linq.XDocument.Parse(await created.Content.ReadAsStringAsync()).Root!.Element("UploadId")!.Value;
        }
        async Task Part(string key, string upload, int number, byte[] bytes)
        {
            using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"https://fake/{key}?uploadId={upload}&partNumber={number}") { Content = new ByteArrayContent(bytes) }, default);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        async Task Complete(string key, string upload)
        {
            using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"https://fake/{key}?uploadId={upload}&complete=1"), default);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Two agents upload at once, interleaved.
        var a = await Create("a");
        var b = await Create("b");
        Assert.NotEqual(a, b);
        await Part("a", a, 1, [1, 1]);
        await Part("b", b, 1, [2]);
        await Part("a", a, 2, [1]);
        await Part("b", b, 2, [2, 2]);
        await Part("b", b, 3, [2]);
        await Complete("a", a);
        await Complete("b", b);
        Assert.Equal([1, 1, 1], s3.Objects["a"]);
        Assert.Equal([2, 2, 2, 2], s3.Objects["b"]);

        // A later, smaller upload of a key gets none of the earlier parts.
        var c = await Create("a");
        await Part("a", c, 1, [3]);
        await Complete("a", c);
        Assert.Equal([3], s3.Objects["a"]);
        Assert.Equal(0, s3.OpenUploads);

        var aborted = await Create("d");
        await Part("d", aborted, 1, [4]);
        using (await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"https://fake/d?uploadId={aborted}"), default)) { }
        Assert.Equal(0, s3.OpenUploads);
        using var gone = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"https://fake/d?uploadId={aborted}&complete=1"), default);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.False(s3.Objects.ContainsKey("d"));
    }

    [PostgresFact]
    public async Task ExchangeIsRateLimitedPerUserOfTheCode429()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        h.Site.RateLimits.ExchangePerUser = 2;
        var busy = Harness.Email("busy");
        var codes = new List<(string Code, string Verifier)>();
        for (var i = 0; i < 4; i++) codes.Add(await h.IssueCodeAsync(busy));
        var other = await h.IssueCodeAsync(Harness.Email("other"));

        // Each exchange comes from a different address, so only the per-user limit applies.
        async Task<HttpResponseMessage> Exchange((string Code, string Verifier) grant, string ip) =>
            await h.Exchange(new JsonObject { ["code"] = grant.Code, ["verifier"] = grant.Verifier }.ToJsonString(), ip);
        using (var first = await Exchange(codes[0], "10.1.0.1")) Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var second = await Exchange(codes[1], "10.1.0.2")) Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using (var third = await Exchange(codes[2], "10.1.0.3")) await AssertError(third, (HttpStatusCode)429, "rate_limited");
        using (var someoneElse = await Exchange(other, "10.1.0.4")) Assert.Equal(HttpStatusCode.OK, someoneElse.StatusCode);
        using (var unknown = await Exchange((Pkce.NewSecret(), Pkce.NewSecret()), "10.1.0.5")) await AssertError(unknown, HttpStatusCode.Unauthorized);

        h.Clock.Advance(TimeSpan.FromMinutes(1));
        using (var later = await Exchange(codes[2], "10.1.0.6")) Assert.Equal(HttpStatusCode.OK, later.StatusCode); // a 429 did not consume the code
        using (var fourth = await Exchange(codes[3], "10.1.0.7")) Assert.Equal(HttpStatusCode.OK, fourth.StatusCode);
    }

    [PostgresFact]
    public async Task RacingExchangesOfOneCodeSucceedOnce()
    {
        await using var h = await Harness.StartAsync(fixture.Database);
        h.Site.RateLimits.ExchangePerIp = 1000;
        var email = Harness.Email("student");
        var (code, verifier) = await h.IssueCodeAsync(email);
        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.ExchangeCode(code, verifier)));
        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.Equal(7, answers.Count(a => a.StatusCode == HttpStatusCode.Unauthorized));
        foreach (var answer in answers) answer.Dispose();
        await using var connection = await h.Database.OpenAsync();
        Assert.Equal(1L, (long)(await Harness.Command(connection, "select count(*) from armory_devices where owner_email=$1", email).ExecuteScalarAsync())!);
    }
}

/// <summary>A stream with no length, so HttpClient would send it chunked.</summary>
internal sealed class NonSeekableStream(byte[] bytes) : Stream
{
    private readonly MemoryStream _inner = new(bytes);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
