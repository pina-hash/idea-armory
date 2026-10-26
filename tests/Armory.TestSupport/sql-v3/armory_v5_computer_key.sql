-- idea-app 0310 (ledger 0441), the parts the app calls, over the core stand-in (applied first):
-- armory_devices.computer_key and the four-argument armory_heartbeat. 0310's own rules: a key of
-- 1 to 64 letters, digits, dashes or underscores is kept, any other is ignored (never refused),
-- null keeps what is stored, and a changed key is written even inside the 20-second window. The
-- three-argument stand-in from armory_v3_core.sql stays as it is, so the pair resolves as on the
-- site (neither form has a default).
alter table public.armory_devices add column if not exists computer_key text
	check (computer_key is null or computer_key ~ '^[0-9A-Za-z_-]{1,64}$');

create or replace function public.armory_heartbeat(p_device uuid, p_app_version text, p_state text, p_computer_key text) returns void
language plpgsql security definer set search_path = '' as $$
declare
	k text := case when p_computer_key ~ '^[0-9A-Za-z_-]{1,64}$' then p_computer_key end;
begin
	perform public.armory_heartbeat(p_device, p_app_version, p_state);
	if k is not null then
		update public.armory_devices set computer_key = k where id = p_device and computer_key is distinct from k;
	end if;
end $$;

grant execute on function public.armory_heartbeat(uuid, text, text, text) to authenticated;
revoke all on function public.armory_heartbeat(uuid, text, text, text) from public, anon;
