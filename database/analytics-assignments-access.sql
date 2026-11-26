-- Only Infinity Urban/Channex account designations; no changes to legacy Guesty tables.
alter policy urban_assignments_read on public.urban_channex_assignments using (
  (select urban_private.urban_current_can_manage_assignments())
  or (
    user_id=(select auth.uid())
    and exists(select 1 from public.profiles p where p.id=(select auth.uid()) and p.status::text='approved')
    and exists(select 1 from public.user_roles r where r.user_id=(select auth.uid()) and r.role::text in ('proprietario','funcionario'))
  )
);
alter policy urban_assignments_insert on public.urban_channex_assignments with check (
  (select urban_private.urban_current_can_manage_assignments())
  and exists(select 1 from public.profiles p where p.id=user_id and p.status::text='approved')
  and exists(select 1 from public.user_roles r where r.user_id=urban_channex_assignments.user_id and r.role::text in ('proprietario','funcionario'))
  and not exists(select 1 from public.user_roles r where r.user_id=urban_channex_assignments.user_id and r.role::text in ('master','admin'))
);
notify pgrst,'reload schema';
