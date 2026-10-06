-- Test-only stand-in for idea-app's public.profiles (0001_profiles.sql plus 0020's display_name),
-- the columns 0232's armory_project_checkouts reads for holder_name. In idea-app the id is the
-- auth.users id; this harness has no auth schema, so the id is a plain uuid. No client role
-- reads it, as in idea-app, where only definer functions name it.
create table public.profiles (
 id uuid primary key default gen_random_uuid(), email text, full_name text, display_name text);
revoke all on public.profiles from public, anon, authenticated;
