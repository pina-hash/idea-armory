namespace Armory.Agent.Tests;

public sealed class RedactorTests
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI4YjBjM2E1ZSIsImVtYWlsIjoiYWxleEBib3Njb3RlY2guZWR1In0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk0";
    private const string Refresh = "v1.Mr5D9kZ2pQ8xWb7nL4sT0yHc";
    private const string Code = "Qm9zY29UZWNoNTY2OUFybW9yeUNvbm5lY3RDb2Rl1234AbCd";
    private const string Verifier = "dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk0aZ";
    // Random base64url tokens that happen to have no digit, or no upper-case letter.
    private const string NoDigits = "dBjftJeZhCVPmBqfKmuhbUJUxpqr_wWwgFWFOEjXkaZ";
    private const string LowerCase = "qm9zy29uzwnontu2ovfybw9yevnvbm5ly3rdb2rl1234";

    [Theory]
    [InlineData("access token " + Jwt + " expired")]
    [InlineData("Authorization: Bearer " + Jwt)]
    [InlineData("authorization=Bearer sk_live_" + Refresh)]
    [InlineData("{\"Authorization\":\"Bearer " + Refresh + "\"}")]
    [InlineData("calling with bearer " + Refresh + " now")]
    [InlineData("{\"access_token\":\"" + Jwt + "\",\"refresh_token\":\"" + Refresh + "\",\"expires_at\":1790000000}")]
    [InlineData("{\"refresh_token\": \"" + Refresh + "\"}")]
    [InlineData("refresh_token=" + Refresh + "&grant_type=refresh_token")]
    [InlineData("apikey: " + Refresh)]
    [InlineData("{\"anon_key\":\"" + Refresh + "\"}")]
    [InlineData("http://127.0.0.1:52011/callback?state=AbCdEfGh123&code=" + Code)]
    [InlineData("GET /callback?code=" + Refresh + "&state=x HTTP/1.1")]
    [InlineData("{\"code\":\"" + Refresh + "\",\"verifier\":\"" + Verifier + "\"}")]
    [InlineData("code_verifier=" + Verifier)]
    [InlineData("stray token " + Verifier + " in a message")]
    [InlineData("stray token " + NoDigits + " in a message")]
    [InlineData("stray token " + LowerCase + " in a message")]
    public void Secrets_are_removed(string line)
    {
        var scrubbed = Redactor.Scrub(line);
        Assert.Contains(Redactor.Mask, scrubbed);
        foreach (var secret in new[] { Jwt, Refresh, Code, Verifier, NoDigits, LowerCase, "eyJ", "sk_live_" })
            Assert.DoesNotContain(secret, scrubbed);
    }

    [Fact]
    public void An_exception_with_a_token_in_its_message_is_scrubbed()
    {
        var error = new InvalidOperationException("Request failed: {\"refresh_token\":\"" + Refresh + "\"} with Bearer " + Jwt);
        var scrubbed = Redactor.Scrub(error.ToString());
        Assert.DoesNotContain(Refresh, scrubbed);
        Assert.DoesNotContain(Jwt, scrubbed);
        Assert.Contains("InvalidOperationException", scrubbed);
    }

    [Theory]
    [InlineData("Everything is saved to Armory.")]
    [InlineData("started 0.1.0")]
    [InlineData("vault runtime started at C:\\IDEA\\Armory")]
    [InlineData("Uploaded Robot 2027/Drivetrain/Gearbox.SLDASM to project 8b0c3a5e-1f2d-4c6b-9a7e-2d3c4b5a6f70")]
    [InlineData("hash 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08 matched")]
    [InlineData("connect: signed in, ArmorySession { Email = alex@boscotech.edu, DeviceId = 8b0c3a5e-1f2d-4c6b-9a7e-2d3c4b5a6f70, DeviceName = CAD-LAB-07, tokens redacted }")]
    [InlineData("The file is open in SLDWORKS. Close it in SolidWorks to get the newer version.")]
    [InlineData("WebView2 runtime 141.0.3537.71")]
    public void Ordinary_text_is_untouched(string line) => Assert.Equal(line, Redactor.Scrub(line));

    [Fact]
    public void A_session_logged_through_its_ToString_keeps_its_tokens_out()
    {
        var session = new Armory.Client.ArmorySession("https://project.supabase.co", Refresh, Jwt, Refresh, DateTimeOffset.UnixEpoch, "alex@boscotech.edu", Guid.Empty, "CAD-LAB-07");
        var scrubbed = Redactor.Scrub("connect: signed in, " + session);
        Assert.DoesNotContain(Jwt, scrubbed);
        Assert.DoesNotContain(Refresh, scrubbed);
        Assert.Contains("alex@boscotech.edu", scrubbed);
    }

    [Fact]
    public void Empty_and_null_text_stay_empty()
    {
        Assert.Equal("", Redactor.Scrub(null));
        Assert.Equal("", Redactor.Scrub(""));
    }
}
