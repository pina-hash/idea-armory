-- A TEST STAND-IN for the parts of idea-app migration 0233 (part armory-core) that the Windows
-- app calls, written from ARMORY.md "The v0.3 server contract (migration 0233)" so the agent's
-- tests can run against a database that answers the way that section says. It is NOT the
-- deployed SQL and is never applied anywhere but a throwaway test database: idea-app's 0233 is
-- the authority. Only what the app depends on is modelled (answers, SQLSTATEs, DETAIL reasons,
-- a site admin reading every project); the project-row-first locking, admin widening of people,
-- archive and Force check in, and the purge refusals are not, and the purges themselves are test
-- helpers below, not armory_purge_*.

alter table public.armory_devices
 add column if not exists last_seen timestamptz,
 add column if not exists app_version text,
 add column if not exists state text,
 add column if not exists heartbeat_writes integer not null default 0;

-- The receipt a project purge leaves (no member address is kept).
create table if not exists public.armory_purged_projects (
 id uuid primary key, name text not null, purged_at timestamptz not null default now(), purged_by text not null,
 counts jsonb not null default '{}');

-- Item 2: each row gains can_take_back (mentor, CAD lead, or site admin). Membership only.
create or replace function public.armory_my_projects() returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare e text := public.armory_current_email();
begin
	return coalesce((
		select jsonb_agg(jsonb_build_object('id', p.id, 'name', p.name, 'season', p.season, 'role', m.role,
			'pinned_release', p.pinned_release, 'release_gate', p.release_gate,
			'archived', p.archived_at is not null, 'archived_at', p.archived_at,
			'can_take_back', m.role in ('mentor', 'cad_lead') or coalesce(public.is_admin(), false)) order by lower(p.name), p.id)
		from public.armory_projects p join public.armory_members m on m.project_id = p.id and m.email = e
	), '[]'::jsonb);
end $$;

-- "The website's reads": armory_can_view (a member, or a site admin) gates the member reads,
-- each keeping its own refusal text and SQLSTATE. Here the four read RPCs are re-made from their
-- own bodies with the membership check widened to it, so a site admin reads every project.
create or replace function public.armory_can_view(p_project uuid) returns boolean
language sql stable security definer set search_path = '' as $$
	select public.armory_is_member(p_project) or coalesce(public.is_admin(), false)
$$;
do $$
declare f text; body text;
begin
	foreach f in array array['armory_list_changes', 'armory_project_files', 'armory_file_history', 'armory_project_checkouts'] loop
		select pg_get_functiondef(p.oid) into body from pg_proc p join pg_namespace n on n.oid = p.pronamespace where n.nspname = 'public' and p.proname = f;
		execute replace(body, 'public.armory_is_member(', 'public.armory_can_view(');
	end loop;
end $$;

-- The caller's projects, and every project for a site admin (role null where not a member).
-- The website's list; the app never calls it (it syncs armory_my_projects, membership only).
create or replace function public.armory_project_summaries(p_project uuid default null) returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare e text := public.armory_current_email(); admin boolean := coalesce(public.is_admin(), false);
begin
	return coalesce((
		select jsonb_agg(jsonb_build_object('id', p.id, 'name', p.name, 'season', p.season, 'role', m.role, 'archived', p.archived_at is not null)
			order by lower(p.name), p.id)
		from public.armory_projects p left join public.armory_members m on m.project_id = p.id and m.email = e
		where (p_project is null or p.id = p_project) and (m.email is not null or admin)
	), '[]'::jsonb);
end $$;

-- Item 3: when the project was deleted forever, or null. Any signed-in user.
create or replace function public.armory_project_purged(p_project uuid) returns timestamptz
language sql stable security definer set search_path = '' as $$
	select purged_at from public.armory_purged_projects where id = p_project
$$;

-- Item 5: the caller's own computer only; null or empty keeps the stored value; a call within
-- 20 seconds that changes nothing writes nothing.
create or replace function public.armory_heartbeat(p_device uuid, p_app_version text, p_state text) returns void
language plpgsql security definer set search_path = '' as $$
declare
	e text := public.armory_current_email();
	v text := nullif(btrim(coalesce(p_app_version, '')), '');
	s text := nullif(btrim(coalesce(p_state, '')), '');
	d public.armory_devices;
