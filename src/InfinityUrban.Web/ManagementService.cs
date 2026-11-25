using System.Net;
using System.Text.Json.Serialization;

namespace InfinityUrban.Web;

public sealed class ManagementService(SupabaseAuth auth)
{
    public static readonly string[] Roles = ["master", "admin", "proprietario", "funcionario"];

    public async Task<PortalMember?> CurrentMember(string token, string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var userId)) return null;
        var profilesTask = Read<MemberProfile>(token, $"profiles?select=id,email,full_name,status,updated_at&id=eq.{userId:D}&limit=1", ct);
        var rolesTask = Read<MemberRole>(token, $"user_roles?select=id,user_id,role&user_id=eq.{userId:D}&order=id", ct);
        await Task.WhenAll(profilesTask,rolesTask);var profiles=await profilesTask;
        var profile = profiles.SingleOrDefault();
        if (profile is null) return null;
        var roles = await rolesTask;
        return Join(profile, roles);
    }

    public async Task<PortalMember[]> List(string token, CancellationToken ct)
    {
        // Read all pages: PostgREST caps a response at the project's configured limit.
        var profilesTask = ReadAll<MemberProfile>(token, "profiles?select=id,email,full_name,status,updated_at&order=created_at,id", ct);
        var rolesTask = ReadAll<MemberRole>(token, "user_roles?select=id,user_id,role&order=id", ct);
        await Task.WhenAll(profilesTask,rolesTask);var profiles=await profilesTask;var roles=await rolesTask;
        var byUser = roles.ToLookup(role => role.UserId);
        return profiles.Select(profile => Join(profile, byUser[profile.Id])).ToArray();
    }

    public async Task ChangeStatus(string token, Guid target, StatusChange change, CancellationToken ct)
    {
        if (change.Status is not ("approved" or "rejected") || change.ExpectedStatus is not ("pending" or "approved" or "rejected"))
            throw new PortalError(400, "Status inválido.");
        var rows = await Write<MemberProfile>(token, HttpMethod.Patch,
            $"profiles?id=eq.{target:D}&status=eq.{change.ExpectedStatus}&select=id,email,full_name,status,updated_at", new { status = change.Status }, ct);
        if (rows.Length != 1) throw new PortalError(409, "Esta conta foi alterada ou não está disponível. Atualize a lista.");
    }

    public async Task ChangeRole(string token, Guid target, RoleChange change, CancellationToken ct)
    {
        if (!Roles.Contains(change.Role)) throw new PortalError(400, "Função inválida.");
        var existing = await Read<MemberRole>(token, $"user_roles?select=id,user_id,role&user_id=eq.{target:D}&order=id", ct);
        if (existing.Length > 1) throw new PortalError(409, "Esta conta tem mais de uma função. As permissões existentes foram preservadas.");
        if (existing.Length == 0)
        {
            if (change.ExpectedRoleId is not null || change.ExpectedRole is not null) throw ChangedRole();
            var targetProfile = await Read<MemberProfile>(token, $"profiles?select=id,email,full_name,status,updated_at&id=eq.{target:D}&limit=1", ct);
            if (targetProfile.Length != 1) throw new PortalError(404, "Conta não encontrada.");
            await Write<MemberRole>(token, HttpMethod.Post, "user_roles?on_conflict=user_id,role&select=id,user_id,role",
                new { user_id = target, role = change.Role }, ct, "return=representation,resolution=ignore-duplicates");
            var current = await Read<MemberRole>(token, $"user_roles?select=id,user_id,role&user_id=eq.{target:D}&order=id", ct);
            if (current.Length != 1 || current[0].Role != change.Role)
                throw new PortalError(409, "Foram detectadas alterações simultâneas nas funções. Atualize a lista antes de continuar.");
            return;
        }
        var row = existing[0];
        if (change.ExpectedRoleId != row.Id || change.ExpectedRole != row.Role) throw ChangedRole();
        if (row.Role == change.Role) return;
        var changed = await Write<MemberRole>(token, HttpMethod.Patch,
            $"user_roles?id=eq.{row.Id:D}&user_id=eq.{target:D}&role=eq.{row.Role}&select=id,user_id,role", new { role = change.Role }, ct);
        if (changed.Length != 1) throw ChangedRole();
    }

    public async Task DeleteAccount(string token, Guid target, CancellationToken ct)
    {
        // Existing Auth administration only. No Guesty function or credential is used.
        using var response = await auth.Send(HttpMethod.Post, "admin-delete-user", new { target_user_id = target }, token, ct, "functions/v1/");
        await EnsureSuccess(response, ct);
        var result = await response.Content.ReadFromJsonAsync<DeleteResult>(ct);
        if (result?.Success != true) throw new PortalError(502, "A exclusão não foi confirmada. Atualize a lista antes de tentar novamente.");
    }

    private static PortalMember Join(MemberProfile profile, IEnumerable<MemberRole> roles)
    {
        var list = roles.ToArray();
        var primary = Roles.FirstOrDefault(role => list.Any(row => row.Role == role));
        return new(profile.Id, profile.Email, profile.FullName, profile.Status, primary, list, profile.UpdatedAt);
    }

    private async Task<T[]> Read<T>(string token, string path, CancellationToken ct)
    {
        using var response = await auth.Send(HttpMethod.Get, path, null, token, ct, "rest/v1/");
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<T[]>(ct) ?? [];
    }

    private async Task<T[]> ReadAll<T>(string token, string path, CancellationToken ct)
    {
        List<T> rows = [];
        var offset = 0;
        while (true)
        {
            // Offset advances by the number actually received, including lower server caps.
            var page = await Read<T>(token, $"{path}&offset={offset}&limit=500", ct);
            if (page.Length == 0) return rows.ToArray();
            rows.AddRange(page);
            offset += page.Length;
            if (offset > 100000) throw new PortalError(503, "A lista é muito grande. Tente novamente mais tarde.");
        }
    }

    private async Task<T[]> Write<T>(string token, HttpMethod method, string path, object body, CancellationToken ct, string prefer = "return=representation")
    {
        using var response = await auth.Send(method, path, body, token, ct, "rest/v1/", prefer);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<T[]>(ct) ?? [];
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        // Do not send database/internal function error details back to the browser.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new PortalError(403, "Você não tem permissão para realizar esta ação.");
        if (response.StatusCode == HttpStatusCode.Conflict) throw ChangedRole();
        if (response.StatusCode == HttpStatusCode.NotFound) throw new PortalError(404, "Conta ou recurso não encontrado.");
        await Task.CompletedTask;
        throw new PortalError(502, "O Supabase não concluiu a operação. Atualize a lista antes de tentar novamente.");
    }

    private static PortalError ChangedRole() => new(409, "As permissões desta conta mudaram. Atualize a lista antes de continuar.");
}

public sealed record MemberProfile(Guid Id, string? Email,
    [property: JsonPropertyName("full_name")] string? FullName,
    string Status, [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);
public sealed record MemberRole(Guid Id, [property: JsonPropertyName("user_id")] Guid UserId, string Role);
public sealed record PortalMember(Guid Id, string? Email, string? FullName, string Status, string? Role, MemberRole[] Roles, DateTimeOffset UpdatedAt)
{
    public bool CanManage => Status == "approved" && Roles.Any(role => role.Role is "master" or "admin");
    public bool IsOperator => Status=="approved" && Roles.Any(role=>role.Role is "master" or "admin" or "funcionario");
    public bool IsRestrictedOwner => Status=="approved" && !IsOperator && Roles.Any(role=>role.Role=="proprietario");
    public bool CanEdit => Status == "approved" && Roles.Any(role => role.Role == "master");
}
public sealed record StatusChange(string? Status, string? ExpectedStatus);
public sealed record RoleChange(string Role, Guid? ExpectedRoleId, string? ExpectedRole);
public sealed record DeleteAccountRequest(string? ConfirmEmail);
public sealed record DeleteResult(bool Success);
public sealed class PortalError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
