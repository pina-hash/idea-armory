-- Draft contract only. idea-app numbers it at landing: supabase/migrations/0288_armory_newer_release_uploads.sql
-- (ledger 0423). Everything below this header is that migration copied byte for byte, so the
-- tests here exercise the exact SQL production runs.

-- 0288_armory_newer_release_uploads.sql
--
-- IDEA ARMORY 0.4.0: A FILE SAVED IN A NEWER SOLIDWORKS THAN ITS PROJECT USES
-- UPLOADS, IS WARNED ABOUT, AND IS NEVER REFUSED (ledger 0423, Mr. Pina
-- 2026-10-21: "when a file was saved in a newer SolidWorks version than its
-- project uses ... warn once and offer 'this is on purpose', which silences it
-- for that file. Never block.").
--
-- armory_check_release(uuid, smallint) is the server's own copy of the
-- Windows app's release gate. Until now it raised 22023 ("SolidWorks 2026
-- cannot upload to a vault pinned to 2025") for a release known to be newer
-- than the project's pinned_release. That refusal is removed: a newer release
-- is accepted and returns true, so armory_commit_version_with_release and
-- armory_save_side_version_with_release record it in armory_version_releases
-- (saved_release, release_checked) exactly as they record any other release,
-- and every computer of the project reads it there and warns. The other
-- refusal is kept word for word: an UNKNOWN release in an 'enforce' project.
--
-- A WIDENING WITH NO ORDERING (CLAUDE.md, DEPLOY ORDERING). Same name, same
-- signature, same return. No stored row is read by this gate, so nothing
-- already stored is answered differently. The deployed Windows apps (0.3.x)
-- refuse a newer release themselves before they call, so they send nothing
-- new; 0.4.0 sends it, and needs this file applied first or its newer files
-- are refused exactly as before (they wait on that computer, nothing lost).
--
-- UNDO, a person's paste: re-run the 0231 body of armory_check_release
-- (supabase/migrations/0231_armory.sql, "create or replace function
-- public.armory_check_release"). Versions recorded as newer while this was
-- applied stay recorded; that is history, and the app only warns about them.

create or replace function public.armory_check_release(p_file uuid, p_saved_release smallint) returns boolean
language plpgsql stable security definer set search_path = '' as $$
declare f public.armory_files; p public.armory_projects;
begin
	select * into f from public.armory_files where id = p_file;
	if not found then raise exception 'file not found' using errcode = 'P0002'; end if;
	if f.name !~* '\.(sldprt|sldasm|slddrw)$' then return false; end if;
	select * into p from public.armory_projects where id = f.project_id;
	-- A release newer than the pin is recorded, never refused (0288): every computer warns.
	if p_saved_release is null and p.release_gate = 'enforce' then
		raise exception 'The saved SolidWorks release is unknown; keep the local draft until it can be read.' using errcode = '22023';
	end if;
	return true;
end $$;

-- Internal: only the definer functions above call it (as in 0231).
revoke all on function public.armory_check_release(uuid, smallint) from public, anon, authenticated;

do $check$
declare v_n int; v_body text;
begin
	select count(*), max(p.prosrc) into v_n, v_body from pg_proc p join pg_namespace n on n.oid = p.pronamespace
	where n.nspname = 'public' and p.proname = 'armory_check_release';
	if v_n <> 1 then
		raise exception '0288: expected exactly one public.armory_check_release, found %', v_n;
	end if;
	if position('cannot upload to a vault pinned' in v_body) > 0 then
		raise exception '0288: armory_check_release still refuses a newer release';
	end if;
	if position('release is unknown' in v_body) = 0 then
		raise exception '0288: armory_check_release lost the enforce refusal of an unknown release';
	end if;
	if has_function_privilege('anon', 'public.armory_check_release(uuid, smallint)', 'execute')
		or has_function_privilege('authenticated', 'public.armory_check_release(uuid, smallint)', 'execute') then
		raise exception '0288: armory_check_release is callable by a client role';
	end if;
	raise notice '0288: armory_check_release records a newer release instead of refusing it';
end $check$;
