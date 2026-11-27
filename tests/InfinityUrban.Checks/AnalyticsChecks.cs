using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InfinityUrban.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stayline;

static class AnalyticsChecks
{
    public static async Task Run(IServiceProvider services,HttpClient client,FakeSupabase auth,UnifiedTransport remote,Action<bool,string> check)
    {
        var start=new DateOnly(2028,4,1);var roomId=Guid.Parse(UnifiedTransport.ExistingRoom);var sibling=Guid.Parse(UnifiedTransport.SiblingRoom);var propertyId=Guid.Parse(UnifiedTransport.ExistingProperty);
        var room=new AnalyticsRoom(roomId,propertyId,"Apartamento A","Prédio", "BRL",1);
        var money=new RevenueRoom(roomId,start,start.AddDays(3),10001,new());
        var distribution=AnalyticsMath.Allocate(money)!;
        check(distribution.Values.Sum()==10001&&distribution[start]==3334&&distribution[start.AddDays(2)]==3333,"Revenue allocation preserves cents exactly, including remainder nights");
        var exact=money with{Days=new(){{"2028-04-01",2000},{"2028-04-02",3000},{"2028-04-03",5001}}};
        check(AnalyticsMath.Allocate(exact)![start]==2000,"Complete nightly OTA breakdown takes precedence over a prorated amount");
        var booking=Guid.NewGuid();var rows=new[]{new AnalyticsStay(booking,roomId,start,start.AddDays(3),1,"confirmed",money),new AnalyticsStay(Guid.NewGuid(),roomId,start,start.AddDays(2),1,"cancelled",money)};
        var metrics=AnalyticsMath.Calculate([room],rows,start.AddDays(1),start.AddDays(3));
        check(metrics.Totals.RevenueCents==6667&&metrics.Totals.OccupiedNights==2&&metrics.Totals.TotalNights==3,"Period clipping includes only occupied nights and excludes checkout and cancelled revenue");
        var overlapping=AnalyticsMath.Calculate([room],[rows[0],rows[0] with{BookingId=Guid.NewGuid()}],start,start.AddDays(2));
        check(overlapping.Totals.Occupancy==100&&overlapping.Totals.OccupiedNights==3,"Overlapping bookings never create occupancy above physical capacity");
        check(AnalyticsMath.Calculate([room],[rows[0] with{Revenue=null}],start,start).Totals is{RevenueCents:0,MissingAmounts:1,AverageDailyCents:null},"Missing amounts are not estimated from calendar rates and make ADR unavailable");
        var expense=new ExpenseView(Guid.NewGuid(),"Internet","monthly",30000,"BRL",null,roomId,propertyId,start,null,null,Guid.NewGuid());
        check(AnalyticsMath.ExpenseAmount(expense,start,start.AddDays(14),rows)==15000,"Monthly expenses are prorated within the selected month");
        check(AnalyticsMath.ExpenseAmount(expense with{Kind="per_stay",AmountCents=5000},start,start.AddDays(3),rows)==5000,"Per-stay expenses count check-ins once and exclude cancellations");
        check(AnalyticsMath.ExpenseAmount(expense with{Kind="one_off"},start.AddDays(1),start.AddDays(3),rows)==0,"One-off expense outside the selected date range is excluded");
        var at=DateTimeOffset.UtcNow;var external=Guid.NewGuid();
        var feed=JsonSerializer.SerializeToElement(new{id=Guid.NewGuid(),type="booking_revision",attributes=new{booking_id=external,property_id=propertyId,inserted_at=at.ToString("O"),currency="BRL",amount="303.01",status="new",arrival_date="2028-04-01",departure_date="2028-04-03",customer=new{email="private-must-not-persist"},guarantee=new{card_number="card-must-not-persist"},rooms=new[]{new{room_type_id=roomId,checkin_date="2028-04-01",checkout_date="2028-04-03",amount="303.01",days=new Dictionary<string,string>{{"2028-04-01","150.00"},{"2028-04-02","153.01"}}}}}});
        var projection=AnalyticsLedger.Project(feed)!;var serialized=JsonSerializer.Serialize(projection);
        check(projection.Rooms[0].AmountCents==30301&&!serialized.Contains("private-must-not-persist")&&!serialized.Contains("card-must-not-persist"),"Financial projection persists exact amounts without raw bookings, guest contacts or cards");
        remote.ReservationFeed.Add(feed);
        await services.GetRequiredService<ChannexClient>().FetchBookingFeedAsync();remote.ReservationFeed.Remove(feed);
        var ledger=services.GetRequiredService<AnalyticsLedger>();
        check((await ledger.Read(external,default))?.Rooms[0].AmountCents==30301,"Existing approved feed response is captured durably without a second booking feed consumer");
        await ledger.Save(projection with{RevisionAt=at.AddDays(-1),Rooms=[money]},default);
        check((await ledger.Read(external,default))?.Rooms[0].AmountCents==30301,"An older financial revision cannot overwrite newer confirmed values");
        var factory=services.GetRequiredService<IDbContextFactory<AppDb>>();
        await using(var db=await factory.CreateDbContextAsync())
        {
            var local=await db.RoomTypes.SingleAsync(r=>r.ChannexId==roomId.ToString());
            var reservation=new Reservation{PropertyId=local.PropertyId,GuestName="Analytics test",Source="channex",ExternalBookingId=external.ToString(),ExternalUpdatedAt=at,Arrival=start,Departure=start.AddDays(2),Status="confirmed"};
            reservation.Stays.Add(new(){RoomTypeId=local.Id,RatePlanId=(await db.RatePlans.FirstAsync(r=>r.RoomTypeId==local.Id)).Id,Arrival=start,Departure=start.AddDays(2)});db.Reservations.Add(reservation);await db.SaveChangesAsync();
        }
        var url="/api/analytics?from=2028-04-01&to=2028-04-30&currency=BRL";
        auth.CurrentRole="master";
        check((await client.GetAsync("/dashboard")).IsSuccessStatusCode&&(await client.GetAsync("/relatorios")).IsSuccessStatusCode,"Dashboard and reports are protected portal pages available to approved administrators");
        using(var response=await client.GetAsync(url)){var text=await response.Content.ReadAsStringAsync();check(response.IsSuccessStatusCode&&JsonDocument.Parse(text).RootElement.GetProperty("totals").GetProperty("revenueCents").GetInt64()==30301,"Administrator metrics use the confirmed financial feed projection");}
        check((await client.GetAsync(url+"&roomId="+Guid.NewGuid())).StatusCode==HttpStatusCode.NotFound,"Even administrator exports reject a nonexistent apartment filter");
        check((await client.GetAsync("/api/analytics?from=2028-04-01&to=2030-01-01")).StatusCode==HttpStatusCode.BadRequest,"Analytics rejects unbounded reporting windows");
        var self=Guid.Parse(FakeSupabase.CurrentId);auth.ClearAssignments(self);auth.CurrentRole="funcionario";
        using(var response=await client.GetAsync(url)){using var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync());check(response.IsSuccessStatusCode&&data.RootElement.GetProperty("apartments").GetArrayLength()==0,"An employee without designations does not receive the global dashboard");}
        auth.SeedAssignment(self,roomId,propertyId);
        using(var response=await client.GetAsync(url)){using var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync());check(response.IsSuccessStatusCode&&data.RootElement.GetProperty("rooms").GetArrayLength()==1&&data.RootElement.GetProperty("apartments").GetArrayLength()==1,"Employee dashboard is restricted to its explicitly assigned apartment");}
        auth.CurrentRole="proprietario";
        using(var response=await client.GetAsync(url)){var text=await response.Content.ReadAsStringAsync();using var data=JsonDocument.Parse(text);check(response.IsSuccessStatusCode&&data.RootElement.GetProperty("totals").GetProperty("revenueCents").GetInt64()==30301&&!data.RootElement.GetProperty("canSeeExpenses").GetBoolean()&&data.RootElement.GetProperty("expenses").GetArrayLength()==0&&!text.Contains("PRIVATE SIBLING"),"Owner sees its apartment's financial results and never internal expense details or sibling data");}
        check((await client.GetAsync(url+"&roomId="+sibling)).StatusCode==HttpStatusCode.NotFound&&(await client.GetAsync(url.Replace("/api/analytics?","/api/analytics/export.csv?")+"&roomId="+sibling)).StatusCode==HttpStatusCode.NotFound,"Forged apartment IDs are denied on both dashboard and CSV export");
        using(var response=await client.GetAsync(url.Replace("/api/analytics?","/api/analytics/export.csv?"))){var csv=await response.Content.ReadAsStringAsync();check(response.IsSuccessStatusCode&&csv.Contains("303.01")&&!csv.Contains("Despesas")&&!csv.Contains("PRIVATE SIBLING"),"CSV export applies the same account scope and omits internal expenses for owners");}
        auth.FailAssignments=true;check((await client.GetAsync(url)).StatusCode==HttpStatusCode.ServiceUnavailable,"Assignment store failure cannot fall back to global financial data");auth.FailAssignments=false;
        auth.RevokeAssignmentsAfterRead=true;check((await client.GetAsync(url)).StatusCode==HttpStatusCode.Conflict,"Revocation during report generation blocks the response before release");
        auth.CurrentRole="master";auth.ClearAssignments(self);
        using var reportResponse=await client.GetAsync(url);var report=await reportResponse.Content.ReadFromJsonAsync<AnalyticsView>();
        var formula=AnalyticsService.Csv(report! with{Apartments=[new(roomId,"=HYPERLINK(1)","@SUM(1)",0,0,30,0,0,0)]});
        check(formula.Contains("'=HYPERLINK")&&formula.Contains("'@SUM"),"CSV labels cannot execute spreadsheet formulas");
        var english=AnalyticsService.Csv(report! with{Apartments=[new(roomId,"Apartamento do proprietário","Prédio cadastrado",0,0,30,0,0,0)]},"en");
        check(english.Contains("Confirmed revenue")&&english.Contains("Available nights")&&english.Contains("Apartamento do proprietário")&&english.Contains("Prédio cadastrado"),"English CSV translates report headings while preserving registered apartment and building names");
    }
}
