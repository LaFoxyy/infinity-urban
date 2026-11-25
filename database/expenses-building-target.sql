alter table public.urban_channex_expenses drop constraint urban_channex_expenses_check;
alter table public.urban_channex_expenses add constraint urban_expenses_room_requires_property check(room_type_id is null or property_id is not null);
comment on column public.urban_channex_expenses.property_id is 'Channex building ID: with room_type_id is the apartment parent; without room_type_id is an expense for the building itself.';
notify pgrst,'reload schema';
