using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfinityUrban.Web;

public sealed class SupabaseAuth(HttpClient http, IConfiguration configuration)
{
    public bool IsConfigured => ValidConfiguration(configuration["Supabase:Url"], configuration["Supabase:PublicKey"]);

    public static bool ValidConfiguration(string? url, string? key)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            return false;
        if (key?.StartsWith("sb_publishable_", StringComparison.Ordinal) == true) return key.Length > 20;
        // Legacy keys must explicitly be anon. Privileged keys are never accepted here.
        try
        {
            var payload = key!.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            return json.RootElement.GetProperty("role").GetString() == "anon";
        }
        catch { return false; }
    }

    public async Task<AuthSession?> SignIn(string email, string password, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Post, "token?grant_type=password", new { email, password }, null, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.UnprocessableEntity) return null;
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<AuthSession>(ct);
        if (session is null || string.IsNullOrWhiteSpace(session.AccessToken) || session.ExpiresIn <= 0) return null;
        // Obtain identity from the Auth service, never from unverified browser claims.
        var user = await GetUser(session.AccessToken, ct);
        return user is null ? null : session with { User = user };
    }

    public async Task Register(string name,string email,string password,CancellationToken ct)
    {
        // Only a display name is accepted as metadata. Approval and roles remain database-controlled.
        var redirect="https://app.urbaninfinity.com.br/login";
        using var response=await Send(HttpMethod.Post,"signup?redirect_to="+Uri.EscapeDataString(redirect),new{email,password,data=new{full_name=name}},null,ct);
        if(!response.IsSuccessStatusCode)
        {
            if((int)response.StatusCode==429)throw new PortalError(429,"Muitas tentativas de cadastro. Aguarde alguns minutos antes de tentar novamente.");
            JsonDocument error;
            try{error=await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),cancellationToken:ct);}
            catch(JsonException){throw new PortalError(503,"O serviço de cadastro não confirmou a solicitação. Tente novamente mais tarde.");}
            using var errorDocument=error;
            var code=error.RootElement.TryGetProperty("code",out var value)?value.GetString():null;
            if(code is "user_already_exists" or "email_exists")return; // Same public response prevents account enumeration.
            if(code=="weak_password")throw new PortalError(400,"A senha não atende aos requisitos. Use uma senha mais longa, com letras, números e símbolos.");
            if(code=="email_address_invalid")throw new PortalError(400,"Informe um email válido para o cadastro.");
            throw new PortalError(503,"O serviço de cadastro não confirmou a solicitação. Tente novamente mais tarde.");
        }
        using var result=await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),cancellationToken:ct);
        // Supabase can return a session when email confirmation is disabled. Never give that
        // session to the browser or bypass the existing approved-profile check.
        if(result.RootElement.TryGetProperty("access_token",out var token)&&token.ValueKind==JsonValueKind.String&&token.GetString() is {Length:>0} access)
        {
            try{await SignOut(access,ct);}catch(HttpRequestException){}catch(TaskCanceledException)when(!ct.IsCancellationRequested){}
        }
    }

    public async Task<AuthUser?> GetUser(string token, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Get, "user", null, token, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) return null;
        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadFromJsonAsync<AuthUser>(ct);
        return Guid.TryParse(user?.Id, out _) ? user : null;
    }

    public async Task SignOut(string token, CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Post, "logout?scope=local", null, token, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized) response.EnsureSuccessStatusCode();
    }

    public async Task<string?> GetAccountStatus(string token, string userId, CancellationToken ct)
    {
        if (!Guid.TryParse(userId, out var id)) return null;
        using var response = await Send(HttpMethod.Get,
            $"profiles?select=status&id=eq.{id:D}&limit=1", null, token, ct, "rest/v1/");
        response.EnsureSuccessStatusCode();
        var profiles = await response.Content.ReadFromJsonAsync<AccountProfile[]>(ct);
        return profiles?.SingleOrDefault()?.Status;
    }

    internal async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, string? token, CancellationToken ct, string api = "auth/v1/", string? prefer = null)
    {
        if (!IsConfigured) throw new InvalidOperationException("Supabase public configuration is missing.");
        using var request = new HttpRequestMessage(method, configuration["Supabase:Url"]!.TrimEnd('/') + "/" + api + path);
        request.Headers.Add("apikey", configuration["Supabase:PublicKey"]);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (prefer is not null) request.Headers.Add("Prefer", prefer);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await http.SendAsync(request, ct);
    }
}

public sealed record AuthSession(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("user")] AuthUser? User);

public sealed record AuthUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string? Email);

public sealed record LoginRequest(string? Email, string? Password);
public sealed record SignupRequest(string? FullName,string? Email,string? Password);
public sealed record AccountProfile([property: JsonPropertyName("status")] string? Status);
