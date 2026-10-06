-- Draft contract only. idea-app numbered it 0232: supabase/migrations/0232_armory_v2.sql on
-- idea-app main afb370f (lane W, ledger 0366), which landed before lane A wrote its own.
-- Lines 13 to 477 of this file are that migration copied byte for byte (SHA-256 of the
-- copied text: cba64901279e4469edf1d7f4d91a4ad5e5359c816810f5433ce38d61f5ef48a9), so this
-- repo writes no function body of its own for C1 to C7 and the tests here exercise the exact
-- SQL production runs. public.profiles, which armory_project_checkouts reads, is idea-app's
-- table; the tests stand it in with tests/Armory.Server.Tests/sql/002_test_profiles.sql.
-- Section 9 at the end is the only text that is not 0232's: the grants production already got
-- from 0231 (its section 7), which this repo's 001 to 004 predate. docs/server/contract.md
-- (v2) lists where 0232 differs from lane A's design notes.
-- ---------------------------------------------------------------------------

-- ---------------------------------------------------------------------------
-- 0232  IDEA ARMORY v2: PROJECTS WITHOUT A SEASON, RENAME AND ARCHIVE, NAME
--       REVIVAL, FOLDER RENAME AND DELETE, AND THE PROJECT'S CHECKOUT LIST.
--
-- Ledger 0366 (lane W), approved by Mr. Pina 2026-10-06. Lane A rebuilds the
-- Windows agent in pina-hash/idea-armory against the SAME contract, so every
-- signature and change kind below is the contract's (C1 to C7) and neither
-- lane may move one:
--
--   C1  armory_projects.season is nullable. armory_create_project keeps its
--       exact signature (text, smallint, uuid) and accepts a null season. A
--       season, when given, is still a year from 2000 to 2100.
--       armory_allocate_part_number reads the season and is the one reader
--       that cannot tolerate null as it stood: with no season on the call and
--       none on the project it now uses the current year in Los Angeles.
--   C2  armory_rename_project(p_project, p_name, p_operation) returns boolean.
--       Mentor only, the create rules for the name, change 'project_renamed'
--       with from and to. A name that only changes case is allowed.
--   C3  armory_set_project_archived(p_project, p_archived, p_operation)
--       returns boolean. Mentor only. armory_projects.archived_at records it;
--       armory_my_projects projects 'archived' and 'archived_at'. Nothing is
--       deleted. Changes 'project_archived' and 'project_restored'.
--   C4  armory_create_file on a name whose only holder in the project is a
--       TOMBSTONED file revives that file: the tombstone row is cleared,
--       deleted_at is cleared, the requested folder (and spelling) is set, any
--       checkout left on the removed file is released (the agent does not
--       release after a tombstone, and a lingering checkout would turn every
--       later save into a side version), and the SAME file id is returned, so
--       its history continues. Change 'file_revived'. A live clash still
--       raises 23505 with the existing folder in DETAIL, from the same helper.
--   C5  armory_rename_folder(p_project, p_from, p_to, p_device, p_operation)
--       returns int (files moved). Any member. Moves every live file whose
--       folder is p_from or starts with p_from and a slash.
--   C6  armory_delete_folder(p_project, p_folder, p_device, p_operation)
--       returns int (files tombstoned). Any member.
--       Both refuse with SQLSTATE 55006 when any of those files is checked out
--       by someone else (a different person, or the caller on a different
--       computer), and C5 also when the target folder already holds live files
--       (Windows paths ignore case, so a target spelled differently is the same
--       folder). DETAIL is jsonb text, the 0231 shape:
--       reason, names (at most 10) and total.
--       One change each: 'folder_renamed' and 'folder_deleted'.
--   C7  armory_project_checkouts(p_project) returns jsonb, for members: one
--       element per live file with an unbroken checkout, carrying file_id,
--       folder, name, holder_email, holder_name, device_name and since.
--       holder_name is the holder's chosen display name, else their full name,
--       else null, read inside this definer function; profiles stays shut.
--   C8  unchanged: check out is armory_acquire_lock, check in is a commit then
--       a release, take back is armory_break_lock.
--
-- ONE REPAIR TO 0231 RIDES HERE, AND IT IS NOT IN THE CONTRACT BECAUSE IT
-- CHANGES NO SIGNATURE. armory_change_feed.cursor is generated always as
-- identity, so its sequence inherited the project's default
-- grant all on sequences to anon and authenticated (the 0203 shape), and
-- 0231 revoked only the tables. tests/grant-surface.test.ts section D has read
-- red on main since 0231 landed. Section 7 below revokes it by name.
--
-- CONVENTIONS, 0231's exactly: security definer, search_path empty, identity
-- only from current_user_email() through armory_current_email(), operation
-- receipts on every write, change-feed entries, and grants that name their
-- roles (revoke from public, anon and authenticated, then grant to
-- authenticated). Every function this file replaces keeps its signature, so
-- no client changes are ordered against the apply.
--
-- UNDO, before any client depends on it (the sequence revoke is never undone): re-create armory_create_project,
-- armory_allocate_part_number, armory_create_file and armory_my_projects from
-- 0231, drop the five new functions, and set a season on any project that has
-- none before restoring the not-null constraint. archived_at may stay.
-- ---------------------------------------------------------------------------

