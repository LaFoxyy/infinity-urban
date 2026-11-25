create table public.urban_channex_assignments (
  id uuid primary key default gen_random_uuid(),
  environment text not null check(environment in ('production','staging')),
  user_id uuid not null references public.profiles(id) on delete cascade,
  room_type_id uuid not null,
  property_id uuid not null,
  assigned_by uuid default auth.uid() references auth.users(id) on delete set null,
  created_at timestamptz not null default now(),
  unique(environment,user_id,room_type_id)
);
create index urban_channex_assignments_user_idx on public.urban_channex_assignments(user_id);
create index urban_channex_assignments_actor_idx on public.urban_channex_assignments(assigned_by);
create index urban_channex_assignments_room_idx on public.urban_channex_assignments(environment,room_type_id);
create index urban_channex_assignments_date_idx on public.urban_channex_assignments(environment,created_at desc,id);
alter table public.urban_channex_assignments enable row level security;
revoke all on public.urban_channex_assignments from public,anon,authenticated;
grant select,delete on public.urban_channex_assignments to authenticated;
grant insert(environment,user_id,room_type_id,property_id) on public.urban_channex_assignments to authenticated;
create policy urban_assignments_read on public.urban_channex_assignments for select to authenticated using (
  (select urban_private.urban_current_can_manage_assignments())
  or (
    user_id=(select auth.uid())
    and exists(select 1 from public.profiles p where p.id=(select auth.uid()) and p.status::text='approved')
    and exists(select 1 from public.user_roles r where r.user_id=(select auth.uid()) and r.role::text='proprietario')
    and not exists(select 1 from public.user_roles r where r.user_id=(select auth.uid()) and r.role::text in ('master','admin','funcionario'))
  )
);
create policy urban_assignments_insert on public.urban_channex_assignments for insert to authenticated with check (
  (select urban_private.urban_current_can_manage_assignments())
  and exists(select 1 from public.profiles p where p.id=user_id and p.status::text='approved')
  and exists(select 1 from public.user_roles r where r.user_id=urban_channex_assignments.user_id and r.role::text='proprietario')
  and not exists(select 1 from public.user_roles r where r.user_id=urban_channex_assignments.user_id and r.role::text in ('master','admin','funcionario'))
);
create policy urban_assignments_delete on public.urban_channex_assignments for delete to authenticated using ((select urban_private.urban_current_can_manage_assignments()));
comment on table public.urban_channex_assignments is 'Account access to individual Channex Room Types. Separate from owner contacts and all legacy Guesty assignments. Room and parent are verified by the server.';
notify pgrst,'reload schema';
