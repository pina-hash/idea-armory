# IDEA app conventions checked for Armory

The read-only source was `/opt/idea-app`. `CLAUDE.md` makes applied migrations immutable and requires explicit grants, catalog self-checks, and migration history. The newest five migrations by modification time were `0224_maps_wall_thickness.sql`, `0223_classroom_teams.sql`, `0221_foundry_boards_and_author_profile.sql`, `0220_profile_identity_style.sql`, and `0218_classroom_create_item_unit.sql`.

Identity is lowercased Google-session email. Migration `0067_admin_tier.sql` defines `current_user_email()` as the email in `auth.users` for `auth.uid()` and defines `is_admin()`. Armory's `armory_current_email()` calls that helper in production. Tests set the transaction-local `armory.test_email` setting, which is deliberately the only seam.

Security-definer RPC precedent was checked in `0218_classroom_create_item_unit.sql`: functions use `security definer`, `set search_path = ''`, schema-qualified objects, and explicit role grants. Migration `0166_short_link_reserve_maps.sql` documents why `revoke ... from public` does not remove Supabase's direct `anon` grant. Armory therefore names `public`, `anon`, and, where applicable, `authenticated` explicitly.
