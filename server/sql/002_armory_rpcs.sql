create or replace function public.armory_current_email() returns text language plpgsql stable security definer set search_path='' as $$
declare v text := public.current_user_email();
begin if v is null then raise exception 'current user email is unavailable'; end if; return v; end $$;
create function public.armory_is_member(p_project uuid) returns boolean language sql stable security definer set search_path='' as $$
 select exists(select 1 from public.armory_members where project_id=p_project and email=public.armory_current_email()) $$;
create function public.armory_add_change(p_project uuid,p_kind text,p_entity uuid,p_payload jsonb default '{}') returns bigint language plpgsql security definer set search_path='' as $$
declare c bigint; begin insert into public.armory_change_feed(project_id,kind,entity_id,payload) values(p_project,p_kind,p_entity,p_payload) returning cursor into c; return c; end $$;

-- Serialize a stable operation ID before looking for its receipt. This makes simultaneous
-- delivery equivalent to sequential replay. NULL means this is the first delivery.
create function public.armory_replay(p_operation uuid,p_rpc text) returns jsonb language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); r public.armory_operation_receipts;
begin
 if p_operation is null then raise exception 'operation id is required'; end if;
 perform pg_advisory_xact_lock(hashtextextended(p_operation::text,0));
 select * into r from public.armory_operation_receipts where operation_id=p_operation;
 if found and (r.caller_email<>e or r.rpc_name<>p_rpc) then raise exception 'operation id was already used by another caller or RPC'; end if;
 if found then return r.result; end if; return null;
end $$;
create function public.armory_remember(p_operation uuid,p_rpc text,p_result jsonb) returns jsonb language plpgsql security definer set search_path='' as $$
begin insert into public.armory_operation_receipts(operation_id,caller_email,rpc_name,result) values(p_operation,public.armory_current_email(),p_rpc,p_result); return p_result; end $$;
create function public.armory_require_device(p_device uuid) returns void language plpgsql stable security definer set search_path='' as $$
begin if not exists(select 1 from public.armory_devices where id=p_device and owner_email=public.armory_current_email()) then raise exception 'device is not registered to caller'; end if; end $$;

create function public.armory_register_device(p_name text,p_operation uuid) returns uuid language plpgsql security definer set search_path='' as $$
declare r jsonb; d uuid;
begin r:=public.armory_replay(p_operation,'armory_register_device'); if r is not null then return (r->>'device_id')::uuid; end if;
 if btrim(coalesce(p_name,''))='' then raise exception 'device name is required'; end if;
 insert into public.armory_devices(owner_email,name) values(public.armory_current_email(),p_name) returning id into d;
 perform public.armory_remember(p_operation,'armory_register_device',jsonb_build_object('device_id',d)); return d; end $$;

create function public.armory_acquire_lock(p_file uuid,p_device uuid,p_operation uuid) returns boolean language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); p uuid; r jsonb; answer boolean;
begin r:=public.armory_replay(p_operation,'armory_acquire_lock'); if r is not null then return (r->>'result')::boolean; end if; perform public.armory_require_device(p_device);
 select project_id into p from public.armory_files where id=p_file; if e='' or not public.armory_is_member(p) then raise exception 'not a project member'; end if;
 insert into public.armory_locks(file_id,holder_email,holder_device_id) values(p_file,e,p_device)
 on conflict(file_id) do update set holder_email=excluded.holder_email,holder_device_id=excluded.holder_device_id,acquired_at=now(),broken_at=null,broken_by=null,broken_holder_email=null,broken_holder_device_id=null where public.armory_locks.broken_at is not null;
 answer:=exists(select 1 from public.armory_locks where file_id=p_file and holder_email=e and holder_device_id=p_device and broken_at is null);
 if answer then perform public.armory_add_change(p,'lock_acquired',p_file,jsonb_build_object('holder',e,'device_id',p_device)); end if;
 perform public.armory_remember(p_operation,'armory_acquire_lock',jsonb_build_object('result',answer)); return answer; end $$;
create function public.armory_release_lock(p_file uuid,p_device uuid,p_operation uuid) returns boolean language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); p uuid; n int; r jsonb; answer boolean;
begin r:=public.armory_replay(p_operation,'armory_release_lock'); if r is not null then return (r->>'result')::boolean; end if; perform public.armory_require_device(p_device);
 delete from public.armory_locks where file_id=p_file and holder_email=e and holder_device_id=p_device and broken_at is null; get diagnostics n=row_count; answer:=n>0;
 if answer then select project_id into p from public.armory_files where id=p_file; perform public.armory_add_change(p,'lock_released',p_file,jsonb_build_object('device_id',p_device)); end if;
 perform public.armory_remember(p_operation,'armory_release_lock',jsonb_build_object('result',answer)); return answer; end $$;
