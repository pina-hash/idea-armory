-- No direct writes. RPC owners perform writes after their own authorization checks.
revoke all on public.armory_projects,public.armory_members,public.armory_devices,public.armory_files,public.armory_versions,
 public.armory_side_versions,public.armory_locks,public.armory_tombstones,
 public.armory_part_number_allocations,public.armory_change_feed,public.armory_operation_receipts from public,anon,authenticated;
grant select on public.armory_projects,public.armory_members,public.armory_files,public.armory_versions,
 public.armory_side_versions,public.armory_locks,public.armory_tombstones,
 public.armory_part_number_allocations,public.armory_change_feed to authenticated;
grant select on public.armory_devices,public.armory_operation_receipts to authenticated;

alter table public.armory_projects enable row level security;
alter table public.armory_members enable row level security;
alter table public.armory_devices enable row level security;
alter table public.armory_files enable row level security;
alter table public.armory_versions enable row level security;
alter table public.armory_side_versions enable row level security;
alter table public.armory_locks enable row level security;
alter table public.armory_tombstones enable row level security;
alter table public.armory_part_number_allocations enable row level security;
alter table public.armory_change_feed enable row level security;
alter table public.armory_operation_receipts enable row level security;
create policy armory_projects_read on public.armory_projects for select to authenticated using(public.armory_is_member(id));
create policy armory_members_read on public.armory_members for select to authenticated using(public.armory_is_member(project_id));
create policy armory_devices_read on public.armory_devices for select to authenticated using(owner_email=public.armory_current_email());
create policy armory_files_read on public.armory_files for select to authenticated using(public.armory_is_member(project_id));
create policy armory_versions_read on public.armory_versions for select to authenticated using(exists(select 1 from public.armory_files f where f.id=file_id and public.armory_is_member(f.project_id)));
create policy armory_side_versions_read on public.armory_side_versions for select to authenticated using(exists(select 1 from public.armory_files f where f.id=file_id and public.armory_is_member(f.project_id)));
create policy armory_locks_read on public.armory_locks for select to authenticated using(exists(select 1 from public.armory_files f where f.id=file_id and public.armory_is_member(f.project_id)));
create policy armory_tombstones_read on public.armory_tombstones for select to authenticated using(exists(select 1 from public.armory_files f where f.id=file_id and public.armory_is_member(f.project_id)));
create policy armory_allocations_read on public.armory_part_number_allocations for select to authenticated using(public.armory_is_member(project_id));
create policy armory_changes_read on public.armory_change_feed for select to authenticated using(public.armory_is_member(project_id));
create policy armory_receipts_read on public.armory_operation_receipts for select to authenticated using(caller_email=public.armory_current_email());