-- ---------------------------------------------------------------------------
-- 1. The two column changes.
-- ---------------------------------------------------------------------------

alter table public.armory_projects add column if not exists archived_at timestamptz;
alter table public.armory_projects alter column season drop not null;

-- ---------------------------------------------------------------------------
-- 2. C1: create without a season; part numbers with no season use this year.
-- ---------------------------------------------------------------------------

create or replace function public.armory_create_project(p_name text, p_season smallint, p_operation uuid) returns uuid
language plpgsql security definer set search_path = '' as $$
declare e text := public.armory_current_email(); r jsonb; n text; v uuid; existing text;
begin
	r := public.armory_replay(p_operation, 'armory_create_project');
	if r is not null then return (r->>'project_id')::uuid; end if;
	if not coalesce(public.is_admin(), false) then raise exception 'only a site admin may create an Armory project' using errcode = '42501'; end if;
	n := normalize(coalesce(p_name, ''), NFC);
	if not public.armory_valid_segment(n) then raise exception 'The project name "%" cannot be a Windows folder name.', n using errcode = '22023'; end if;
	if p_season is not null and p_season not between 2000 and 2100 then raise exception 'season must be a year from 2000 to 2100' using errcode = '22023'; end if;
	-- Each project is a folder under the vault root, so names are unique without regard to case.
	perform pg_advisory_xact_lock(hashtextextended('armory_project_name:' || lower(n), 0));
	select name into existing from public.armory_projects where lower(normalize(name, NFC)) = lower(n) limit 1;
	if found then raise exception 'A project named "%" already exists.', existing using errcode = '23505', detail = jsonb_build_object('existing_name', existing)::text; end if;
	insert into public.armory_projects(name, season) values (n, p_season) returning id into v;
	insert into public.armory_members(project_id, email, role) values (v, e, 'mentor');
	perform public.armory_add_change(v, 'project_created', v, jsonb_build_object('name', n, 'season', p_season, 'by', e));
	perform public.armory_remember(p_operation, 'armory_create_project', jsonb_build_object('project_id', v));
	return v;
end $$;

create or replace function public.armory_allocate_part_number(p_project uuid, p_subsystem int, p_season int, p_operation uuid)
returns table(part_number text, subsystem_full boolean)
language plpgsql security definer set search_path = '' as $$
declare e text := public.armory_current_email(); s int; n int; pat text; r jsonb;
begin
	r := public.armory_replay(p_operation, 'armory_allocate_part_number');
	if r is not null then part_number := r->>'part_number'; subsystem_full := (r->>'subsystem_full')::boolean; return next; return; end if;
	if not public.armory_is_member(p_project) then raise exception 'not a project member'; end if;
	if p_subsystem not between 0 and 99 then raise exception 'invalid subsystem'; end if;
	-- A project with no season (0232) numbers its parts by the current year in Los Angeles.
	select coalesce(p_season, season, extract(year from (now() at time zone 'America/Los_Angeles'))::int), part_number_pattern into s, pat
	from public.armory_projects where id = p_project for update;
	select x into n from generate_series(0, 99) x
	where not exists(select 1 from public.armory_part_number_allocations where project_id = p_project and season = s and subsystem = p_subsystem and sequence = x)
	limit 1;
	if n is null then
		part_number := null; subsystem_full := true;
	else
		part_number := replace(replace(replace(replace(pat, '{YY}', lpad((s % 100)::text, 2, '0')), '{SS}', lpad(p_subsystem::text, 2, '0')), '{NN}', lpad(n::text, 2, '0')), 'YYYY', s::text);
		insert into public.armory_part_number_allocations(project_id, season, subsystem, sequence, part_number, allocated_by)
		values (p_project, s, p_subsystem, n, part_number, e);
		subsystem_full := false;
	end if;
	perform public.armory_remember(p_operation, 'armory_allocate_part_number', jsonb_build_object('part_number', part_number, 'subsystem_full', subsystem_full));
	return next;
