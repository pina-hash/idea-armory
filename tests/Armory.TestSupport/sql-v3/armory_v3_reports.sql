-- A TEST STAND-IN for idea-app migration 0233 part armory-reports (ARMORY.md v0.3, item 4): the
-- app's feedback and incidents, with the limits, refusals and DETAIL shapes that section names.
-- NOT the deployed SQL; applied only to a throwaway test database. Kept apart from the core
-- stand-in so the tests of a site without these RPCs (404 PGRST202) still have one.

create table if not exists public.armory_app_feedback (
 id uuid primary key default gen_random_uuid(), created_at timestamptz not null default now(), email text not null,
 device_name text, app_version text not null, kind text not null, body text not null, context jsonb not null default '{}',
 status text not null default 'new');
create table if not exists public.armory_app_incidents (
 id uuid primary key default gen_random_uuid(), created_at timestamptz not null default now(), email text not null,
 device_name text, app_version text not null, kind text not null, summary text not null, project_id uuid,
 report jsonb not null default '{}', feedback_id uuid references public.armory_app_feedback, status text not null default 'new');

create or replace function public.armory_report_refusal(p_reason text, p_field text, p_limit bigint default null, p_size bigint default null) returns void
language plpgsql set search_path = '' as $$
begin
	raise exception '% %', p_field, p_reason using errcode = '22023', detail = jsonb_strip_nulls(jsonb_build_object('reason', p_reason, 'field', p_field,
		'limit', p_limit, 'size', p_size))::text;
end $$;

create or replace function public.armory_report_rate(p_table text, p_limit int) returns void
language plpgsql set search_path = '' as $$
declare e text := public.armory_current_email(); n int; oldest timestamptz;
begin
	if p_table = 'feedback' then
		select count(*), min(created_at) into n, oldest from public.armory_app_feedback where email = e and created_at > now() - interval '1 hour';
	else
		select count(*), min(created_at) into n, oldest from public.armory_app_incidents where email = e and created_at > now() - interval '1 hour';
	end if;
	if n >= p_limit then
		raise exception 'too many reports' using errcode = 'PT429', detail = jsonb_build_object('reason', 'rate_limited', 'limit', p_limit,
			'window_seconds', 3600, 'retry_after_seconds', greatest(1, ceil(extract(epoch from (oldest + interval '1 hour' - now())))::int))::text;
	end if;
end $$;

create or replace function public.armory_submit_app_feedback(p_kind text, p_body text, p_app_version text, p_device_name text, p_context jsonb) returns uuid
language plpgsql security definer set search_path = '' as $$
declare
	e text := public.armory_current_email();
	k text := lower(btrim(coalesce(p_kind, '')));
	b text := btrim(coalesce(p_body, ''));
	v text := btrim(coalesce(p_app_version, ''));
	c jsonb := coalesce(p_context, '{}'::jsonb);
	id uuid;
begin
	if k not in ('bug', 'idea', 'other') then perform public.armory_report_refusal('kind', 'kind'); end if;
	if b = '' then perform public.armory_report_refusal('empty', 'body'); end if;
	if length(b) > 8000 then perform public.armory_report_refusal('too_long', 'body', 8000, length(b)); end if;
	if v = '' then perform public.armory_report_refusal('empty', 'app_version'); end if;
	if length(v) > 64 then perform public.armory_report_refusal('too_long', 'app_version', 64, length(v)); end if;
	if jsonb_typeof(c) <> 'object' then perform public.armory_report_refusal('not_object', 'context'); end if;
	if pg_column_size(c) > 131072 then perform public.armory_report_refusal('too_large', 'context', 131072, pg_column_size(c)); end if;
	perform public.armory_report_rate('feedback', 20);
	insert into public.armory_app_feedback(email, device_name, app_version, kind, body, context)
	values (e, left(btrim(p_device_name), 120), v, k, b, c) returning armory_app_feedback.id into id;
	return id;
end $$;

create or replace function public.armory_submit_app_incident(p_kind text, p_summary text, p_app_version text, p_device_name text, p_project uuid,
	p_report jsonb, p_feedback uuid) returns uuid
language plpgsql security definer set search_path = '' as $$
declare
	e text := public.armory_current_email();
	k text := btrim(coalesce(p_kind, ''));
	s text := btrim(coalesce(p_summary, ''));
	v text := btrim(coalesce(p_app_version, ''));
	r jsonb := coalesce(p_report, '{}'::jsonb);
	id uuid;
begin
	if k !~ '^[A-Za-z][A-Za-z0-9_-]{0,39}$' then perform public.armory_report_refusal('kind', 'kind'); end if;
	if s = '' then perform public.armory_report_refusal('empty', 'summary'); end if;
	if length(s) > 500 then perform public.armory_report_refusal('too_long', 'summary', 500, length(s)); end if;
	if v = '' then perform public.armory_report_refusal('empty', 'app_version'); end if;
	if length(v) > 64 then perform public.armory_report_refusal('too_long', 'app_version', 64, length(v)); end if;
	if jsonb_typeof(r) <> 'object' then perform public.armory_report_refusal('not_object', 'report'); end if;
	if pg_column_size(r) > 1048576 then perform public.armory_report_refusal('too_large', 'report', 1048576, pg_column_size(r)); end if;
	if p_feedback is not null and not exists(select 1 from public.armory_app_feedback where armory_app_feedback.id = p_feedback and email = e) then
		perform public.armory_report_refusal('feedback_not_found', 'feedback');
	end if;
	perform public.armory_report_rate('incidents', 30);
	delete from public.armory_app_incidents where created_at < now() - interval '90 days';
	insert into public.armory_app_incidents(email, device_name, app_version, kind, summary, project_id, report, feedback_id)
	values (e, left(btrim(p_device_name), 120), v, k, s, (select p.id from public.armory_projects p where p.id = p_project), r, p_feedback)
	returning armory_app_incidents.id into id;
	return id;
end $$;

grant execute on function public.armory_submit_app_feedback(text, text, text, text, jsonb),
	public.armory_submit_app_incident(text, text, text, text, uuid, jsonb, uuid) to authenticated;
revoke all on function public.armory_submit_app_feedback(text, text, text, text, jsonb),
	public.armory_submit_app_incident(text, text, text, text, uuid, jsonb, uuid) from public, anon;
revoke all on function public.armory_report_refusal(text, text, bigint, bigint), public.armory_report_rate(text, int) from public, anon, authenticated;
