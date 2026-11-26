using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Stayline;

namespace InfinityUrban.Web;

public sealed record AnalyticsRoom(Guid Id,Guid PropertyId,string Title,string Building,string Currency,int Capacity);
public sealed record AnalyticsStay(Guid BookingId,Guid RoomId,DateOnly Arrival,DateOnly Departure,int Quantity,string Status,RevenueRoom? Revenue);
public sealed record ApartmentPerformance(Guid Id,string Title,string Building,int Reservations,int OccupiedNights,int TotalNights,decimal Occupancy,long RevenueCents,int MissingAmounts);
public sealed record DailyPerformance(DateOnly Date,long RevenueCents,int OccupiedNights);
public sealed record ReportExpense(Guid Id,string Name,string Kind,string Category,string Target,long AmountCents);
public sealed record AnalyticsTotals(long RevenueCents,int Reservations,int OccupiedNights,int TotalNights,decimal Occupancy,long? AverageDailyCents,int MissingAmounts,int Cancelled,long? ExpenseCents,long? BalanceCents);
public sealed record AnalyticsView(DateOnly From,DateOnly To,string Currency,AnalyticsRoom[] Rooms,ApartmentPerformance[] Apartments,ApartmentPerformance[] Best,ApartmentPerformance[] Worst,DailyPerformance[] Daily,Dictionary<string,int> Statuses,AnalyticsTotals Totals,ReportExpense[] Expenses,bool Restricted,bool CanSeeExpenses,DateTimeOffset FetchedAt,string[] Notices);

public static class AnalyticsMath
{
    public static bool Cancelled(string status)=>status is "cancelled" or "canceled" or "rejected";
    public static Dictionary<DateOnly,long>? Allocate(RevenueRoom? revenue)
    {
        if(revenue?.AmountCents is not {} amount||revenue.Departure<=revenue.Arrival)return null;
        var count=revenue.Departure.DayNumber-revenue.Arrival.DayNumber;if(count>730)return null;
        if(revenue.Days is { } days&&days.Count==count&&days.Values.Sum()==amount&&Enumerable.Range(0,count).All(i=>days.ContainsKey(revenue.Arrival.AddDays(i).ToString("yyyy-MM-dd"))))
            return days.ToDictionary(d=>DateOnly.ParseExact(d.Key,"yyyy-MM-dd"),d=>d.Value);
        return Enumerable.Range(0,count).ToDictionary(i=>revenue.Arrival.AddDays(i),i=>amount/count+(i<amount%count?1:0));
    }
    public static (ApartmentPerformance[] Apartments,DailyPerformance[] Daily,Dictionary<string,int> Statuses,AnalyticsTotals Totals) Calculate(AnalyticsRoom[] rooms,AnalyticsStay[] stays,DateOnly from,DateOnly to)
    {
        var days=Enumerable.Range(0,to.DayNumber-from.DayNumber+1).Select(from.AddDays).ToArray();
        var daily=days.ToDictionary(d=>d,d=>(Revenue:0L,Occupied:0));var result=new List<ApartmentPerformance>();
        foreach(var room in rooms)
        {
            var rows=stays.Where(s=>s.RoomId==room.Id&&s.Arrival<=to&&s.Departure>from&&!Cancelled(s.Status)).ToArray();
            long revenue=0;var occupied=0;var missing=0;
            foreach(var stay in rows)
            {
                var distribution=Allocate(stay.Revenue);if(distribution is null)missing++;
                foreach(var date in days.Where(d=>d>=stay.Arrival&&d<stay.Departure))
                {var money=distribution?.GetValueOrDefault(date)??0;revenue+=money;var old=daily[date];daily[date]=(old.Revenue+money,old.Occupied);}
            }
            foreach(var date in days)
            {
                // Multiple overlapping bookings never inflate physical occupancy beyond capacity.
                var count=Math.Min(room.Capacity,rows.Where(s=>s.Arrival<=date&&s.Departure>date).Sum(s=>s.Quantity));occupied+=count;
                var old=daily[date];daily[date]=(old.Revenue,old.Occupied+count);
            }
            var capacity=days.Length*room.Capacity;
            result.Add(new(room.Id,room.Title,room.Building,rows.Select(r=>r.BookingId).Distinct().Count(),occupied,capacity,capacity==0?0:decimal.Round(100m*occupied/capacity,1),revenue,missing));
        }
        var ids=rooms.Select(r=>r.Id).ToHashSet();var visible=stays.Where(s=>ids.Contains(s.RoomId)&&s.Arrival<=to&&s.Departure>from).ToArray();
        var statuses=visible.GroupBy(s=>s.BookingId).Select(g=>g.First()).GroupBy(s=>Cancelled(s.Status)?"Canceladas":"Confirmadas").ToDictionary(g=>g.Key,g=>g.Count());
        var revenueTotal=result.Sum(r=>r.RevenueCents);var occupiedTotal=result.Sum(r=>r.OccupiedNights);var total=result.Sum(r=>r.TotalNights);var missingTotal=result.Sum(r=>r.MissingAmounts);
        var totals=new AnalyticsTotals(revenueTotal,visible.Where(s=>!Cancelled(s.Status)).Select(s=>s.BookingId).Distinct().Count(),occupiedTotal,total,total==0?0:decimal.Round(100m*occupiedTotal/total,1),occupiedTotal==0||missingTotal>0?null:(long)decimal.Round((decimal)revenueTotal/occupiedTotal,0,MidpointRounding.AwayFromZero),missingTotal,statuses.GetValueOrDefault("Canceladas"),null,null);
        return(result.ToArray(),daily.Select(d=>new DailyPerformance(d.Key,d.Value.Revenue,d.Value.Occupied)).ToArray(),statuses,totals);
    }
    public static long ExpenseAmount(ExpenseView expense,DateOnly from,DateOnly to,AnalyticsStay[] stays)
    {
        if(expense.Kind=="one_off")return expense.StartDate>=from&&expense.StartDate<=to?expense.AmountCents:0;
        var end=expense.EndDate is {} e?new DateOnly(e.Year,e.Month,DateTime.DaysInMonth(e.Year,e.Month)):DateOnly.MaxValue;
        if(expense.Kind=="per_stay")return checked(expense.AmountCents*stays.Where(s=>!Cancelled(s.Status)&&s.Arrival>=from&&s.Arrival<=to&&s.Arrival>=expense.StartDate&&s.Arrival<=end&&(expense.RoomTypeId is null||expense.RoomTypeId==s.RoomId)).GroupBy(s=>(s.BookingId,s.RoomId)).Sum(g=>g.First().Quantity));
        long total=0;
        for(var month=new DateOnly(from.Year,from.Month,1);month<=to;month=month.AddMonths(1))
        {
            if(month<expense.StartDate||month>end)continue;
            var monthDays=DateTime.DaysInMonth(month.Year,month.Month);
            var start=from>month?from:month;var finish=to<month.AddDays(monthDays-1)?to:month.AddDays(monthDays-1);
            total+= (long)decimal.Round((decimal)expense.AmountCents*(finish.DayNumber-start.DayNumber+1)/monthDays,0,MidpointRounding.AwayFromZero);
        }
        return total;
    }
}