begin
	select * into d from public.armory_devices where id = p_device and owner_email = e for update;
	if not found then raise exception 'device is not registered to caller'; end if;
	if v is not null and length(v) > 40 then
		raise exception 'app version is too long' using errcode = '22023',
			detail = jsonb_build_object('reason', 'too_long', 'field', 'app_version', 'limit', 40, 'size', length(v))::text;
	end if;
	if s is not null and s !~ '^[A-Za-z][A-Za-z0-9_-]{0,39}$' then
		raise exception 'state must be one word' using errcode = '22023', detail = jsonb_build_object('reason', 'state', 'field', 'state')::text;
	end if;
	if d.last_seen is not null and d.last_seen > now() - interval '20 seconds'
		and (v is null or v = d.app_version) and (s is null or s = d.state) then return; end if;
	update public.armory_devices set last_seen = now(), app_version = coalesce(v, app_version), state = coalesce(s, state),
		heartbeat_writes = heartbeat_writes + 1 where id = p_device;
end $$;

-- Item 6: armory_acquire_lock / armory_release_lock per file, in id order, each file answered on
-- its own. 1 to 500 distinct files, else 22023 {reason: count, total, limit: 500}. A replayed
-- operation answers the first time.
create or replace function public.armory_batch_files(p_files uuid[]) returns uuid[]
language plpgsql immutable set search_path = '' as $$
declare ids uuid[]; total int := coalesce(array_length(p_files, 1), 0);
begin
	select array_agg(distinct x order by x) into ids from unnest(p_files) x where x is not null;
	if ids is null or array_length(ids, 1) <> total or total > 500 then
		raise exception 'a batch takes 1 to 500 distinct files' using errcode = '22023',
			detail = jsonb_build_object('reason', 'count', 'total', total, 'limit', 500)::text;
	end if;
	return ids;
end $$;

