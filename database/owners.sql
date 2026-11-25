-- Owner contact data and Channex Room associations are kept only in Supabase.
-- Guesty tables and the approved Channex integration are not changed.
create table public.urban_channex_owners (
  id uuid primary key default gen_random_uuid(),
  environment text not null default 'production' check(environment in ('production','staging')),
  full_name text not null check(length(btrim(full_name)) between 1 and 180),
  email text check(email is null or length(email) <= 320),
  phone text check(phone is null or length(phone) <= 50),
  revision uuid not null default gen_random_uuid(),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  created_by uuid default auth.uid() references auth.users(id) on delete set null,
  unique(environment,id)
);
create index urban_channex_owners_name_idx on public.urban_channex_owners(environment,full_name,id);
create table public.urban_channex_room_owners (
  environment text not null check(environment in ('production','staging')),
  room_type_id uuid not null,
  property_id uuid not null,
  owner_id uuid not null,
  revision uuid not null default gen_random_uuid(),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  created_by uuid default auth.uid() references auth.users(id) on delete set null,
  primary key(environment,room_type_id),
  foreign key(environment,owner_id) references public.urban_channex_owners(environment,id) on delete restrict
);
create index urban_channex_room_owners_owner_idx on public.urban_channex_room_owners(environment,owner_id);
alter table public.urban_channex_owners enable row level security;
alter table public.urban_channex_room_owners enable row level security;
revoke all on public.urban_channex_owners,public.urban_channex_room_owners from anon,authenticated;
grant select,delete on public.urban_channex_owners,public.urban_channex_room_owners to authenticated;
grant insert(id,environment,full_name,email,phone) on public.urban_channex_owners to authenticated;
grant update(full_name,email,phone,revision,updated_at) on public.urban_channex_owners to authenticated;
grant insert(environment,room_type_id,property_id,owner_id) on public.urban_channex_room_owners to authenticated;
grant update(owner_id,property_id,revision,updated_at) on public.urban_channex_room_owners to authenticated;
create policy urban_owners_read on public.urban_channex_owners for select to authenticated using ((select urban_private.urban_current_is_operator()));
create policy urban_owners_insert on public.urban_channex_owners for insert to authenticated with check ((select urban_private.urban_current_can_manage_assignments()));
create policy urban_owners_update on public.urban_channex_owners for update to authenticated using ((select urban_private.urban_current_can_manage_assignments())) with check ((select urban_private.urban_current_can_manage_assignments()));
create policy urban_owners_delete on public.urban_channex_owners for delete to authenticated using ((select urban_private.urban_current_can_manage_assignments()));
create policy urban_room_owners_read on public.urban_channex_room_owners for select to authenticated using ((select urban_private.urban_current_is_operator()));
create policy urban_room_owners_insert on public.urban_channex_room_owners for insert to authenticated with check ((select urban_private.urban_current_can_manage_assignments()));
create policy urban_room_owners_update on public.urban_channex_room_owners for update to authenticated using ((select urban_private.urban_current_can_manage_assignments())) with check ((select urban_private.urban_current_can_manage_assignments()));
create policy urban_room_owners_delete on public.urban_channex_room_owners for delete to authenticated using ((select urban_private.urban_current_can_manage_assignments()));
comment on table public.urban_channex_owners is 'Urban Infinity owner contacts, independent of Guesty and Channex metadata.';
comment on table public.urban_channex_room_owners is 'One current owner per Channex Room Type per environment. Remote room/property IDs are verified by the portal before linking.';
notify pgrst, 'reload schema';
