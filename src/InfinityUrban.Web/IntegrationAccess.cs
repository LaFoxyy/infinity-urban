using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Stayline;

namespace InfinityUrban.Web;

public static class IntegrationAccess
{
    public static string ChannexSourceRoot(IConfiguration config,string contentRoot) =>
        Path.GetFullPath(config["Channex:SourceDirectory"]??"../InfinityUrban.Channex",contentRoot);
    public static bool IsLegacyApi(PathString path,string method)
    {
        var p=(path.Value??"").TrimEnd('/').ToLowerInvariant();const string id="[0-9a-fA-F-]{36}";
        if(method=="GET")return Regex.IsMatch(p,$"^/api/(status|properties|properties/{id}/(calendar|reservations|sync)|channex/(remote|import-preview))$");
        return Regex.IsMatch(p,$"^/api/(properties|properties/{id}/(rooms|calendar|reservations|full-sync|retry|channex/provision)|rooms/{id}/rates|reservations/{id}|properties/{id}/mapping|channex/(bookings/check|import))$")
            && method is "POST" or "PUT" or "DELETE";
    }

    public static Task<PortalMember> PortfolioReader(HttpContext context)
    {
        if(context.Items["PortalMember"] is not PortalMember member||(!member.IsOperator&&!member.IsRestrictedOwner))throw new PortalError(403,"Sua conta não tem acesso às acomodações.");
        return Task.FromResult(member);
    }

    public static Task<PortalMember> Operator(HttpContext context,bool write=false)
    {
        if(context.Items["PortalMember"] is not PortalMember member || member.Status!="approved"
            || !member.Roles.Any(role=>role.Role is "master" or "admin" || (!write&&role.Role=="funcionario")))
            throw new PortalError(403,write?"Somente Master e Admin podem cadastrar ou alterar acomodações.":"Acesso às operações disponível apenas para a equipe aprovada.");
        return Task.FromResult(member);
    }

}
