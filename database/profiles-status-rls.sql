create function urban_private.urban_current_account_status()
returns text
language sql stable security definer
set search_path = ''
as $function$
  select p.status::text from public.profiles p where p.id = (select auth.uid());
$function$;
revoke all on function urban_private.urban_current_account_status() from public,anon;
grant execute on function urban_private.urban_current_account_status() to authenticated;
comment on function urban_private.urban_current_account_status() is 'Current caller status only; definer lookup avoids the recursive profiles UPDATE RLS self-reference. No caller-supplied user ID.';
alter policy profiles_self_update on public.profiles
using (id = (select auth.uid()))
with check (id = (select auth.uid()) and status::text = (select urban_private.urban_current_account_status()));
notify pgrst,'reload schema';
