-- A TEST STAND-IN for idea-app migration 0234 (armory_break_locks, ARMORY.md "The v0.3.1 server
-- contract (migration 0234)"), applied only to a throwaway test database. The function body is
-- 0234's, copied as it is: the contract is that SQL. Each file goes through this database's own
-- armory_break_lock (server/sql/002), so every per-file rule (who may, the lock_broken change, the
-- refusal text and SQLSTATE) is that function's, as in 0234. What differs from the deployed
-- database is armory_break_lock itself: here it is 0231's (a named device is required, a site
-- admin is not widened), which is what the rest of the stand-in models too.

-- 0231's helper (not in server/sql): the operation id a batch hands each file.
create or replace function public.armory_derived_operation(p_operation uuid, p_purpose text) returns uuid
language sql immutable set search_path = '' as $$
	select md5(p_operation::text || ':' || p_purpose)::uuid $$;

create or replace function public.armory_break_locks(p_files uuid[], p_device uuid, p_operation uuid) returns jsonb
language plpgsql security definer set search_path = '' as $ac$
declare r jsonb; ids uuid[]; f uuid; got boolean; results jsonb := '[]'::jsonb; n_ok int := 0; n_bad int := 0;
begin
	r := public.armory_replay(p_operation, 'armory_break_locks');
	if r is not null then return r; end if;
	if p_device is not null then perform public.armory_require_device(p_device); end if;
	select array_agg(distinct x order by x) into ids from unnest(coalesce(p_files, '{}'::uuid[])) x where x is not null;
	if ids is null or cardinality(ids) > 500 then
		raise exception 'A batch is 1 to 500 files.' using errcode = '22023',
			detail = jsonb_build_object('reason', 'count', 'total', coalesce(cardinality(ids), 0), 'limit', 500)::text;
	end if;
	foreach f in array ids loop
		begin
			got := public.armory_break_lock(f, p_device, public.armory_derived_operation(p_operation, 'break:' || f::text));
			results := results || jsonb_build_array(jsonb_build_object('file_id', f, 'ok', true, 'broken', got));
			n_ok := n_ok + 1;
		exception when others then
			results := results || jsonb_build_array(jsonb_build_object('file_id', f, 'ok', false, 'code', sqlstate, 'message', sqlerrm));
			n_bad := n_bad + 1;
		end;
	end loop;
	return public.armory_remember(p_operation, 'armory_break_locks',
		jsonb_build_object('total', cardinality(ids), 'succeeded', n_ok, 'refused', n_bad, 'results', results));
end $ac$;

revoke all on function public.armory_break_locks(uuid[], uuid, uuid) from public, anon, authenticated;
grant execute on function public.armory_break_locks(uuid[], uuid, uuid) to authenticated;
revoke all on function public.armory_derived_operation(uuid, text) from public, anon, authenticated;
