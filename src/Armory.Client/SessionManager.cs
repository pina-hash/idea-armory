using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Armory.Client;

// Holds the signed-in session and refreshes it through Supabase's token endpoint.
// Refresh tokens rotate, so the new session is written to the secret store before the
// new access token is used. Refresh is single-flight.
public sealed class SessionManager
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    private readonly HttpClient http;
    private readonly ISecretStore secrets;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private ArmorySession? session;

    public SessionManager(HttpClient http, ISecretStore secrets, TimeProvider? clock = null)
    {
        this.http = http;
        this.secrets = secrets;
        this.clock = clock ?? TimeProvider.System;
        session = SessionStorage.Load(secrets);
    }

    public ArmorySession? Current => Volatile.Read(ref session);
    public bool IsSignedIn => Current is not null;
    public event Action? SignedOut;

    public void SignIn(ArmorySession signedIn)
    {
        SessionStorage.Save(secrets, signedIn);
        Volatile.Write(ref session, signedIn);
    }

    public void SignOut()
    {
        secrets.Delete(SessionStorage.Name);
        Volatile.Write(ref session, null);
        SignedOut?.Invoke();
    }

    // Removing a student from a shared computer (docs/agent/PROFILES.md): this sign-in ends on the
    // server too, as far as it can (Supabase's sign-out of this one session, scope=local, which
    // leaves the student's other computers signed in), then is forgotten here. Offline, refused or
    // slow (5 seconds at most): forgotten here all the same, and the session simply goes unused.
    // True when the server ended it.
    public async Task<bool> SignOutSessionAsync(CancellationToken cancellationToken = default)
    {
        var ended = false;
        if (Current is { } current)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                // The sign-out needs a live access token: an old one is renewed first.
                var live = current.ExpiresAt - clock.GetUtcNow() > RefreshMargin ? current : await GetFreshAsync(cancellationToken: deadline.Token);
                using var request = new HttpRequestMessage(HttpMethod.Post, live.SupabaseUrl.TrimEnd('/') + "/auth/v1/logout?scope=local");
                request.Headers.TryAddWithoutValidation("apikey", live.AnonKey);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", live.AccessToken);
                using var response = await http.SendAsync(request, deadline.Token);
                ended = response.IsSuccessStatusCode;
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ArmoryClientException) { }
        }
        if (Current is not null) SignOut();
        return ended;
    }

    // Returns a session whose access token is valid for at least the refresh margin.
    public async Task<ArmorySession> GetFreshAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var current = Current ?? throw new ArmorySignedOutException("This computer is not connected to Armory.");
        if (!forceRefresh && current.ExpiresAt - clock.GetUtcNow() > RefreshMargin) return current;
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            var latest = Current ?? throw new ArmorySignedOutException("This computer is not connected to Armory.");
            // Another caller refreshed while this one waited.
            if (!ReferenceEquals(latest, current) && latest.ExpiresAt - clock.GetUtcNow() > RefreshMargin) return latest;
            var refreshed = await RefreshAsync(latest, cancellationToken);
            SessionStorage.Save(secrets, refreshed);
            Volatile.Write(ref session, refreshed);
            return refreshed;
        }
        finally { refreshGate.Release(); }
    }

    private async Task<ArmorySession> RefreshAsync(ArmorySession current, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, current.SupabaseUrl.TrimEnd('/') + "/auth/v1/token?grant_type=refresh_token")
        {
            Content = JsonContent.Create(new Dictionary<string, string> { ["refresh_token"] = current.RefreshToken }),
        };
        request.Headers.TryAddWithoutValidation("apikey", current.AnonKey);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, cancellationToken); }
        catch (HttpRequestException error) { throw new ArmoryOfflineException("Armory could not be reached to renew the sign-in.", error); }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw new ArmoryOfflineException("Armory took too long to renew the sign-in.", error); }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                SignOut();
                throw new ArmorySignedOutException("This computer's Armory sign-in ended. Connect it again.");
            }
            if (!response.IsSuccessStatusCode) throw new ArmoryOfflineException($"Armory sign-in service answered {(int)response.StatusCode}.");
            TokenAnswer? answer;
            try { answer = await response.Content.ReadFromJsonAsync<TokenAnswer>(Json.Options, cancellationToken); }
            catch (JsonException error) { throw new ArmoryOfflineException("Armory sign-in service sent an unreadable answer.", error); }
            if (answer?.AccessToken is not { Length: > 0 } access || answer.RefreshToken is not { Length: > 0 } refresh)
                throw new ArmoryOfflineException("Armory sign-in service sent an incomplete answer.");
            var expires = answer.ExpiresAt is { } at ? DateTimeOffset.FromUnixTimeSeconds(at)
                : clock.GetUtcNow().AddSeconds(answer.ExpiresIn ?? 3600);
            return current with { AccessToken = access, RefreshToken = refresh, ExpiresAt = expires };
        }
    }

    private sealed record TokenAnswer(string? AccessToken, string? RefreshToken, long? ExpiresAt, long? ExpiresIn);
}