create function public.armory_break_lock(p_file uuid,p_device uuid,p_operation uuid) returns boolean language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); p uuid; n int; r jsonb; answer boolean; old_email text; old_device uuid;
begin r:=public.armory_replay(p_operation,'armory_break_lock'); if r is not null then return (r->>'result')::boolean; end if; perform public.armory_require_device(p_device); select project_id into p from public.armory_files where id=p_file;
 if not exists(select 1 from public.armory_members where project_id=p and email=e and role in ('mentor','cad_lead')) then raise exception 'only a mentor or cad_lead may break a lock'; end if;
 select holder_email,holder_device_id into old_email,old_device from public.armory_locks where file_id=p_file and broken_at is null for update;
 update public.armory_locks set broken_at=now(),broken_by=e,broken_holder_email=holder_email,broken_holder_device_id=holder_device_id where file_id=p_file and broken_at is null; get diagnostics n=row_count; answer:=n>0;
 if answer then perform public.armory_add_change(p,'lock_broken',p_file,jsonb_build_object('by',e,'former_holder',old_email,'former_device_id',old_device)); end if;
 perform public.armory_remember(p_operation,'armory_break_lock',jsonb_build_object('result',answer)); return answer; end $$;
create function public.armory_save_side_version(p_file uuid,p_parent uuid,p_key text,p_hash text,p_bytes bigint,p_reason text,p_device uuid,p_operation uuid) returns uuid language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); p uuid; v uuid; r jsonb;
begin r:=public.armory_replay(p_operation,'armory_save_side_version'); if r is not null then return (r->>'version_id')::uuid; end if; perform public.armory_require_device(p_device);
 select project_id into p from public.armory_files where id=p_file; if not public.armory_is_member(p) then raise exception 'not a project member'; end if;
 insert into public.armory_side_versions(file_id,parent_version_id,object_key,content_sha256,byte_length,author_email,reason) values(p_file,p_parent,p_key,p_hash,p_bytes,e,p_reason) returning id into v;
 perform public.armory_add_change(p,'side_version',v,jsonb_build_object('file_id',p_file,'device_id',p_device)); perform public.armory_remember(p_operation,'armory_save_side_version',jsonb_build_object('version_id',v)); return v; end $$;
create function public.armory_commit_version(p_file uuid,p_parent uuid,p_key text,p_hash text,p_bytes bigint,p_device uuid,p_operation uuid) returns table(version_id uuid, advanced boolean) language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); f public.armory_files; v uuid; a boolean; r jsonb;
begin r:=public.armory_replay(p_operation,'armory_commit_version'); if r is not null then return query select (r->>'version_id')::uuid,(r->>'advanced')::boolean; return; end if; perform public.armory_require_device(p_device);
 select * into f from public.armory_files where id=p_file for update; if not found then raise exception 'file not found'; end if;
 if not exists(select 1 from public.armory_locks where file_id=p_file and holder_email=e and holder_device_id=p_device and broken_at is null) then
  insert into public.armory_side_versions(file_id,parent_version_id,object_key,content_sha256,byte_length,author_email,reason) values(p_file,p_parent,p_key,p_hash,p_bytes,e,'caller does not hold lock') returning id into v; a:=false;
  perform public.armory_add_change(f.project_id,'side_version',v,jsonb_build_object('file_id',p_file,'device_id',p_device));
 elsif f.current_version_id is distinct from p_parent then
  insert into public.armory_side_versions(file_id,parent_version_id,object_key,content_sha256,byte_length,author_email,reason) values(p_file,p_parent,p_key,p_hash,p_bytes,e,'stale parent') returning id into v; a:=false;
  perform public.armory_add_change(f.project_id,'side_version',v,jsonb_build_object('file_id',p_file,'device_id',p_device));
 else insert into public.armory_versions(file_id,parent_version_id,object_key,content_sha256,byte_length,author_email) values(p_file,p_parent,p_key,p_hash,p_bytes,e) returning id into v; update public.armory_files set current_version_id=v where id=p_file; a:=true; perform public.armory_add_change(f.project_id,'version',v,jsonb_build_object('file_id',p_file,'device_id',p_device)); end if;
 perform public.armory_remember(p_operation,'armory_commit_version',jsonb_build_object('version_id',v,'advanced',a)); return query select v,a; end $$;