create or replace function public.armory_lock_files(p_files uuid[], p_device uuid, p_operation uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare r jsonb; ids uuid[]; f uuid; results jsonb := '[]'; ok int := 0; bad int := 0; got boolean;
begin
	r := public.armory_replay(p_operation, 'armory_lock_files'); if r is not null then return r; end if;
	ids := public.armory_batch_files(p_files);
	foreach f in array ids loop
		begin
			got := public.armory_acquire_lock(f, p_device, md5(p_operation::text || ':' || f::text)::uuid);
			results := results || jsonb_build_array(jsonb_build_object('file_id', f, 'ok', true, 'acquired', got));
			ok := ok + 1;
		exception when others then
			results := results || jsonb_build_array(jsonb_build_object('file_id', f, 'ok', false, 'code', sqlstate, 'message', sqlerrm));
			bad := bad + 1;
		end;
	end loop;
	r := jsonb_build_object('total', array_length(ids, 1), 'succeeded', ok, 'refused', bad, 'results', results);
	perform public.armory_remember(p_operation, 'armory_lock_files', r);
	return r;
end $$;

create or replace function public.armory_release_locks(p_files uuid[], p_device uuid, p_operation uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare r jsonb; ids uuid[]; f uuid; results jsonb := '[]'; ok int := 0; bad int := 0; got boolean;
begin
	r := public.armory_replay(p_operation, 'armory_release_locks'); if r is not null then return r; end if;
	ids := public.armory_batch_files(p_files);
	foreach f in array ids loop
		begin
			got := public.armory_release_lock(f, p_device, md5(p_operation::text || ':' || f::text)::uuid);
			results := results || jsonb_build_array(jsonb_build_object('file_id', f, 'ok', true, 'released', got));
			ok := ok + 1;
		exception when others then
			results := results || jsonb_build_array(jsonb_build_object('file_id', f, 'ok', false, 'code', sqlstate, 'message', sqlerrm));
			bad := bad + 1;
		end;
	end loop;
	r := jsonb_build_object('total', array_length(ids, 1), 'succeeded', ok, 'refused', bad, 'results', results);
	perform public.armory_remember(p_operation, 'armory_release_locks', r);
	return r;
end $$;

grant execute on function public.armory_project_summaries(uuid) to authenticated;
revoke all on function public.armory_project_summaries(uuid) from public, anon;
revoke all on function public.armory_can_view(uuid) from public, anon;
grant execute on function public.armory_project_purged(uuid), public.armory_heartbeat(uuid, text, text),
	public.armory_lock_files(uuid[], uuid, uuid), public.armory_release_locks(uuid[], uuid, uuid) to authenticated;
revoke all on function public.armory_project_purged(uuid), public.armory_heartbeat(uuid, text, text),
	public.armory_lock_files(uuid[], uuid, uuid), public.armory_release_locks(uuid[], uuid, uuid) from public, anon;

-- TEST HELPERS (superuser connections only; never granted). What armory_purge_folder and
-- armory_purge_project leave behind once they succeed, without their refusals: the rows go
-- (the immutability triggers are bypassed for this session), folder_purged is written for a
-- folder, and a project purge leaves only its receipt.
create or replace function public.armory_test_purge_folder(p_project uuid, p_folder text, p_by text) returns jsonb
language plpgsql set search_path = '' as $$
declare ids uuid[];
begin
	select coalesce(array_agg(id order by id), '{}') into ids from public.armory_files
	where project_id = p_project and deleted_at is not null and (p_folder = '' or folder = p_folder or folder like p_folder || '/%');
	if array_length(ids, 1) is null then return jsonb_build_object('folder', p_folder, 'files', 0); end if;
	perform set_config('session_replication_role', 'replica', true);
	delete from public.armory_version_releases where file_id = any(ids);
	delete from public.armory_locks where file_id = any(ids);
	delete from public.armory_tombstones where file_id = any(ids);
	delete from public.armory_side_versions where file_id = any(ids);
	update public.armory_files set current_version_id = null where id = any(ids);
	delete from public.armory_versions where file_id = any(ids);
	delete from public.armory_files where id = any(ids);
	perform set_config('session_replication_role', 'origin', true);
	perform public.armory_add_change(p_project, 'folder_purged', p_project,
		jsonb_build_object('folder', p_folder, 'files', array_length(ids, 1), 'file_ids', to_jsonb(ids), 'by', p_by));
	return jsonb_build_object('folder', p_folder, 'files', array_length(ids, 1), 'file_ids', to_jsonb(ids));
end $$;

create or replace function public.armory_test_purge_project(p_project uuid, p_by text) returns timestamptz
language plpgsql set search_path = '' as $$
declare n text; ids uuid[]; at timestamptz := now();
begin
	select name into n from public.armory_projects where id = p_project;
	if not found then raise exception 'project not found' using errcode = 'P0002'; end if;
	select coalesce(array_agg(id), '{}') into ids from public.armory_files where project_id = p_project;
	perform set_config('session_replication_role', 'replica', true);
	delete from public.armory_version_releases where file_id = any(ids);
	delete from public.armory_locks where file_id = any(ids);
	delete from public.armory_tombstones where file_id = any(ids);
	delete from public.armory_side_versions where file_id = any(ids);
	update public.armory_files set current_version_id = null where id = any(ids);
	delete from public.armory_versions where file_id = any(ids);
	delete from public.armory_files where id = any(ids);
	delete from public.armory_part_number_allocations where project_id = p_project;
	delete from public.armory_change_feed where project_id = p_project;
	delete from public.armory_members where project_id = p_project;
	delete from public.armory_projects where id = p_project;
	perform set_config('session_replication_role', 'origin', true);
	insert into public.armory_purged_projects(id, name, purged_at, purged_by, counts) values (p_project, n, at, p_by, jsonb_build_object('files', array_length(ids, 1)));
	return at;
end $$;
revoke all on function public.armory_test_purge_folder(uuid, text, text), public.armory_test_purge_project(uuid, text) from public, anon, authenticated;
