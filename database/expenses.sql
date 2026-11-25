create table public.urban_channex_expenses (
 id uuid primary key default gen_random_uuid(),
 environment text not null check(environment in ('production','staging')),
 name text not null check(length(btrim(name)) between 1 and 180),
 kind text not null check(kind in ('monthly','per_stay','one_off')),
 amount_cents bigint not null check(amount_cents between 1 and 99999999999),
 currency text not null check(currency ~ '^[A-Z]{3}$'),
 category_id uuid references public.expense_categories(id) on delete set null,
 room_type_id uuid,
 property_id uuid,
 starts_on date not null check(starts_on between '2000-01-01'::date and '2100-12-31'::date),
 ends_on date check(ends_on between '2000-01-01'::date and '2100-12-31'::date),
 description text check(description is null or length(description)<=10000),
 revision uuid not null default gen_random_uuid(),
 created_by uuid default auth.uid() references auth.users(id) on delete set null,
 created_at timestamptz not null default now(),
 updated_at timestamptz not null default now(),
 check((room_type_id is null)=(property_id is null)),
 check(ends_on is null or ends_on>=starts_on),
 check((kind='one_off' and ends_on is null) or (kind in ('monthly','per_stay') and extract(day from starts_on)=1 and (ends_on is null or extract(day from ends_on)=1)))
);
create index urban_channex_expenses_name_idx on public.urban_channex_expenses(environment,name,id);
create index urban_channex_expenses_room_idx on public.urban_channex_expenses(environment,room_type_id);
create index urban_channex_expenses_category_idx on public.urban_channex_expenses(category_id);
create index urban_channex_expenses_creator_idx on public.urban_channex_expenses(created_by);
alter table public.urban_channex_expenses enable row level security;
revoke all on public.urban_channex_expenses from public,anon,authenticated;
grant select,delete on public.urban_channex_expenses to authenticated;
grant insert(id,environment,name,kind,amount_cents,currency,category_id,room_type_id,property_id,starts_on,ends_on,description) on public.urban_channex_expenses to authenticated;
grant update(name,kind,amount_cents,currency,category_id,room_type_id,property_id,starts_on,ends_on,description,revision,updated_at) on public.urban_channex_expenses to authenticated;
create policy urban_expenses_read on public.urban_channex_expenses for select to authenticated using ((select urban_private.urban_current_is_operator()));
create policy urban_expenses_insert on public.urban_channex_expenses for insert to authenticated with check ((select urban_private.urban_current_can_manage_assignments()));
create policy urban_expenses_update on public.urban_channex_expenses for update to authenticated using ((select urban_private.urban_current_can_manage_assignments())) with check ((select urban_private.urban_current_can_manage_assignments()));
create policy urban_expenses_delete on public.urban_channex_expenses for delete to authenticated using ((select urban_private.urban_current_can_manage_assignments()));
comment on table public.urban_channex_expenses is 'Internal recurring monthly/per-stay rules and one-off expenses. Linked optionally to a Channex Room Type; monetary amounts stored as exact integer cents. Independent of Guesty expenses and not sent to Channex.';
notify pgrst,'reload schema';
