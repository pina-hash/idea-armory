-- 0.5.9: empty folders have identity in metadata, without a pretend student file.
-- Additive RPCs. Older agents keep their file-derived folder behavior.
-- Production: idea-app supabase/migrations/0410_armory_shared_empty_folders.sql.
-- Undo: drop the two additive public RPCs and disable the metadata trigger. Keep the
-- metadata table until clients stop using it; file versions and bytes are untouched.
-- The existing migration tool owns the transaction and its history row.
create table if not exists public.armory_shared_folders (
    project_id uuid not null references public.armory_projects(id) on delete cascade,
    folder text not null check (folder <> '' and public.armory_valid_folder(folder)),
    folder_key text generated always as (lower(folder)) stored,
    primary key (project_id, folder_key)
);
alter table public.armory_shared_folders enable row level security;
revoke all on public.armory_shared_folders from public, anon, authenticated, service_role;
grant select on public.armory_shared_folders to authenticated;
drop policy if exists armory_shared_folders_read on public.armory_shared_folders;
create policy armory_shared_folders_read on public.armory_shared_folders for select to authenticated
    using (public.armory_is_member(project_id));

-- Old agents and the website still use the original folder RPCs. Follow their change
-- events in the same transaction, including permanent removal, so metadata never brings
-- a removed folder back or leaves an empty copy under its former name.
create or replace function public.armory_update_folder_paths() returns trigger
language plpgsql security definer set search_path = '' as $$
declare source text := new.payload->>'from'; target text := new.payload->>'to'; removed_folder text := new.payload->>'folder';
begin
    if new.kind = 'folder_renamed' and source <> '' and target <> '' then
        update public.armory_shared_folders set folder = target || substr(armory_shared_folders.folder, length(source) + 1)
            where project_id = new.project_id and (folder_key = lower(source) or left(folder_key, length(source) + 1) = lower(source) || '/');
    elsif new.kind in ('folder_deleted', 'folder_purged') and removed_folder <> '' then
        delete from public.armory_shared_folders where project_id = new.project_id
            and (folder_key = lower(removed_folder) or left(folder_key, length(removed_folder) + 1) = lower(removed_folder) || '/');
    elsif new.kind = 'project_purged' then
        delete from public.armory_shared_folders where project_id = new.project_id;
    end if;
    return new;
end $$;
revoke all on function public.armory_update_folder_paths() from public, anon, authenticated, service_role;
drop trigger if exists armory_update_folder_paths on public.armory_change_feed;
create trigger armory_update_folder_paths after insert on public.armory_change_feed
    for each row when (new.kind in ('folder_renamed', 'folder_deleted', 'folder_purged', 'project_purged'))
    execute function public.armory_update_folder_paths();

create or replace function public.armory_list_folder_paths(p_project uuid) returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare paths jsonb; managed jsonb := '[]'::jsonb;
begin
    if not public.armory_is_member(p_project) then
        raise exception 'not a project member' using errcode = '42501';
    end if;
    select coalesce(jsonb_agg(folder order by folder_key), '[]'::jsonb) into paths
        from public.armory_shared_folders where project_id = p_project;
    -- The website's class folders already have metadata. Keep their ownership rules intact.
    if to_regprocedure('public.armory_folder_list(uuid)') is not null then
        execute 'select coalesce(jsonb_agg(x->>''folder''), ''[]''::jsonb) from jsonb_array_elements(public.armory_folder_list($1)) x'
            into managed using p_project;
    end if;
    return paths || managed;
end $$;

create or replace function public.armory_edit_folder(p_project uuid, p_action text, p_from text, p_to text,
    p_device uuid, p_operation uuid) returns integer
