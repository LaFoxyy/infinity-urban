using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace InfinityUrban.Web;

public static class ManagementEndpoints
{
    public static void MapManagement(this WebApplication app)
    {
        app.MapGet("/api/management/users", async (HttpContext context, ManagementService service) =>
        {
            var (token, member) = await Actor(context, service, false);
            return Results.Ok(new { users = await service.List(token, context.RequestAborted), currentUser = member, fetchedAt = DateTimeOffset.UtcNow });
        }).RequireAuthorization();

        app.MapPost("/api/management/users/{id:guid}/status", async (Guid id, StatusChange change, HttpContext context, ManagementService service) =>
        {
            var (token, actor) = await Actor(context, service, true);
            CheckTarget(actor, id);
            await service.ChangeStatus(token, id, change, context.RequestAborted);
            return Results.Ok(new { updated = true });
        }).RequireAuthorization();

        app.MapPost("/api/management/users/{id:guid}/role", async (Guid id, RoleChange change, HttpContext context, ManagementService service) =>
        {
            var (token, actor) = await Actor(context, service, true);
            CheckTarget(actor, id);
            await service.ChangeRole(token, id, change, context.RequestAborted);
            return Results.Ok(new { updated = true });
        }).RequireAuthorization();

        app.MapPost("/api/management/users/{id:guid}/delete", async (Guid id, DeleteAccountRequest request, HttpContext context, ManagementService service) =>
        {
            var (token, actor) = await Actor(context, service, true);
            CheckTarget(actor, id);
            var target = await service.CurrentMember(token, id.ToString(), context.RequestAborted);
            if (target is null) throw new PortalError(404, "Conta não encontrada.");
            if (string.IsNullOrWhiteSpace(target.Email) || request.ConfirmEmail?.Trim() != target.Email)
                throw new PortalError(400, "Digite o email da conta para confirmar a exclusão.");
            await service.DeleteAccount(token, id, context.RequestAborted);
            return Results.Ok(new { deleted = true });
        }).RequireAuthorization();

        app.MapGet("/gestao", () => Results.File(Path.Combine(app.Environment.ContentRootPath, "Pages", "gestao.html"), "text/html"))
            .RequireAuthorization("management");
    }

    private static async Task<(string Token, PortalMember Member)> Actor(HttpContext context, ManagementService service, bool write)
    {
        var token = await context.GetTokenAsync("access_token");
        var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (token is null || id is null) throw new PortalError(401, "Sua sessão expirou. Entre novamente.");
        // Cookie validation already loaded a freshly verified profile for this request.
        var member = context.Items["PortalMember"] as PortalMember;
        if(member?.Id.ToString()!=id)throw new PortalError(401,"Sua sessão expirou. Entre novamente.");
        if (member is null || !member.CanManage || (write && !member.CanEdit))
            throw new PortalError(403, write ? "Somente Master pode alterar contas e permissões." : "A Gestão está disponível apenas para Master e Admin.");
        return (token, member);
    }

    private static void CheckTarget(PortalMember actor, Guid id)
    {
        if (actor.Id == id) throw new PortalError(403, "Você não pode alterar ou excluir sua própria conta pela Gestão.");
    }
}