end $$;

-- ---------------------------------------------------------------------------
-- 3. C2 and C3: rename, archive and restore a project. Mentor only.
-- ---------------------------------------------------------------------------

create or replace function public.armory_rename_project(p_project uuid, p_name text, p_operation uuid) returns boolean
language plpgsql security definer set search_path = '' as $$
declare e text := public.armory_current_email(); r jsonb; n text; old text; existing text; answer boolean;
begin
	r := public.armory_replay(p_operation, 'armory_rename_project');
	if r is not null then return (r->>'result')::boolean; end if;
	perform public.armory_require_role(p_project, array['mentor']::public.armory_member_role[], 'only a mentor may rename the project');
	n := normalize(coalesce(p_name, ''), NFC);
	if not public.armory_valid_segment(n) then raise exception 'The project name "%" cannot be a Windows folder name.', n using errcode = '22023'; end if;
	perform pg_advisory_xact_lock(hashtextextended('armory_project_name:' || lower(n), 0));
	select name into old from public.armory_projects where id = p_project for update;
	select name into existing from public.armory_projects where lower(normalize(name, NFC)) = lower(n) and id <> p_project limit 1;
	if found then raise exception 'A project named "%" already exists.', existing using errcode = '23505', detail = jsonb_build_object('existing_name', existing)::text; end if;
	answer := old is distinct from n;
	if answer then
		update public.armory_projects set name = n where id = p_project;
		perform public.armory_add_change(p_project, 'project_renamed', p_project, jsonb_build_object('from', old, 'to', n, 'by', e));
	end if;
	perform public.armory_remember(p_operation, 'armory_rename_project', jsonb_build_object('result', answer));
	return answer;
end $$;

create or replace function public.armory_set_project_archived(p_project uuid, p_archived boolean, p_operation uuid) returns boolean
language plpgsql security definer set search_path = '' as $$
declare e text := public.armory_current_email(); r jsonb; was timestamptz; answer boolean;
begin
	r := public.armory_replay(p_operation, 'armory_set_project_archived');
	if r is not null then return (r->>'result')::boolean; end if;
	perform public.armory_require_role(p_project, array['mentor']::public.armory_member_role[], 'only a mentor may archive or restore the project');
	if p_archived is null then raise exception 'archived is true or false' using errcode = '22023'; end if;
	select archived_at into was from public.armory_projects where id = p_project for update;
	answer := (was is not null) <> p_archived;
	if answer then
		update public.armory_projects set archived_at = case when p_archived then now() else null end where id = p_project;
		perform public.armory_add_change(p_project, case when p_archived then 'project_archived' else 'project_restored' end, p_project,
			jsonb_build_object('archived', p_archived, 'by', e));
	end if;
	perform public.armory_remember(p_operation, 'armory_set_project_archived', jsonb_build_object('result', answer));
	return answer;
end $$;

create or replace function public.armory_my_projects() returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare e text := public.armory_current_email();
begin
	return coalesce((
		select jsonb_agg(jsonb_build_object('id', p.id, 'name', p.name, 'season', p.season, 'role', m.role,
			'pinned_release', p.pinned_release, 'release_gate', p.release_gate,
			'archived', p.archived_at is not null, 'archived_at', p.archived_at) order by lower(p.name), p.id)
		from public.armory_projects p join public.armory_members m on m.project_id = p.id and m.email = e
	), '[]'::jsonb);