language plpgsql security definer set search_path = '' as $$
declare r jsonb; n integer := 0; metadata integer := 0; child_operation uuid;
begin
    p_from := normalize(p_from, NFC);
    p_to := normalize(p_to, NFC);
    -- Same lock order as the existing folder RPCs. Their transaction, locks, permissions and
    -- receipts still decide every file action. No file bytes or versions are deleted here.
    perform 1 from public.armory_projects where id = p_project for update;
    if not public.armory_is_member(p_project) then
        raise exception 'not a project member' using errcode = '42501';
    end if;
    perform public.armory_require_device(p_device);
    if exists (select 1 from public.armory_projects where id = p_project and archived_at is not null) then
        raise exception 'Restore the project first.' using errcode = '55000';
    end if;
    r := public.armory_replay(p_operation, 'armory_edit_folder');
    if r is not null then return (r->>'result')::integer; end if;
    if p_action is null or p_action not in ('create', 'rename', 'delete') or p_from is null or p_from = '' or not public.armory_valid_folder(p_from) then
        raise exception 'Choose a valid folder.' using errcode = '22023';
    end if;
    if p_action = 'create' then
        if to_regprocedure('public._armory_refuse_folder_write(uuid,text)') is not null then
            perform public._armory_refuse_folder_write(p_project, p_from);
        end if;
        if exists (select 1 from public.armory_files where project_id = p_project and deleted_at is null
            and lower((case when folder = '' then '' else folder || '/' end) || name) = lower(p_from)) then
            raise exception 'A file already has that name.' using errcode = '23505';
        end if;
        insert into public.armory_shared_folders(project_id, folder) values (p_project, p_from) on conflict do nothing;
        get diagnostics metadata = row_count;
        if metadata > 0 then perform public.armory_add_change(p_project, 'folder_created', p_project, jsonb_build_object('folder', p_from)); end if;
    else
        child_operation := md5('armory-folder-files:' || p_operation::text)::uuid;
        if p_action = 'rename' then
            if p_to is null or p_to = '' or not public.armory_valid_folder(p_to) or p_to = p_from
                or left(lower(p_to), length(p_from) + 1) = lower(p_from) || '/' then
                raise exception 'Choose a different folder outside this one.' using errcode = '22023';
            end if;
            if exists (select 1 from public.armory_shared_folders where project_id = p_project
                and (folder_key = lower(p_to) or left(folder_key, length(p_to) + 1) = lower(p_to) || '/')
                and not (folder_key = lower(p_from) or left(folder_key, length(p_from) + 1) = lower(p_from) || '/')) then
                raise exception 'The target folder already exists.' using errcode = '55006';
            end if;
            -- With no source files the original RPC has no move to check. Metadata must
            -- still refuse merging an empty folder into someone else's existing work.
            if exists (select 1 from public.armory_files where project_id = p_project and deleted_at is null
                and not (lower(folder) = lower(p_from) or left(lower(folder), length(p_from) + 1) = lower(p_from) || '/')
                and (lower(folder) = lower(p_to) or left(lower(folder), length(p_to) + 1) = lower(p_to) || '/'
                    or lower((case when folder = '' then '' else folder || '/' end) || name) = lower(p_to))) then
                raise exception 'The target folder already exists.' using errcode = '55006', detail = '{"reason":"target_exists"}';
            end if;
            n := public.armory_rename_folder(p_project, p_from, p_to, p_device, child_operation);
            update public.armory_shared_folders set folder = p_to || substr(folder, length(p_from) + 1)
                where project_id = p_project and (folder_key = lower(p_from) or left(folder_key, length(p_from) + 1) = lower(p_from) || '/');
            get diagnostics metadata = row_count;
            if n = 0 and metadata > 0 then
                perform public.armory_add_change(p_project, 'folder_renamed', p_project, jsonb_build_object('from', p_from, 'to', p_to, 'files', 0, 'device_id', p_device));
            end if;
        else
            n := public.armory_delete_folder(p_project, p_from, p_device, child_operation);
            delete from public.armory_shared_folders where project_id = p_project
                and (folder_key = lower(p_from) or left(folder_key, length(p_from) + 1) = lower(p_from) || '/');
            get diagnostics metadata = row_count;
            if n = 0 and metadata > 0 then
                perform public.armory_add_change(p_project, 'folder_deleted', p_project, jsonb_build_object('folder', p_from, 'files', 0, 'device_id', p_device));
            end if;
        end if;
    end if;
    perform public.armory_remember(p_operation, 'armory_edit_folder', jsonb_build_object('result', n));
    return n;
end $$;
revoke all on function public.armory_list_folder_paths(uuid), public.armory_edit_folder(uuid,text,text,text,uuid,uuid) from public, anon, authenticated, service_role;
grant execute on function public.armory_list_folder_paths(uuid), public.armory_edit_folder(uuid,text,text,text,uuid,uuid) to authenticated;
do $check$
declare fn text; n integer;
begin
    foreach fn in array array['armory_list_folder_paths(uuid)', 'armory_edit_folder(uuid,text,text,text,uuid,uuid)'] loop
        if has_function_privilege('anon', 'public.' || fn, 'execute')
            or not has_function_privilege('authenticated', 'public.' || fn, 'execute')
            or has_function_privilege('service_role', 'public.' || fn, 'execute') then
            raise exception '0410: unexpected folder RPC grants for %', fn;
        end if;
    end loop;
    if has_function_privilege('authenticated', 'public.armory_update_folder_paths()', 'execute')
        or has_table_privilege('authenticated', 'public.armory_shared_folders', 'insert,update,delete')
        or has_table_privilege('anon', 'public.armory_shared_folders', 'select') then
        raise exception '0410: unexpected metadata write or anonymous read grant';
    end if;
    select count(*) into n from public.armory_shared_folders;
    raise notice '0410: % shared folder rows; no files, versions or storage removed', n;
end $check$;
