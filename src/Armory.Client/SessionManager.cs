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