end $$;

-- ---------------------------------------------------------------------------
-- 4. C4: a name held only by a removed file revives that file.
-- ---------------------------------------------------------------------------

-- The 23505 refusal for a LIVE file, 0231's armory_name_taken narrowed to live
-- rows. Same message, same DETAIL keys, so the agent reads one shape.
create or replace function public.armory_live_name_taken(p_project uuid, p_name text, p_except uuid) returns void
language plpgsql stable security definer set search_path = '' as $$
declare f public.armory_files;
begin
	select * into f from public.armory_files
	where project_id = p_project and lower(normalize(name, NFC)) = lower(normalize(p_name, NFC)) and id is distinct from p_except
		and deleted_at is null
	limit 1;
	if found then
		raise exception 'A file named "%" already exists in this project.', f.name using errcode = '23505',
			detail = jsonb_build_object('existing_folder', f.folder, 'existing_name', f.name, 'file_id', f.id)::text,
			hint = 'Choose another name, or open the existing file.';
	end if;
end $$;

create or replace function public.armory_create_file(p_project uuid, p_folder text, p_name text, p_device uuid, p_operation uuid) returns uuid
language plpgsql security definer set search_path = '' as $$
declare e text := public.armory_current_email(); r jsonb; folder_n text; name_n text; v uuid; dead public.armory_files; holder text;
begin
	r := public.armory_replay(p_operation, 'armory_create_file');
	if r is not null then return (r->>'file_id')::uuid; end if;
	perform public.armory_require_device(p_device);
	if not public.armory_is_member(p_project) then raise exception 'not a project member' using errcode = '42501'; end if;
	folder_n := normalize(coalesce(p_folder, ''), NFC);
	name_n := normalize(coalesce(p_name, ''), NFC);
	if not public.armory_valid_folder(folder_n) then raise exception 'The folder "%" cannot be a Windows folder path.', folder_n using errcode = '22023'; end if;
	if not public.armory_valid_segment(name_n) then raise exception 'The name "%" cannot be a Windows file name.', name_n using errcode = '22023'; end if;
	-- Serialize creators of one name, so a revival and a concurrent create cannot both win.
	perform pg_advisory_xact_lock(hashtextextended('armory_file_name:' || p_project::text || ':' || lower(name_n), 0));
	perform public.armory_live_name_taken(p_project, name_n, null);
	select * into dead from public.armory_files
	where project_id = p_project and lower(normalize(name, NFC)) = lower(name_n) and deleted_at is not null
	for update;
	if found then
		select holder_email into holder from public.armory_locks where file_id = dead.id and broken_at is null;
		delete from public.armory_locks where file_id = dead.id;
		delete from public.armory_tombstones where file_id = dead.id;
		update public.armory_files set deleted_at = null, folder = folder_n, name = name_n where id = dead.id;
		v := dead.id;
		perform public.armory_add_change(p_project, 'file_revived', v, jsonb_build_object('folder', folder_n, 'name', name_n,
			'old_folder', dead.folder, 'old_name', dead.name, 'released_checkout_of', holder, 'device_id', p_device, 'by', e));
	else
		begin
			insert into public.armory_files(project_id, folder, name) values (p_project, folder_n, name_n) returning id into v;
		exception when unique_violation then
			-- A concurrent creator won; name the folder where its file now lives.
			perform public.armory_name_taken(p_project, name_n, null);
			raise;
		end;
		perform public.armory_add_change(p_project, 'file_created', v, jsonb_build_object('folder', folder_n, 'name', name_n, 'device_id', p_device, 'by', e));
	end if;
	perform public.armory_remember(p_operation, 'armory_create_file', jsonb_build_object('file_id', v));
	return v;
end $$;

-- ---------------------------------------------------------------------------
-- 5. C5 and C6: rename and delete a folder, refused while someone else has a
-- file in it checked out.
-- ---------------------------------------------------------------------------

-- Live files at or under a folder, locked for update in a stable order.
create or replace function public.armory_folder_files(p_project uuid, p_folder text) returns setof public.armory_files
language plpgsql security definer set search_path = '' as $$
begin
	return query select * from public.armory_files
	where project_id = p_project and deleted_at is null and (folder = p_folder or left(folder, length(p_folder) + 1) = p_folder || '/')
	order by folder, lower(name), id
	for update;
