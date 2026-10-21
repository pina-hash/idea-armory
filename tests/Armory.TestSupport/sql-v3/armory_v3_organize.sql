-- idea-app migration 0236 (supabase/migrations/0236_armory_v033.sql, ledger 0377), the parts the
-- Windows app calls, COPIED AS THEY ARE: lines 71 to 131 (armory_can_take_back, and
-- armory_my_projects and armory_break_lock asking it) and lines 183 to 281 (a lead organizes files
-- someone else has checked out: armory_move_file and armory_rename_folder). Applied over this
-- database's 0232/0233 bodies by ArmoryV3StandIn.ApplyOrganizeAsync, so the agent is tested
-- against the server production runs. Left out: armory_add_member and armory_remove_empty_file
-- (the app calls neither) and the incidents' machine_id column (no incidents table here). The
-- grants are 0236's own for the one new function.
-- ---------------------------------------------------------------------------
-- 0. The one take-back predicate, first, so the object the deploy probe
-- checks is new here. Granted to authenticated as well, so the website asks
-- the server's own answer rather than restating the role list.
-- ---------------------------------------------------------------------------

create or replace function public.armory_can_take_back(p_project uuid) returns boolean
language sql stable security definer set search_path = '' as $at$
	select coalesce(public.is_admin(), false)
		or exists(
			select 1 from public.armory_members m
			where m.project_id = p_project
				and m.email = public.current_user_email()
				and m.role in ('mentor', 'cad_lead', 'instructor'))
$at$;

-- ---------------------------------------------------------------------------
-- 1. The three readers of it. Each body is the 0233 body with only the lines
-- marked 0236 changed.
-- ---------------------------------------------------------------------------

-- Membership only: the app syncs exactly the projects this lists.
create or replace function public.armory_my_projects() returns jsonb
language plpgsql stable security definer set search_path = '' as $at$
declare e text := public.armory_current_email();
begin
	return coalesce((
		select jsonb_agg(jsonb_build_object('id', p.id, 'name', p.name, 'season', p.season, 'role', m.role,
			'pinned_release', p.pinned_release, 'release_gate', p.release_gate,
			'archived', p.archived_at is not null, 'archived_at', p.archived_at,
			-- 0236: the one predicate (mentor, CAD lead, instructor, or a site admin).
			'can_take_back', public.armory_can_take_back(p.id)) order by lower(p.name), p.id)
		from public.armory_projects p join public.armory_members m on m.project_id = p.id and m.email = e
	), '[]'::jsonb);
end $at$;

create or replace function public.armory_break_lock(p_file uuid, p_device uuid, p_operation uuid) returns boolean
language plpgsql security definer set search_path = '' as $at$
declare e text := public.armory_current_email(); p uuid; n int; r jsonb; answer boolean; old_email text; old_device uuid;
begin
	r := public.armory_replay(p_operation, 'armory_break_lock');
	if r is not null then return (r->>'result')::boolean; end if;
	-- 0233: the website has no computer, so a null device is allowed; a named device must still be the caller's.
	if p_device is not null then perform public.armory_require_device(p_device); end if;
	select project_id into p from public.armory_files where id = p_file;
	perform 1 from public.armory_projects where id = p for key share;
	-- 0236: the one predicate. The refusal text is 0231's, unchanged, because the app reads it.
	if not public.armory_can_take_back(p) then
		raise exception 'only a mentor or cad_lead may break a lock';
	end if;
	select holder_email, holder_device_id into old_email, old_device from public.armory_locks where file_id = p_file and broken_at is null for update;
	update public.armory_locks set broken_at = now(), broken_by = e, broken_holder_email = holder_email, broken_holder_device_id = holder_device_id
	where file_id = p_file and broken_at is null;
	get diagnostics n = row_count; answer := n > 0;
	if answer then
		perform public.armory_add_change(p, 'lock_broken', p_file, jsonb_build_object('by', e, 'former_holder', old_email, 'former_device_id', old_device));
	end if;
	perform public.armory_remember(p_operation, 'armory_break_lock', jsonb_build_object('result', answer));
	return answer;
end $at$;