create function public.armory_tombstone(p_file uuid,p_parent uuid,p_device uuid,p_operation uuid) returns boolean language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); f public.armory_files; r jsonb; answer boolean;
begin r:=public.armory_replay(p_operation,'armory_tombstone'); if r is not null then return (r->>'result')::boolean; end if; perform public.armory_require_device(p_device); select * into f from public.armory_files where id=p_file for update;
 answer:=f.current_version_id is not distinct from p_parent and exists(select 1 from public.armory_locks where file_id=p_file and holder_email=e and holder_device_id=p_device and broken_at is null);
 if answer then insert into public.armory_tombstones(file_id,version_id,author_email) values(p_file,p_parent,e) on conflict do nothing; update public.armory_files set deleted_at=now() where id=p_file; perform public.armory_add_change(f.project_id,'tombstone',p_file,jsonb_build_object('device_id',p_device)); end if;
 perform public.armory_remember(p_operation,'armory_tombstone',jsonb_build_object('result',answer)); return answer; end $$;
create function public.armory_allocate_part_number(p_project uuid,p_subsystem int,p_season int,p_operation uuid) returns table(part_number text, subsystem_full boolean) language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); s int; n int; pat text; r jsonb;
begin r:=public.armory_replay(p_operation,'armory_allocate_part_number'); if r is not null then part_number:=r->>'part_number'; subsystem_full:=(r->>'subsystem_full')::boolean; return next; return; end if;
 if not public.armory_is_member(p_project) then raise exception 'not a project member'; end if; if p_subsystem not between 0 and 99 then raise exception 'invalid subsystem'; end if; select coalesce(p_season,season),part_number_pattern into s,pat from public.armory_projects where id=p_project for update;
 select x into n from generate_series(0,99)x where not exists(select 1 from public.armory_part_number_allocations where project_id=p_project and season=s and subsystem=p_subsystem and sequence=x) limit 1;
 if n is null then part_number:=null; subsystem_full:=true; else part_number:=replace(replace(replace(replace(pat,'{YY}',lpad((s%100)::text,2,'0')),'{SS}',lpad(p_subsystem::text,2,'0')),'{NN}',lpad(n::text,2,'0')),'YYYY',s::text); insert into public.armory_part_number_allocations(project_id,season,subsystem,sequence,part_number,allocated_by) values(p_project,s,p_subsystem,n,part_number,e); subsystem_full:=false; end if;
 perform public.armory_remember(p_operation,'armory_allocate_part_number',jsonb_build_object('part_number',part_number,'subsystem_full',subsystem_full)); return next; end $$;
create function public.armory_list_changes(p_project uuid,p_after bigint default 0) returns setof public.armory_change_feed language plpgsql stable security definer set search_path='' as $$
begin if not public.armory_is_member(p_project) then raise exception 'not a project member'; end if; return query select * from public.armory_change_feed where project_id=p_project and cursor>p_after order by cursor; end $$;

revoke all on function public.armory_current_email(),public.armory_is_member(uuid),public.armory_add_change(uuid,text,uuid,jsonb),public.armory_replay(uuid,text),public.armory_remember(uuid,text,jsonb),public.armory_require_device(uuid) from public,anon;
grant execute on function public.armory_is_member(uuid) to authenticated;
grant execute on function public.armory_register_device(text,uuid),public.armory_acquire_lock(uuid,uuid,uuid),public.armory_release_lock(uuid,uuid,uuid),public.armory_break_lock(uuid,uuid,uuid),public.armory_save_side_version(uuid,uuid,text,text,bigint,text,uuid,uuid),public.armory_commit_version(uuid,uuid,text,text,bigint,uuid,uuid),public.armory_tombstone(uuid,uuid,uuid,uuid),public.armory_allocate_part_number(uuid,int,int,uuid),public.armory_list_changes(uuid,bigint) to authenticated;
revoke all on function public.armory_register_device(text,uuid),public.armory_acquire_lock(uuid,uuid,uuid),public.armory_release_lock(uuid,uuid,uuid),public.armory_break_lock(uuid,uuid,uuid),public.armory_save_side_version(uuid,uuid,text,text,bigint,text,uuid,uuid),public.armory_commit_version(uuid,uuid,text,text,bigint,uuid,uuid),public.armory_tombstone(uuid,uuid,uuid,uuid),public.armory_allocate_part_number(uuid,int,int,uuid),public.armory_list_changes(uuid,bigint) from public,anon;