end $$;

-- 55006 when any of the files is checked out by anyone but the caller on this
-- computer. DETAIL is jsonb text: reason, names (at most 10), total.
create or replace function public.armory_refuse_checked_out(p_files uuid[], p_device uuid, p_folder text) returns void
language plpgsql stable security definer set search_path = '' as $$
declare e text := public.armory_current_email(); names text[]; total int;
begin
	select array_agg(x.name order by lower(x.name)), count(*) into names, total from (
		select f.name from public.armory_files f join public.armory_locks l on l.file_id = f.id and l.broken_at is null
		where f.id = any(p_files) and (l.holder_email <> e or l.holder_device_id <> p_device)
	) x;
	if total > 0 then
		raise exception 'Someone else has % file(s) in "%" checked out: %.', total, p_folder, array_to_string(names[1:10], ', ')
			using errcode = '55006',
			detail = jsonb_build_object('reason', 'checked_out', 'names', to_jsonb(names[1:10]), 'total', total)::text,
			hint = 'Ask them to check the files in, or ask a mentor or CAD lead to take them back.';
	end if;
end $$;

create or replace function public.armory_rename_folder(p_project uuid, p_from text, p_to text, p_device uuid, p_operation uuid) returns int
language plpgsql security definer set search_path = '' as $$
declare e text := public.armory_current_email(); r jsonb; from_n text; to_n text; ids uuid[]; names text[]; total int; moved int;
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
		perform public.armory_refuse_checked_out(ids, p_device, from_n);
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
			jsonb_build_object('from', from_n, 'to', to_n, 'files', moved, 'device_id', p_device, 'by', e));
	end if;
	perform public.armory_remember(p_operation, 'armory_rename_folder', jsonb_build_object('result', moved));
	return moved;
end $$;

create or replace function public.armory_delete_folder(p_project uuid, p_folder text, p_device uuid, p_operation uuid) returns int
language plpgsql security definer set search_path = '' as $$
declare e text := public.armory_current_email(); r jsonb; folder_n text; ids uuid[]; removed int;
begin
	r := public.armory_replay(p_operation, 'armory_delete_folder');
	if r is not null then return (r->>'result')::int; end if;
	perform public.armory_require_device(p_device);
	if not public.armory_is_member(p_project) then raise exception 'not a project member' using errcode = '42501'; end if;
	folder_n := normalize(coalesce(p_folder, ''), NFC);
	if folder_n = '' or not public.armory_valid_folder(folder_n) then raise exception 'The folder "%" cannot be a Windows folder path.', folder_n using errcode = '22023'; end if;
	perform 1 from public.armory_projects where id = p_project for update;
	select array_agg(f.id) into ids from public.armory_folder_files(p_project, folder_n) f;
	if ids is null then
		removed := 0;
	else
		perform public.armory_refuse_checked_out(ids, p_device, folder_n);
		insert into public.armory_tombstones(file_id, version_id, author_email)
		select f.id, f.current_version_id, e from public.armory_files f where f.id = any(ids)
		on conflict do nothing;
		update public.armory_files set deleted_at = now() where id = any(ids);
		removed := cardinality(ids);
		perform public.armory_add_change(p_project, 'folder_deleted', p_project,
			jsonb_build_object('folder', folder_n, 'files', removed, 'device_id', p_device, 'by', e));
	end if;
	perform public.armory_remember(p_operation, 'armory_delete_folder', jsonb_build_object('result', removed));
	return removed;
end $$;

-- ---------------------------------------------------------------------------
-- 6. C7: who has what checked out, for the members of a project.
-- ---------------------------------------------------------------------------