-- ---------------------------------------------------------------------------
-- 3. A lead organizes files someone else has checked out. armory_move_file is
-- 0233's body and armory_rename_folder 0232's, with only the marked lines
-- changed. The lock is never read for update or written here: it belongs to
-- the file id, and a move changes only the folder and the name.
-- ---------------------------------------------------------------------------

create or replace function public.armory_move_file(p_file uuid, p_folder text, p_name text, p_device uuid, p_operation uuid) returns boolean
language plpgsql security definer set search_path = '' as $at$
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
end $at$;

create or replace function public.armory_rename_folder(p_project uuid, p_from text, p_to text, p_device uuid, p_operation uuid) returns int
language plpgsql security definer set search_path = '' as $at$
declare e text := public.armory_current_email(); r jsonb; from_n text; to_n text; ids uuid[]; names text[]; total int; moved int;
	lead boolean; held int := 0;
begin
	r := public.armory_replay(p_operation, 'armory_rename_folder');
	if r is not null then return (r->>'result')::int; end if;
	perform public.armory_require_device(p_device);
	if not public.armory_is_member(p_project) then raise exception 'not a project member' using errcode = '42501'; end if;
	from_n := normalize(coalesce(p_from, ''), NFC);
	to_n := normalize(coalesce(p_to, ''), NFC);
	if from_n = '' or not public.armory_valid_folder(from_n) then raise exception 'The folder "%" cannot be a Windows folder path.', from_n using errcode = '22023'; end if;
	if to_n = '' or not public.armory_valid_folder(to_n) then raise exception 'The folder "%" cannot be a Windows folder path.', to_n using errcode = '22023'; end if;
	if to_n = from_n then raise exception 'The folder already has that name.' using errcode = '22023'; end if;
	if left(lower(to_n), length(from_n) + 1) = lower(from_n) || '/' then raise exception 'A folder cannot move inside itself.' using errcode = '22023'; end if;
	-- One folder operation at a time in a project.
	perform 1 from public.armory_projects where id = p_project for update;
	select array_agg(f.id) into ids from public.armory_folder_files(p_project, from_n) f;
	if ids is null then
		moved := 0;
	else
		-- 0236: a lead renames over other people's checkouts; everyone else is refused as before.
		lead := public.armory_can_take_back(p_project);
		if lead then
			select count(*) into held from public.armory_locks l
			where l.file_id = any(ids) and l.broken_at is null and (l.holder_email <> e or l.holder_device_id <> p_device);
		else
			perform public.armory_refuse_checked_out(ids, p_device, from_n);
		end if;
		-- The target already holds live files that are not being moved (a case-only
		-- rename of the same folder moves every file in it, so it never trips this).
		select array_agg(x.name order by lower(x.name)), count(*) into names, total from (
			select f.name from public.armory_files f
			where f.project_id = p_project and f.deleted_at is null and not (f.id = any(ids))
				and (lower(f.folder) = lower(to_n) or left(lower(f.folder), length(to_n) + 1) = lower(to_n) || '/')
		) x;
		if total > 0 then
			raise exception 'The folder "%" already has % file(s) in it: %.', to_n, total, array_to_string(names[1:10], ', ')
				using errcode = '55006',
				detail = jsonb_build_object('reason', 'target_exists', 'names', to_jsonb(names[1:10]), 'total', total)::text,
				hint = 'Choose another folder name.';
		end if;
		update public.armory_files set folder = to_n || substr(folder, length(from_n) + 1) where id = any(ids);
		moved := cardinality(ids);
		perform public.armory_add_change(p_project, 'folder_renamed', p_project,
			jsonb_build_object('from', from_n, 'to', to_n, 'files', moved, 'device_id', p_device, 'by', e)
			-- 0236: said only when the rename went over someone else's checkouts.
			|| case when held > 0 then jsonb_build_object('over_checkouts', held) else '{}'::jsonb end);
	end if;
	perform public.armory_remember(p_operation, 'armory_rename_folder', jsonb_build_object('result', moved));
	return moved;
end $at$;

revoke all on function public.armory_can_take_back(uuid) from public, anon, authenticated;
grant execute on function public.armory_can_take_back(uuid) to authenticated;
