-- Test-only replacement for idea-app migration 0067_admin_tier.sql.
create or replace function public.current_user_email()
returns text
language sql
stable
security definer
set search_path = ''
as $$
 select nullif(lower(btrim(current_setting('armory.test_email', true))), '');
$$;