create or replace function public.armory_project_checkouts(p_project uuid) returns jsonb
language plpgsql stable security definer set search_path = '' as $$
begin
	if not public.armory_is_member(p_project) then raise exception 'not a project member' using errcode = '42501'; end if;
	return coalesce((
		select jsonb_agg(jsonb_build_object(
			'file_id', f.id, 'folder', f.folder, 'name', f.name, 'holder_email', l.holder_email,
			'holder_name', (select coalesce(nullif(btrim(pr.display_name), ''), nullif(btrim(pr.full_name), ''))
				from public.profiles pr where lower(pr.email) = l.holder_email order by pr.id limit 1),
			'device_name', d.name, 'since', l.acquired_at)
			order by l.acquired_at, f.folder, lower(f.name), f.id)
		from public.armory_files f
		join public.armory_locks l on l.file_id = f.id and l.broken_at is null
		left join public.armory_devices d on d.id = l.holder_device_id
		where f.project_id = p_project and f.deleted_at is null
	), '[]'::jsonb);
end $$;

-- ---------------------------------------------------------------------------
-- 7. Grants, the 0166 shape.
-- ---------------------------------------------------------------------------

revoke all on function
	public.armory_live_name_taken(uuid, text, uuid),
	public.armory_folder_files(uuid, text),
	public.armory_refuse_checked_out(uuid[], uuid, text)
from public, anon, authenticated;

revoke all on function
	public.armory_create_project(text, smallint, uuid),
	public.armory_allocate_part_number(uuid, int, int, uuid),
	public.armory_my_projects(),
	public.armory_create_file(uuid, text, text, uuid, uuid),
	public.armory_rename_project(uuid, text, uuid),
	public.armory_set_project_archived(uuid, boolean, uuid),
	public.armory_rename_folder(uuid, text, text, uuid, uuid),
	public.armory_delete_folder(uuid, text, uuid, uuid),
	public.armory_project_checkouts(uuid)
from public, anon, authenticated;
grant execute on function
	public.armory_create_project(text, smallint, uuid),
	public.armory_allocate_part_number(uuid, int, int, uuid),
	public.armory_my_projects(),
	public.armory_create_file(uuid, text, text, uuid, uuid),
	public.armory_rename_project(uuid, text, uuid),
	public.armory_set_project_archived(uuid, boolean, uuid),
	public.armory_rename_folder(uuid, text, text, uuid, uuid),
	public.armory_delete_folder(uuid, text, uuid, uuid),
	public.armory_project_checkouts(uuid)
to authenticated;

-- The 0231 repair: the change feed's identity sequence, revoked BY NAME (0203).
revoke all on sequence public.armory_change_feed_cursor_seq from public, anon, authenticated;

-- ---------------------------------------------------------------------------
-- 8. Self-check, by NAME over the objects this file writes and nothing else.
-- ---------------------------------------------------------------------------

do $$
declare
	v_new text[] := array['armory_rename_project', 'armory_set_project_archived', 'armory_rename_folder',
		'armory_delete_folder', 'armory_project_checkouts'];
	v_helpers text[] := array['armory_live_name_taken', 'armory_folder_files', 'armory_refuse_checked_out'];
	v_replaced text[] := array['armory_create_project', 'armory_allocate_part_number', 'armory_my_projects', 'armory_create_file'];
	v_name text;
	v_count integer;
