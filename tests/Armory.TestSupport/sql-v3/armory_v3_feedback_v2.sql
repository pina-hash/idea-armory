-- A TEST STAND-IN for idea-app migration 0235 (ARMORY.md "The v0.3.2 server contract (migration
-- 0235)", item 5), applied only to a throwaway test database AFTER armory_v3_reports.sql. The three
-- functions' bodies are 0235's, copied as they are: the contract is that SQL. Around them, the
-- parts of Supabase this harness otherwise lacks, kept to what 0235 touches: auth.uid() (Supabase's
-- own definition, reading the request's JWT claims, which FakeSupabase sets for every call), and a
-- storage schema with buckets, objects and storage.foldername, so 0235's bucket and its two
-- policies are created exactly as 0235 writes them. FakeSupabase's Storage endpoint inserts each
-- upload into storage.objects as the caller, so the insert policy (own folder) is PostgreSQL's
-- own check, as on Supabase.

-- ---------------------------------------------------------------------------
-- Supabase's auth.uid() and the storage tables 0235 uses (not in this harness otherwise).
-- ---------------------------------------------------------------------------

create schema if not exists auth;
create or replace function auth.uid() returns uuid language sql stable as $$
	select coalesce(nullif(current_setting('request.jwt.claim.sub', true), ''),
		(nullif(current_setting('request.jwt.claims', true), '')::jsonb ->> 'sub'))::uuid
$$;
grant usage on schema auth to anon, authenticated;
grant execute on function auth.uid() to anon, authenticated;

create schema if not exists storage;
create table if not exists storage.buckets (
 id text primary key, name text not null, public boolean not null default false, file_size_limit bigint,
 allowed_mime_types text[], created_at timestamptz not null default now());
create table if not exists storage.objects (
 id uuid primary key default gen_random_uuid(), bucket_id text references storage.buckets(id), name text not null,
 owner uuid, created_at timestamptz not null default now(), metadata jsonb, unique (bucket_id, name));
create or replace function storage.foldername(name text) returns text[] language plpgsql immutable as $$
declare parts text[];
begin
	parts := string_to_array(name, '/');
	return parts[1:array_length(parts, 1) - 1];
end $$;
alter table storage.objects enable row level security;
grant usage on schema storage to anon, authenticated;
grant select, insert on storage.objects to authenticated;
grant execute on function storage.foldername(text) to anon, authenticated;

-- The note's review columns 0233 has (the reports stand-in kept only status).
alter table public.armory_app_feedback add column if not exists reviewed_at timestamptz;
alter table public.armory_app_feedback add column if not exists reviewed_by text;

-- ---------------------------------------------------------------------------
-- 0235, section 0: Your feedback (copied).
-- ---------------------------------------------------------------------------

create or replace function public.armory_my_app_feedback(p_limit integer) returns jsonb
language plpgsql stable security definer set search_path = '' as $af$
declare
	e text := public.armory_current_email();
	v_limit integer := least(greatest(coalesce(p_limit, 50), 1), 200);
begin
	if e = '' then
		raise exception 'Sign in to see your feedback.' using errcode = '42501';
	end if;
	return coalesce((
		select jsonb_agg(jsonb_build_object(
			'id', r.id, 'created_at', r.created_at, 'kind', r.kind, 'body', r.body, 'tried', r.tried, 'area', r.area,
			'has_screenshot', r.screenshot_path is not null, 'app_version', r.app_version, 'device_name', r.device_name,
			'status', case when r.status = 'spam' then 'closed' else r.status end, 'reviewed_at', r.reviewed_at)
			order by r.created_at desc, r.id)
		from (
			select f.* from public.armory_app_feedback f where f.email = e order by f.created_at desc, f.id limit v_limit
		) r
	), '[]'::jsonb);
end $af$;

-- ---------------------------------------------------------------------------
-- 0235, section 1: the note's new fields (copied; the stand-in table has no kind check to widen).
-- ---------------------------------------------------------------------------

alter table public.armory_app_feedback add column if not exists tried text
	check (tried is null or char_length(tried) between 1 and 1000);
alter table public.armory_app_feedback add column if not exists area text
	check (area is null or char_length(area) between 1 and 120);
alter table public.armory_app_feedback add column if not exists screenshot_path text
	check (screenshot_path is null
		or screenshot_path ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.png$');

-- ---------------------------------------------------------------------------
-- 0235, section 2: the submit, wide, with no defaults (copied).
-- ---------------------------------------------------------------------------

create or replace function public.armory_submit_app_feedback(
	p_kind text, p_body text, p_app_version text, p_device_name text, p_context jsonb,
	p_tried text, p_area text, p_screenshot text
) returns uuid
language plpgsql security definer set search_path = '' as $af$
declare
	e text := public.armory_current_email();
	v_kind text := lower(regexp_replace(coalesce(p_kind, ''), '^\s+|\s+$', '', 'g'));
	v_body text := regexp_replace(coalesce(p_body, ''), '^\s+|\s+$', '', 'g');
	v_version text := regexp_replace(coalesce(p_app_version, ''), '^\s+|\s+$', '', 'g');
	v_device text := nullif(left(regexp_replace(coalesce(p_device_name, ''), '^\s+|\s+$', '', 'g'), 120), '');
	v_context jsonb := coalesce(p_context, '{}'::jsonb);
	v_tried text := nullif(regexp_replace(coalesce(p_tried, ''), '^\s+|\s+$', '', 'g'), '');
	v_area text := nullif(regexp_replace(coalesce(p_area, ''), '^\s+|\s+$', '', 'g'), '');
	v_shot text := nullif(regexp_replace(coalesce(p_screenshot, ''), '^\s+|\s+$', '', 'g'), '');
	v_size integer;
	v_recent integer;
	v_oldest timestamptz;
	v_id uuid;
begin
	-- The 0233 checks, in 0233's order and words, then the three new fields.
	if v_kind not in ('bug', 'idea', 'praise', 'other') then
		raise exception 'The kind of note is bug, idea, praise or other.' using errcode = '22023',
			detail = jsonb_build_object('reason', 'kind', 'field', 'kind')::text;
	end if;
	if v_body = '' then
		raise exception 'The note has nothing in it.' using errcode = '22023',
			detail = jsonb_build_object('reason', 'empty', 'field', 'body')::text;
	end if;
	if char_length(v_body) > 8000 then
		raise exception 'The note is % characters; the limit is 8000.', char_length(v_body) using errcode = '22023',
			detail = jsonb_build_object('reason', 'too_long', 'field', 'body', 'limit', 8000, 'size', char_length(v_body))::text;
	end if;
	if v_version = '' or char_length(v_version) > 64 then
		raise exception 'The app version is required, at most 64 characters.' using errcode = '22023',
			detail = jsonb_build_object('reason', case when v_version = '' then 'empty' else 'too_long' end, 'field', 'app_version')::text;
	end if;
	if jsonb_typeof(v_context) is distinct from 'object' then
		raise exception 'The context must be a JSON object.' using errcode = '22023',
			detail = jsonb_build_object('reason', 'not_object', 'field', 'context')::text;
	end if;
	v_size := pg_column_size(v_context);
	if v_size > 131072 then
		raise exception 'The context is % bytes; the limit is 131072.', v_size using errcode = '22023',
			detail = jsonb_build_object('reason', 'too_large', 'field', 'context', 'limit', 131072, 'size', v_size)::text;
	end if;
	if char_length(coalesce(v_tried, '')) > 1000 then
		raise exception 'What you tried is % characters; the limit is 1000.', char_length(v_tried) using errcode = '22023',
			detail = jsonb_build_object('reason', 'too_long', 'field', 'tried', 'limit', 1000, 'size', char_length(v_tried))::text;
	end if;
	if char_length(coalesce(v_area, '')) > 120 then
		raise exception 'The area is % characters; the limit is 120.', char_length(v_area) using errcode = '22023',
			detail = jsonb_build_object('reason', 'too_long', 'field', 'area', 'limit', 120, 'size', char_length(v_area))::text;
	end if;
	if v_shot is not null then
		-- The key is <the caller's auth uid>/<uuid>.png, lowercase, nothing else.
		if v_shot !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.png$'
			or auth.uid() is null or split_part(v_shot, '/', 1) <> auth.uid()::text then
			raise exception 'The screenshot key must be your own folder, then a uuid, then .png.' using errcode = '22023',
				detail = jsonb_build_object('reason', 'bad_path', 'field', 'screenshot')::text;
		end if;
		if not exists (select 1 from storage.objects o where o.bucket_id = 'armory-feedback-shots' and o.name = v_shot) then
			raise exception 'The screenshot was not uploaded. Upload it first, then send the note.' using errcode = '22023',
				detail = jsonb_build_object('reason', 'not_found', 'field', 'screenshot')::text;
		end if;
		if exists (select 1 from public.armory_app_feedback f where f.screenshot_path = v_shot) then
			raise exception 'That screenshot is already on another note.' using errcode = '22023',
				detail = jsonb_build_object('reason', 'in_use', 'field', 'screenshot')::text;
		end if;
	end if;

	-- The capacity check holds a lock on the account, so two notes at once cannot both pass.
	perform pg_advisory_xact_lock(hashtextextended('armory_app_feedback:' || e, 0));
	select count(*), min(created_at) into v_recent, v_oldest from public.armory_app_feedback
	where email = e and created_at > now() - interval '1 hour';
	if v_recent >= 20 then
		raise exception 'Too many feedback notes from this account in the last hour.' using errcode = 'PT429',
			detail = jsonb_build_object('reason', 'rate_limited', 'limit', 20, 'window_seconds', 3600,
				'retry_after_seconds', greatest(1, ceil(extract(epoch from (v_oldest + interval '1 hour' - now())))::int))::text;
	end if;

	insert into public.armory_app_feedback (email, device_name, app_version, kind, body, context, tried, area, screenshot_path)
	values (e, v_device, v_version, v_kind, v_body, v_context, v_tried, v_area, v_shot)
	returning id into v_id;
	return v_id;
end $af$;

-- The five-argument form a 0.3.x app calls: 0233's refusal for a kind it never
-- took, then the wide form with nothing new. Same signature, same OID. (Copied.)
create or replace function public.armory_submit_app_feedback(
	p_kind text, p_body text, p_app_version text, p_device_name text, p_context jsonb
) returns uuid
language plpgsql security definer set search_path = '' as $af$
begin
	if lower(regexp_replace(coalesce(p_kind, ''), '^\s+|\s+$', '', 'g')) not in ('bug', 'idea', 'other') then
		raise exception 'The kind of note is bug, idea or other.' using errcode = '22023',
			detail = jsonb_build_object('reason', 'kind', 'field', 'kind')::text;
	end if;
	return public.armory_submit_app_feedback(p_kind, p_body, p_app_version, p_device_name, p_context, null::text, null::text, null::text);
end $af$;

-- ---------------------------------------------------------------------------
-- 0235, section 5: grants (copied, without the admin list this stand-in does not have).
-- ---------------------------------------------------------------------------

revoke all on function
	public.armory_submit_app_feedback(text, text, text, text, jsonb, text, text, text),
	public.armory_submit_app_feedback(text, text, text, text, jsonb),
	public.armory_my_app_feedback(integer)
from public, anon, authenticated;
grant execute on function
	public.armory_submit_app_feedback(text, text, text, text, jsonb, text, text, text),
	public.armory_submit_app_feedback(text, text, text, text, jsonb),
	public.armory_my_app_feedback(integer)
to authenticated;

-- ---------------------------------------------------------------------------
-- 0235, section 6: the storage half (copied).
-- ---------------------------------------------------------------------------

do $af$
begin
	begin
		insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
		values ('armory-feedback-shots', 'armory-feedback-shots', false, 2097152, array['image/png'])
		on conflict (id) do update
			set public = false, file_size_limit = 2097152, allowed_mime_types = array['image/png'];

		drop policy if exists "armory feedback shots insert own folder" on storage.objects;
		create policy "armory feedback shots insert own folder"
			on storage.objects
			for insert
			to authenticated
			with check (
				bucket_id = 'armory-feedback-shots'
				and (storage.foldername(name))[1] = auth.uid()::text
			);

		drop policy if exists "armory feedback shots admin read" on storage.objects;
		create policy "armory feedback shots admin read"
			on storage.objects
			for select
			to authenticated
			using (
				bucket_id = 'armory-feedback-shots'
				and public.is_admin()
			);
	exception when insufficient_privilege then
		raise notice '0235: the storage half was NOT applied (this role cannot write storage.buckets or storage.objects policies). Re-paste 0235 in the SQL editor to finish it; screenshots are refused as not_found until then.';
	end;
end;
$af$;
