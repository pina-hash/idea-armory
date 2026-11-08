-- The website's 0388 armory_web_trash body verbatim. Minimal class-folder fixture:
-- only this test table and refusal helper stand in for 0319's roster machinery.
create table public.armory_folders(project_id uuid, folder text, owner_email text, placeholder_file_id uuid);
create function public._armory_refuse_folder_write(p_project uuid,p_folder text) returns void
language plpgsql security definer set search_path = '' as $$
begin
 if exists(select 1 from public.armory_folders where project_id=p_project
  and (lower(p_folder)=lower(folder) or left(lower(p_folder),length(folder)+1)=lower(folder)||'/')
  and owner_email is not null and owner_email<>public.armory_current_email()) then
  raise exception 'Save your work in your own folder.' using errcode='PT403';
 end if;
end $$;
create or replace function public.armory_web_trash(p_file uuid, p_restore boolean, p_operation uuid) returns boolean
language plpgsql security definer set search_path = '' as $trash$
declare
 e text := public.armory_current_email();
 p uuid;
 f public.armory_files;
 r jsonb;
 answer boolean := false;
begin
 select project_id into p from public.armory_files where id = p_file;
 perform 1 from public.armory_projects where id = p for update;
 if not public.armory_is_member(p) then
  raise exception 'not a project member' using errcode = '42501', detail = jsonb_build_object('reason', 'not_member')::text;
 end if;
 select * into f from public.armory_files where id = p_file for update;
 perform public._armory_refuse_folder_write(p, f.folder);
 if p_restore is null then raise exception 'Choose trash or restore.' using errcode = '22023', detail = jsonb_build_object('reason', 'action')::text; end if;
 if exists(select 1 from public.armory_projects where id = p and archived_at is not null) then
  raise exception 'Restore the project first.' using errcode = '55000', detail = jsonb_build_object('reason', 'project_archived')::text;
 end if;
 if exists(select 1 from public.armory_folders where placeholder_file_id = p_file) then
  raise exception 'This note keeps the class folder in place.' using errcode = '55000', detail = jsonb_build_object('reason', 'folder_note')::text;
 end if;
 r := public.armory_replay(p_operation, 'armory_web_trash');
 if r is not null then return (r->>'result')::boolean; end if;
 if exists(select 1 from public.armory_locks where file_id = p_file and broken_at is null) then
  raise exception 'Check in this file before moving it.' using errcode = '55006', detail = jsonb_build_object('reason', 'checked_out')::text;
 end if;
 if p_restore and f.deleted_at is not null then
  if f.deleted_at <= now() - interval '30 days' then
   raise exception 'The 30-day restore time has ended.' using errcode = '55000', detail = jsonb_build_object('reason', 'restore_expired')::text;
  end if;
  perform public.armory_live_name_taken(p, f.name, p_file);
  update public.armory_files set deleted_at = null where id = p_file;
  perform public.armory_add_change(p, 'file_revived', p_file, jsonb_build_object('by', e, 'folder', f.folder, 'name', f.name, 'source', 'website'));
  answer := true;
 elsif not p_restore and f.deleted_at is null then
  insert into public.armory_tombstones(file_id, version_id, author_email)
   values(p_file, f.current_version_id, e) on conflict do nothing;
  update public.armory_files set deleted_at = now() where id = p_file;
  perform public.armory_add_change(p, 'tombstone', p_file, jsonb_build_object('by', e, 'source', 'website'));
  answer := true;
 end if;
 perform public.armory_remember(p_operation, 'armory_web_trash', jsonb_build_object('result', answer));
 return answer;
end $trash$;


revoke all on function public.armory_web_trash(uuid,boolean,uuid) from public,anon;
grant execute on function public.armory_web_trash(uuid,boolean,uuid) to authenticated;