begin
	foreach v_name in array v_new || v_helpers || v_replaced loop
		select count(*) into v_count from pg_proc p join pg_namespace n on n.oid = p.pronamespace
		where n.nspname = 'public' and p.proname = v_name;
		if v_count <> 1 then
			raise exception 'armory 0232 self-check: expected exactly one %, found %', v_name, v_count;
		end if;
		select count(*) into v_count from pg_proc p join pg_namespace n on n.oid = p.pronamespace
		where n.nspname = 'public' and p.proname = v_name and has_function_privilege('anon', p.oid, 'execute');
		if v_count <> 0 then
			raise exception 'armory 0232 self-check: anon can execute %', v_name;
		end if;
	end loop;
	foreach v_name in array v_helpers loop
		select count(*) into v_count from pg_proc p join pg_namespace n on n.oid = p.pronamespace
		where n.nspname = 'public' and p.proname = v_name and has_function_privilege('authenticated', p.oid, 'execute');
		if v_count <> 0 then
			raise exception 'armory 0232 self-check: authenticated can execute the helper %', v_name;
		end if;
	end loop;
	foreach v_name in array v_new || v_replaced loop
		select count(*) into v_count from pg_proc p join pg_namespace n on n.oid = p.pronamespace
		where n.nspname = 'public' and p.proname = v_name and has_function_privilege('authenticated', p.oid, 'execute');
		if v_count <> 1 then
			raise exception 'armory 0232 self-check: authenticated cannot execute %', v_name;
		end if;
	end loop;
	if exists (select 1 from information_schema.columns where table_schema = 'public' and table_name = 'armory_projects'
		and column_name = 'season' and is_nullable = 'NO') then
		raise exception 'armory 0232 self-check: armory_projects.season is still not null';
	end if;
	if not exists (select 1 from information_schema.columns where table_schema = 'public' and table_name = 'armory_projects'
		and column_name = 'archived_at') then
		raise exception 'armory 0232 self-check: armory_projects.archived_at is missing';
	end if;
	if has_sequence_privilege('anon', 'public.armory_change_feed_cursor_seq', 'usage')
		or has_sequence_privilege('authenticated', 'public.armory_change_feed_cursor_seq', 'update')
		or has_sequence_privilege('authenticated', 'public.armory_change_feed_cursor_seq', 'usage')
		or has_sequence_privilege('anon', 'public.armory_change_feed_cursor_seq', 'select') then
		raise exception 'armory 0232 self-check: a client role still holds the change feed sequence';
	end if;
	raise notice 'armory 0232: season nullable, archived_at present, 5 new RPCs and 3 helpers, 4 replaced in place, 0 executable by anon';
end
$$;

-- ---------------------------------------------------------------------------
-- 9. Not in 0232. Production parity with 0231's section 7 for this repo's 001 to 004.
-- 002 revoked its helpers from public and anon only, and 001's trigger function was never
-- revoked, so a hosted project's default privileges would leave them executable by every
-- signed-in client. 0231 closed that in production; this closes it here. The RLS policies
-- name armory_current_email and armory_is_member, so they are evaluated as the querying role
-- and authenticated keeps exactly those two.
-- ---------------------------------------------------------------------------

revoke all on function
	public.armory_refuse_version_mutation(),
	public.armory_add_change(uuid, text, uuid, jsonb),
	public.armory_replay(uuid, text),
	public.armory_remember(uuid, text, jsonb),
	public.armory_require_device(uuid),
	public.armory_valid_segment(text),
	public.armory_valid_folder(text),
	public.armory_derived_operation(uuid, text),
	public.armory_require_role(uuid, public.armory_member_role[], text),
	public.armory_name_taken(uuid, text, uuid),
	public.armory_check_release(uuid, smallint),
	public.armory_record_release(uuid, uuid, boolean, smallint)
from public, anon, authenticated;

revoke all on function
	public.armory_current_email(),
	public.armory_is_member(uuid)
from public, anon, authenticated;
grant execute on function
	public.armory_current_email(),
	public.armory_is_member(uuid)
to authenticated;

do $$
declare
	v_count integer;
begin
	select count(*) into v_count from pg_proc p join pg_namespace n on n.oid = p.pronamespace
	where n.nspname = 'public' and p.proname like 'armory\_%' and has_function_privilege('anon', p.oid, 'execute');
	if v_count <> 0 then
		raise exception 'armory 005 self-check: % armory function(s) are executable by anon', v_count;
	end if;
	select count(*) into v_count from pg_proc p join pg_namespace n on n.oid = p.pronamespace
	where n.nspname = 'public' and p.proname = any(array['armory_refuse_version_mutation', 'armory_add_change',
		'armory_replay', 'armory_remember', 'armory_require_device', 'armory_valid_segment', 'armory_valid_folder',
		'armory_derived_operation', 'armory_require_role', 'armory_name_taken', 'armory_check_release',
		'armory_record_release', 'armory_live_name_taken', 'armory_folder_files', 'armory_refuse_checked_out'])
		and has_function_privilege('authenticated', p.oid, 'execute');
	if v_count <> 0 then
		raise exception 'armory 005 self-check: % internal helper(s) are executable by authenticated', v_count;
	end if;
	if not has_function_privilege('authenticated', 'public.armory_current_email()', 'execute') then
		raise exception 'armory 005 self-check: authenticated cannot execute armory_current_email, which RLS names';
	end if;
end
$$;