public sealed class AnalyticsService(IDbContextFactory<AppDb> factory,ChannexClient channex,AssignmentsService assignments,ExpensesService expenses,AnalyticsLedger ledger)
{
    public async Task<AnalyticsView> Read(HttpContext context,DateOnly from,DateOnly to,Guid? selected,string currency)
    {
        if(from.Year<2000||to.Year>2099||to<from||to.DayNumber-from.DayNumber>365||!PmsService.Currencies.Contains(currency))throw new PortalError(400,"Escolha uma moeda e um período de até 366 dias, entre 2000 e 2099.");
        var member=await IntegrationAccess.PortfolioReader(context);var ct=context.RequestAborted;
        var token=await context.GetTokenAsync("access_token")??throw new PortalError(401,"Sua sessão expirou.");
        // Administrators see the portfolio; both employees and owners are scoped to explicit designations.
        var restricted=!member.CanManage;var allowed=restricted?await assignments.Mine(token,member.Id,ct):[];
        if(selected is {} requested&&restricted&&!allowed.Any(a=>a.RoomTypeId==requested))throw new PortalError(404,"Apartamento não disponível para esta conta.");
        if(restricted&&allowed.Length==0)return Empty(from,to,currency,member.IsOperator);
        var propertiesTask=channex.FetchCollectionAsync("properties",ct);var roomsTask=channex.FetchCollectionAsync("room_types",ct);await Task.WhenAll(propertiesTask,roomsTask);
        var buildings=(await propertiesTask).ToDictionary(p=>Guid.Parse(RemoteAccommodationService.Text(p,"id")!),p=>RemoteAccommodationService.Attributes(p));
        var rooms=(await roomsTask).Select(r=>
        {
            var a=RemoteAccommodationService.Attributes(r);var id=Guid.Parse(RemoteAccommodationService.Text(r,"id")!);var parent=Guid.Parse(RemoteAccommodationService.Parent(r,"property")!);var p=buildings.GetValueOrDefault(parent);
            var capacity=a.TryGetProperty("count_of_rooms",out var n)&&n.TryGetInt32(out var count)?Math.Clamp(count,1,10000):1;
            return new AnalyticsRoom(id,parent,RemoteAccommodationService.Text(a,"title")??"Apartamento",RemoteAccommodationService.Text(p,"title")??"Prédio",RemoteAccommodationService.Text(p,"currency")??"BRL",capacity);
        }).Where(r=>!restricted||allowed.Any(a=>a.RoomTypeId==r.Id&&a.PropertyId==r.PropertyId)).ToArray();
        if(selected is {} roomId&&!rooms.Any(r=>r.Id==roomId))throw new PortalError(404,"Apartamento não disponível para esta conta.");
        var active=rooms.Where(r=>(selected is null||r.Id==selected)&&r.Currency==currency).ToArray();var roomIds=active.Select(r=>r.Id).ToHashSet();
        await using var db=await factory.CreateDbContextAsync(ct);
        var localRooms=await db.RoomTypes.AsNoTracking().ToListAsync(ct);var localProperties=await db.Properties.AsNoTracking().ToListAsync(ct);
        var localIds=localRooms.Where(r=>Guid.TryParse(r.ChannexId,out var remote)&&roomIds.Contains(remote)).Select(r=>r.Id).ToArray();
        var bookings=await db.Reservations.AsNoTracking().Where(r=>r.Stays.Any(s=>localIds.Contains(s.RoomTypeId)&&(s.Arrival??r.Arrival)<=to&&(s.Departure??r.Departure)>from)).Include(r=>r.Stays).Take(50001).ToListAsync(ct);
        if(bookings.Count>50000)throw new PortalError(503,"Reduza o período para consultar este volume de reservas.");
        var stays=new List<AnalyticsStay>();
        var snapshots=await ledger.ReadMany(bookings.Select(b=>Guid.TryParse(b.ExternalBookingId,out var id)?id:Guid.Empty).Where(id=>id!=Guid.Empty),ct);
        foreach(var booking in bookings)
        {
            var property=localProperties.SingleOrDefault(p=>p.Id==booking.PropertyId);var snapshot=Guid.TryParse(booking.ExternalBookingId,out var external)?snapshots.GetValueOrDefault(external):null;
            // A later operational revision invalidates old amounts until its financial projection arrives.
            if(snapshot is not null&&(property?.ChannexId!=snapshot.PropertyId.ToString()||booking.ExternalUpdatedAt is {} at&&snapshot.RevisionAt<at))snapshot=null;
            var consumed=new HashSet<int>();
            foreach(var stay in booking.Stays)
            {
                var local=localRooms.SingleOrDefault(r=>r.Id==stay.RoomTypeId);if(!Guid.TryParse(local?.ChannexId,out var id)||!roomIds.Contains(id))continue;
                var room=active.Single(r=>r.Id==id);if(property?.ChannexId!=room.PropertyId.ToString())continue;
                var start=stay.Arrival??booking.Arrival;var end=stay.Departure??booking.Departure;RevenueRoom? money=null;
                if(snapshot?.Currency==currency)for(var i=0;i<snapshot.Rooms.Length;i++)if(!consumed.Contains(i)&&snapshot.Rooms[i].RoomId==id&&snapshot.Rooms[i].Arrival==start&&snapshot.Rooms[i].Departure==end){money=snapshot.Rooms[i];consumed.Add(i);break;}
                stays.Add(new(booking.Id,id,start,end,Math.Max(1,stay.Quantity),booking.Status,money));
            }
        }
        var calculated=AnalyticsMath.Calculate(active,stays.ToArray(),from,to);var details=new List<ReportExpense>();
        if(member.IsOperator)
        {
            var data=JsonSerializer.SerializeToElement(await expenses.List(token,ct),JsonSerializerOptions.Web);
            var rules=data.GetProperty("expenses").Deserialize<ExpenseView[]>(JsonSerializerOptions.Web)??[];
            var categories=data.GetProperty("categories").Deserialize<ExpenseCategory[]>(JsonSerializerOptions.Web)??[];
            foreach(var rule in rules.Where(e=>e.Currency==currency))
            {
                // Building/shared expenses appear once, only in the administrator's consolidated report.
                if(rule.RoomTypeId is {} rid?!roomIds.Contains(rid):restricted||selected is not null||rule.PropertyId is {} pid&&!active.Any(r=>r.PropertyId==pid))continue;
                var matching=stays.Where(s=>rule.PropertyId is null||active.Any(r=>r.Id==s.RoomId&&r.PropertyId==rule.PropertyId)).ToArray();
                var amount=AnalyticsMath.ExpenseAmount(rule,from,to,matching);if(amount==0)continue;
                details.Add(new(rule.Id,rule.Name,rule.Kind,categories.FirstOrDefault(c=>c.Id==rule.CategoryId)?.Label??"Sem categoria",rule.RoomTypeId is {} target?active.First(r=>r.Id==target).Title:rule.PropertyId is {} building?active.First(r=>r.PropertyId==building).Building:"Sem vínculo",amount));
            }
        }
        if(restricted)
        {
            var current=await assignments.Mine(token,member.Id,ct);
            if(!current.Select(a=>(a.RoomTypeId,a.PropertyId)).ToHashSet().SetEquals(allowed.Select(a=>(a.RoomTypeId,a.PropertyId))))throw new PortalError(409,"Suas designações mudaram. Atualize a página.");
        }
        var notices=new List<string>();if(calculated.Totals.MissingAmounts>0)notices.Add("Há estadias sem valor confirmado. A receita e o saldo são parciais; a diária do calendário não é usada como receita.");
        notices.Add("Receita das diárias do apartamento distribuída pelas noites da estadia. Check-out não ocupa noite; canceladas não geram receita ou ocupação. Não representa repasse bancário.");
        if(member.IsOperator)notices.Add("Mensais proporcionais aos dias selecionados; por estadia no check-in; avulsas na data cadastrada. Despesas de prédio e sem vínculo aparecem apenas no consolidado administrativo, sem rateio entre apartamentos.");
        var totals=calculated.Totals with{ExpenseCents=member.IsOperator?details.Sum(e=>e.AmountCents):null,BalanceCents=member.IsOperator?calculated.Totals.RevenueCents-details.Sum(e=>e.AmountCents):null};
        return new(from,to,currency,rooms,calculated.Apartments,calculated.Apartments.Where(a=>a.MissingAmounts==0).OrderByDescending(a=>a.RevenueCents).ThenByDescending(a=>a.Occupancy).ThenBy(a=>a.Title).Take(10).ToArray(),calculated.Apartments.Where(a=>a.MissingAmounts==0).OrderBy(a=>a.RevenueCents).ThenBy(a=>a.Occupancy).ThenBy(a=>a.Title).Take(10).ToArray(),calculated.Daily,calculated.Statuses,totals,details.ToArray(),restricted,member.IsOperator,DateTimeOffset.UtcNow,notices.ToArray());
    }
    private static AnalyticsView Empty(DateOnly from,DateOnly to,string currency,bool internalUser)=>new(from,to,currency,[],[],[],[],[],new(),new(0,0,0,0,0,null,0,0,internalUser?0:null,internalUser?0:null),[],true,internalUser,DateTimeOffset.UtcNow,["Nenhum apartamento designado para esta conta. Solicite a designação à administração."]);
    public static string Csv(AnalyticsView report)
    {
        static string Cell(string value)=>"\""+((value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' or '\t' or '\r')?"'":"")+value.Replace("\"","\"\"")+"\"";
        var text=new StringBuilder("\uFEFF");void Row(params string[] cells)=>text.AppendLine(string.Join(';',cells.Select(Cell)));
        Row("Infinity Urban",report.From.ToString("dd/MM/yyyy"),report.To.ToString("dd/MM/yyyy"),report.Currency);
        Row("Apartamento","Prédio","Reservas","Noites ocupadas","Noites disponíveis","Ocupação (%)","Receita confirmada","Estadias sem valor");
        foreach(var room in report.Apartments)Row(room.Title,room.Building,room.Reservations.ToString(),room.OccupiedNights.ToString(),room.TotalNights.ToString(),room.Occupancy.ToString(CultureInfo.InvariantCulture),(room.RevenueCents/100m).ToString("F2",CultureInfo.InvariantCulture),room.MissingAmounts.ToString());
        Row("Receita confirmada",(report.Totals.RevenueCents/100m).ToString("F2",CultureInfo.InvariantCulture));
        if(report.CanSeeExpenses){Row("Despesas",(report.Totals.ExpenseCents/100m)?.ToString("F2",CultureInfo.InvariantCulture)??"");Row("Saldo após despesas",(report.Totals.BalanceCents/100m)?.ToString("F2",CultureInfo.InvariantCulture)??"");Row("Despesa","Categoria","Vínculo","Valor");foreach(var expense in report.Expenses)Row(expense.Name,expense.Category,expense.Target,(expense.AmountCents/100m).ToString("F2",CultureInfo.InvariantCulture));}
        foreach(var note in report.Notices)Row(note);return text.ToString();
    }
}
