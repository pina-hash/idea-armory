-- Test-only replacement for idea-app migration 0067_admin_tier.sql's is_admin().
-- Like the current_user_email() stub, it reads a caller-set setting that exists only in
-- the test harness: the comma-separated emails in armory.test_admins are site admins.
create or replace function public.is_admin()
returns boolean
language sql
stable
security definer
set search_path = ''
as $$
 select coalesce(public.current_user_email() = any(string_to_array(current_setting('armory.test_admins', true), ',')), false);
$$;
