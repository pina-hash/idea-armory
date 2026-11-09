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

create or replace function public.armory_can_take_back(p_project uuid) returns boolean
language sql stable security definer set search_path = '' as $at$
	select coalesce(public.is_admin(), false)
		or exists(
			select 1 from public.armory_members m
			where m.project_id = p_project
				and m.email = public.current_user_email()
				and m.role in ('mentor', 'cad_lead', 'instructor'))
$at$;

-- idea-app 0319's move body verbatim, so drops exercise both class-folder boundaries.
create or replace function public.armory_move_file(p_file uuid, p_folder text, p_name text, p_device uuid, p_operation uuid) returns boolean
language plpgsql security definer set search_path = '' as $ac$
declare e text := public.armory_current_email(); r jsonb; f public.armory_files; folder_n text; name_n text; answer boolean; p uuid;
	mine boolean; other text;
begin
	r := public.armory_replay(p_operation, 'armory_move_file');
	if r is not null then return (r->>'result')::boolean; end if;
	perform public.armory_require_device(p_device);
	-- 0233: the project row before the file row.
	select project_id into p from public.armory_files where id = p_file;
	perform 1 from public.armory_projects where id = p for key share;
	select * into f from public.armory_files where id = p_file for update;
	if not found or not public.armory_is_member(f.project_id) then raise exception 'not a project member' using errcode = '42501'; end if;
	folder_n := normalize(coalesce(p_folder, ''), NFC);
	name_n := normalize(coalesce(p_name, ''), NFC);
	if not public.armory_valid_folder(folder_n) then raise exception 'The folder "%" cannot be a Windows folder path.', folder_n using errcode = '22023'; end if;
	if not public.armory_valid_segment(name_n) then raise exception 'The name "%" cannot be a Windows file name.', name_n using errcode = '22023'; end if;
	mine := exists(select 1 from public.armory_locks where file_id = p_file and holder_email = e and holder_device_id = p_device and broken_at is null);
	-- 0236: a lead may move a file someone else holds; the lock stays theirs.
	if not mine and public.armory_can_take_back(f.project_id) then
		select holder_email into other from public.armory_locks where file_id = p_file and broken_at is null;
	end if;
	answer := f.deleted_at is null and (mine or other is not null);
	if answer and (f.folder <> folder_n or f.name <> name_n) then
		-- 0319: a file moves out of a class folder and into one only for that folder's writers.
		perform public._armory_refuse_folder_write(f.project_id, f.folder);
		perform public._armory_refuse_folder_write(f.project_id, folder_n);
		perform public.armory_name_taken(f.project_id, name_n, p_file);
		begin
			update public.armory_files set folder = folder_n, name = name_n where id = p_file;
		exception when unique_violation then
			perform public.armory_name_taken(f.project_id, name_n, p_file);
			raise;
		end;
		perform public.armory_add_change(f.project_id, 'file_moved', p_file,
			jsonb_build_object('old_folder', f.folder, 'old_name', f.name, 'folder', folder_n, 'name', name_n, 'device_id', p_device, 'by', e)
			-- 0236: said only when the move went over someone else's checkout.
			|| case when mine then '{}'::jsonb else jsonb_build_object('checked_out_by', other) end);
	end if;
	perform public.armory_remember(p_operation, 'armory_move_file', jsonb_build_object('result', answer));
	return answer;
end $ac$;
