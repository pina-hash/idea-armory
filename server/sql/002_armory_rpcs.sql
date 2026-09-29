create or replace function public.armory_current_email() returns text language plpgsql stable security definer set search_path='' as $$
declare v text := public.current_user_email();
begin
 if v is null then raise exception 'current user email is unavailable'; end if;
 return v;
end $$;
create function public.armory_is_member(p_project uuid) returns boolean language sql stable security definer set search_path='' as $$
 select exists(select 1 from public.armory_members where project_id=p_project and email=public.armory_current_email()) $$;
create function public.armory_add_change(p_project uuid,p_kind text,p_entity uuid,p_payload jsonb default '{}') returns bigint language plpgsql security definer set search_path='' as $$
declare c bigint; begin insert into public.armory_change_feed(project_id,kind,entity_id,payload) values(p_project,p_kind,p_entity,p_payload) returning cursor into c; return c; end $$;

create function public.armory_acquire_lock(p_file uuid) returns boolean language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); p uuid;
begin select project_id into p from public.armory_files where id=p_file; if e='' or not public.armory_is_member(p) then raise exception 'not a project member'; end if;
 insert into public.armory_locks(file_id,holder_email) values(p_file,e)
 on conflict(file_id) do update set holder_email=excluded.holder_email,acquired_at=now(),broken_at=null,broken_by=null
 where public.armory_locks.broken_at is not null;
 if not exists(select 1 from public.armory_locks where file_id=p_file and holder_email=e and broken_at is null) then return false; end if;
 perform public.armory_add_change(p,'lock_acquired',p_file,jsonb_build_object('holder',e)); return true; end $$;
create function public.armory_release_lock(p_file uuid) returns boolean language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); p uuid; n int;
begin delete from public.armory_locks where file_id=p_file and holder_email=e and broken_at is null returning file_id into p; get diagnostics n=row_count; if n=0 then return false; end if;
 select project_id into p from public.armory_files where id=p_file; perform public.armory_add_change(p,'lock_released',p_file); return true; end $$;
create function public.armory_break_lock(p_file uuid) returns boolean language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); p uuid; n int;
begin select project_id into p from public.armory_files where id=p_file;
 if not exists(select 1 from public.armory_members where project_id=p and email=e and role in ('mentor','cad_lead')) then raise exception 'only a mentor or cad_lead may break a lock'; end if;
 update public.armory_locks set broken_at=now(),broken_by=e where file_id=p_file and broken_at is null; get diagnostics n=row_count;
 if n>0 then perform public.armory_add_change(p,'lock_broken',p_file,jsonb_build_object('by',e)); end if; return n>0; end $$;
create function public.armory_save_side_version(p_file uuid,p_parent uuid,p_key text,p_hash text,p_bytes bigint,p_reason text default 'conflict') returns uuid language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); p uuid; v uuid;
begin select project_id into p from public.armory_files where id=p_file; if not public.armory_is_member(p) then raise exception 'not a project member'; end if;
 insert into public.armory_side_versions(file_id,parent_version_id,object_key,content_sha256,byte_length,author_email,reason) values(p_file,p_parent,p_key,p_hash,p_bytes,e,p_reason) returning id into v;
 perform public.armory_add_change(p,'side_version',v,jsonb_build_object('file_id',p_file)); return v; end $$;
create function public.armory_commit_version(p_file uuid,p_parent uuid,p_key text,p_hash text,p_bytes bigint) returns table(version_id uuid, advanced boolean) language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); f public.armory_files; v uuid;
begin select * into f from public.armory_files where id=p_file for update; if not found then raise exception 'file not found'; end if;
 if not exists(select 1 from public.armory_locks where file_id=p_file and holder_email=e and broken_at is null) then raise exception 'caller does not hold lock'; end if;
 if f.current_version_id is distinct from p_parent then v:=public.armory_save_side_version(p_file,p_parent,p_key,p_hash,p_bytes,'stale parent'); return query select v,false; return; end if;
 insert into public.armory_versions(file_id,parent_version_id,object_key,content_sha256,byte_length,author_email) values(p_file,p_parent,p_key,p_hash,p_bytes,e) returning id into v;
 update public.armory_files set current_version_id=v where id=p_file; perform public.armory_add_change(f.project_id,'version',v,jsonb_build_object('file_id',p_file)); return query select v,true; end $$;
create function public.armory_tombstone(p_file uuid,p_parent uuid) returns boolean language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); f public.armory_files;
begin select * into f from public.armory_files where id=p_file for update; if f.current_version_id is distinct from p_parent then return false; end if;
 if not exists(select 1 from public.armory_locks where file_id=p_file and holder_email=e and broken_at is null) then raise exception 'caller does not hold lock'; end if;
 insert into public.armory_tombstones(file_id,version_id,author_email) values(p_file,p_parent,e) on conflict do nothing; update public.armory_files set deleted_at=now() where id=p_file;
 perform public.armory_add_change(f.project_id,'tombstone',p_file); return true; end $$;
create function public.armory_allocate_part_number(p_project uuid,p_subsystem int,p_season int default null) returns table(part_number text, subsystem_full boolean) language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); s int; n int; pat text;
begin if not public.armory_is_member(p_project) then raise exception 'not a project member'; end if; if p_subsystem not between 0 and 99 then raise exception 'invalid subsystem'; end if;
 select coalesce(p_season,season),part_number_pattern into s,pat from public.armory_projects where id=p_project for update;
 select x into n from generate_series(0,99)x where not exists(select 1 from public.armory_part_number_allocations where project_id=p_project and season=s and subsystem=p_subsystem and sequence=x) limit 1;
 if n is null then return query select null::text,true; return; end if;
 part_number:=replace(replace(replace(replace(pat,'{YY}',lpad((s%100)::text,2,'0')),'{SS}',lpad(p_subsystem::text,2,'0')),'{NN}',lpad(n::text,2,'0')),'YYYY',s::text);
 insert into public.armory_part_number_allocations(project_id,season,subsystem,sequence,part_number,allocated_by) values(p_project,s,p_subsystem,n,part_number,e); subsystem_full:=false; return next; end $$;
create function public.armory_list_changes(p_project uuid,p_after bigint default 0) returns setof public.armory_change_feed language plpgsql stable security definer set search_path='' as $$
begin if not public.armory_is_member(p_project) then raise exception 'not a project member'; end if; return query select * from public.armory_change_feed where project_id=p_project and cursor>p_after order by cursor; end $$;

revoke all on function public.armory_current_email(),public.armory_is_member(uuid),public.armory_add_change(uuid,text,uuid,jsonb) from public,anon;
grant execute on function public.armory_is_member(uuid) to authenticated;
grant execute on function public.armory_acquire_lock(uuid),public.armory_release_lock(uuid),public.armory_break_lock(uuid),public.armory_save_side_version(uuid,uuid,text,text,bigint,text),public.armory_commit_version(uuid,uuid,text,text,bigint),public.armory_tombstone(uuid,uuid),public.armory_allocate_part_number(uuid,int,int),public.armory_list_changes(uuid,bigint) to authenticated;
revoke all on function public.armory_acquire_lock(uuid),public.armory_release_lock(uuid),public.armory_break_lock(uuid),public.armory_save_side_version(uuid,uuid,text,text,bigint,text),public.armory_commit_version(uuid,uuid,text,text,bigint),public.armory_tombstone(uuid,uuid),public.armory_allocate_part_number(uuid,int,int),public.armory_list_changes(uuid,bigint) from public,anon;
