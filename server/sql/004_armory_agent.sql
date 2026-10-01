-- Draft contract only. idea-app will assign the migration number after review.
-- Lane A additions for the Windows agent (docs/agent/CONTRACT.md section 6), plus the
-- per-project SolidWorks release gate and the agent's read snapshots. Files 001-003 are
-- unchanged; every write here is a security-definer RPC with an operation receipt and a
-- change-feed entry, identity only from current_user_email(), and no direct table writes.

-- idea-app's production current_user_email() returns '' (not null) when no user is signed
-- in. Treat both the same way, so an empty identity can never act.
create or replace function public.armory_current_email() returns text language plpgsql stable security definer set search_path='' as $$
declare v text := public.current_user_email();
begin if v is null or v='' then raise exception 'current user email is unavailable' using errcode='42501'; end if; return v; end $$;

alter table public.armory_files add column folder text not null default '';
alter table public.armory_projects
 add column pinned_release smallint not null default 2025 check (pinned_release >= 1995),
 add column release_gate text not null default 'warn' check (release_gate in ('enforce','warn'));

-- One immutable row per SolidWorks version or side version: the saved release the agent
-- read, or null when no reader could check it ("release not checked").
create table public.armory_version_releases (
 version_id uuid primary key, file_id uuid not null references public.armory_files, side boolean not null,
 saved_release smallint check (saved_release is null or saved_release >= 1995),
 release_checked boolean generated always as (saved_release is not null) stored,
 recorded_by text not null, created_at timestamptz not null default now());
create trigger armory_version_releases_immutable before update or delete on public.armory_version_releases
 for each row execute function public.armory_refuse_version_mutation();

-- VaultPath.TryValidateName in Armory.Core, in SQL. The agent's VaultIgnore list is also
-- refused, because the agent would never sync such a name.
create function public.armory_valid_segment(p text) returns boolean language plpgsql immutable set search_path='' as $$
declare stem text;
begin
 if p is null or p='' or p in ('.','..') then return false; end if;
 if p ~ '[\u0001-\u001f\u007f-\u009f<>:"/\\|?*]' then return false; end if;
 if right(p,1) in ('.',' ') then return false; end if;
 stem := upper(rtrim(split_part(p,'.',1),' '));
 if stem in ('CON','PRN','AUX','NUL','CONIN$','CONOUT$') then return false; end if;
 if length(stem)=4 and left(stem,3) in ('COM','LPT') and position(substr(stem,4,1) in '123456789¹²³')>0 then return false; end if;
 if left(p,2)='~$' or lower(p) in ('.armory','desktop.ini','thumbs.db') then return false; end if;
 return true;
end $$;
create function public.armory_valid_folder(p text) returns boolean language plpgsql immutable set search_path='' as $$
declare s text;
begin
 if p is null then return false; end if;
 if p='' then return true; end if;
 foreach s in array string_to_array(p,'/') loop
  if not public.armory_valid_segment(s) then return false; end if;
 end loop;
 return true;
end $$;
create function public.armory_derived_operation(p_operation uuid,p_purpose text) returns uuid language sql immutable set search_path='' as $$
 select md5(p_operation::text||':'||p_purpose)::uuid $$;
create function public.armory_require_role(p_project uuid,p_roles public.armory_member_role[],p_message text) returns public.armory_member_role language plpgsql stable security definer set search_path='' as $$
declare r public.armory_member_role;
begin
 select role into r from public.armory_members where project_id=p_project and email=public.armory_current_email();
 if r is null or not (r = any(p_roles)) then raise exception '%',p_message using errcode='42501'; end if;
 return r;
end $$;
create function public.armory_name_taken(p_project uuid,p_name text,p_except uuid) returns void language plpgsql stable security definer set search_path='' as $$
declare f public.armory_files;
begin
 select * into f from public.armory_files where project_id=p_project and lower(normalize(name,NFC))=lower(normalize(p_name,NFC)) and id is distinct from p_except limit 1;
 if found then
  raise exception 'A file named "%" already exists in this project.',f.name using errcode='23505',
   detail=jsonb_build_object('existing_folder',f.folder,'existing_name',f.name,'file_id',f.id)::text,
   hint='Choose another name, or open the existing file.';
 end if;
end $$;

create function public.armory_create_project(p_name text,p_season smallint,p_operation uuid) returns uuid language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); r jsonb; n text; v uuid; existing text;
begin r:=public.armory_replay(p_operation,'armory_create_project'); if r is not null then return (r->>'project_id')::uuid; end if;
 if not coalesce(public.is_admin(),false) then raise exception 'only a site admin may create an Armory project' using errcode='42501'; end if;
 n:=normalize(coalesce(p_name,''),NFC);
 if not public.armory_valid_segment(n) then raise exception 'The project name "%" cannot be a Windows folder name.',n using errcode='22023'; end if;
 if p_season is null or p_season not between 2000 and 2100 then raise exception 'season must be a year from 2000 to 2100' using errcode='22023'; end if;
 -- Each project is a folder under the vault root, so names are unique without regard to case.
 perform pg_advisory_xact_lock(hashtextextended('armory_project_name:'||lower(n),0));
 select name into existing from public.armory_projects where lower(normalize(name,NFC))=lower(n) limit 1;
 if found then raise exception 'A project named "%" already exists.',existing using errcode='23505',detail=jsonb_build_object('existing_name',existing)::text; end if;
 insert into public.armory_projects(name,season) values(n,p_season) returning id into v;
 insert into public.armory_members(project_id,email,role) values(v,e,'mentor');
 perform public.armory_add_change(v,'project_created',v,jsonb_build_object('name',n,'season',p_season,'by',e));
 perform public.armory_remember(p_operation,'armory_create_project',jsonb_build_object('project_id',v)); return v; end $$;

create function public.armory_add_member(p_project uuid,p_email text,p_role public.armory_member_role,p_operation uuid) returns boolean language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); r jsonb; caller public.armory_member_role; target text; old public.armory_member_role; mentors int; answer boolean;
begin r:=public.armory_replay(p_operation,'armory_add_member'); if r is not null then return (r->>'result')::boolean; end if;
 -- Serialize every membership change in a project so the last-mentor rule cannot race.
 perform 1 from public.armory_projects where id=p_project for update;
 caller:=public.armory_require_role(p_project,array['mentor','cad_lead']::public.armory_member_role[],'only a mentor or CAD lead may add members');
 if p_role is null then raise exception 'a role is required' using errcode='22023'; end if;
 target:=lower(btrim(coalesce(p_email,'')));
 if target !~ '^[^@[:space:]]+@[^@[:space:]]+$' then raise exception 'a valid email is required' using errcode='22023'; end if;
 if p_role in ('mentor','cad_lead') and caller<>'mentor' then raise exception 'only a mentor may grant mentor or cad_lead' using errcode='42501'; end if;
 select role into old from public.armory_members where project_id=p_project and email=target;
 if old in ('mentor','cad_lead') and caller<>'mentor' then raise exception 'only a mentor may change a mentor or cad_lead' using errcode='42501'; end if;
 if old='mentor' and p_role<>'mentor' then
  select count(*) into mentors from public.armory_members where project_id=p_project and role='mentor';
  if mentors<=1 then raise exception 'A project always keeps at least one mentor.' using errcode='P0001'; end if;
 end if;
 if old is null then
  insert into public.armory_members(project_id,email,role) values(p_project,target,p_role); answer:=true;
  perform public.armory_add_change(p_project,'member_added',p_project,jsonb_build_object('email',target,'role',p_role,'by',e));
 elsif old<>p_role then
  update public.armory_members set role=p_role where project_id=p_project and email=target; answer:=true;
  perform public.armory_add_change(p_project,'member_role_changed',p_project,jsonb_build_object('email',target,'role',p_role,'previous_role',old,'by',e));
 else answer:=false; end if;
 perform public.armory_remember(p_operation,'armory_add_member',jsonb_build_object('result',answer)); return answer; end $$;

create function public.armory_remove_member(p_project uuid,p_email text,p_operation uuid) returns boolean language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); r jsonb; target text; old public.armory_member_role; mentors int; answer boolean;
begin r:=public.armory_replay(p_operation,'armory_remove_member'); if r is not null then return (r->>'result')::boolean; end if;
 perform 1 from public.armory_projects where id=p_project for update;
 perform public.armory_require_role(p_project,array['mentor']::public.armory_member_role[],'only a mentor may remove members');
 target:=lower(btrim(coalesce(p_email,'')));
 select role into old from public.armory_members where project_id=p_project and email=target;
 if old='mentor' then
  select count(*) into mentors from public.armory_members where project_id=p_project and role='mentor';
  if mentors<=1 then raise exception 'A project always keeps at least one mentor.' using errcode='P0001'; end if;
 end if;
 answer:=old is not null;
 if answer then
  delete from public.armory_members where project_id=p_project and email=target;
  perform public.armory_add_change(p_project,'member_removed',p_project,jsonb_build_object('email',target,'role',old,'by',e));
 end if;
 perform public.armory_remember(p_operation,'armory_remove_member',jsonb_build_object('result',answer)); return answer; end $$;

create function public.armory_create_file(p_project uuid,p_folder text,p_name text,p_device uuid,p_operation uuid) returns uuid language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); r jsonb; folder_n text; name_n text; v uuid;
begin r:=public.armory_replay(p_operation,'armory_create_file'); if r is not null then return (r->>'file_id')::uuid; end if;
 perform public.armory_require_device(p_device);
 if not public.armory_is_member(p_project) then raise exception 'not a project member' using errcode='42501'; end if;
 folder_n:=normalize(coalesce(p_folder,''),NFC); name_n:=normalize(coalesce(p_name,''),NFC);
 if not public.armory_valid_folder(folder_n) then raise exception 'The folder "%" cannot be a Windows folder path.',folder_n using errcode='22023'; end if;
 if not public.armory_valid_segment(name_n) then raise exception 'The name "%" cannot be a Windows file name.',name_n using errcode='22023'; end if;
 perform public.armory_name_taken(p_project,name_n,null);
 begin
  insert into public.armory_files(project_id,folder,name) values(p_project,folder_n,name_n) returning id into v;
 exception when unique_violation then
  -- A concurrent creator won; name the folder where its file now lives.
  perform public.armory_name_taken(p_project,name_n,null); raise;
 end;
 perform public.armory_add_change(p_project,'file_created',v,jsonb_build_object('folder',folder_n,'name',name_n,'device_id',p_device,'by',e));
 perform public.armory_remember(p_operation,'armory_create_file',jsonb_build_object('file_id',v)); return v; end $$;

create function public.armory_move_file(p_file uuid,p_folder text,p_name text,p_device uuid,p_operation uuid) returns boolean language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); r jsonb; f public.armory_files; folder_n text; name_n text; answer boolean;
begin r:=public.armory_replay(p_operation,'armory_move_file'); if r is not null then return (r->>'result')::boolean; end if;
 perform public.armory_require_device(p_device);
 select * into f from public.armory_files where id=p_file for update;
 if not found or not public.armory_is_member(f.project_id) then raise exception 'not a project member' using errcode='42501'; end if;
 folder_n:=normalize(coalesce(p_folder,''),NFC); name_n:=normalize(coalesce(p_name,''),NFC);
 if not public.armory_valid_folder(folder_n) then raise exception 'The folder "%" cannot be a Windows folder path.',folder_n using errcode='22023'; end if;
 if not public.armory_valid_segment(name_n) then raise exception 'The name "%" cannot be a Windows file name.',name_n using errcode='22023'; end if;
 answer:=f.deleted_at is null and exists(select 1 from public.armory_locks where file_id=p_file and holder_email=e and holder_device_id=p_device and broken_at is null);
 if answer and (f.folder<>folder_n or f.name<>name_n) then
  perform public.armory_name_taken(f.project_id,name_n,p_file);
  begin
   update public.armory_files set folder=folder_n,name=name_n where id=p_file;
  exception when unique_violation then perform public.armory_name_taken(f.project_id,name_n,p_file); raise;
  end;
  perform public.armory_add_change(f.project_id,'file_moved',p_file,jsonb_build_object('old_folder',f.folder,'old_name',f.name,'folder',folder_n,'name',name_n,'device_id',p_device,'by',e));
 end if;
 perform public.armory_remember(p_operation,'armory_move_file',jsonb_build_object('result',answer)); return answer; end $$;

-- Release gate. Warn (the default) accepts a SolidWorks file whose release could not be
-- read and marks it "release not checked"; enforce refuses it. A release known to be newer
-- than the pin is refused in both modes. Core decides first in the agent; this is the
-- server's own defense for the same rule.
create function public.armory_check_release(p_file uuid,p_saved_release smallint) returns boolean language plpgsql stable security definer set search_path='' as $$
declare f public.armory_files; p public.armory_projects;
begin
 select * into f from public.armory_files where id=p_file;
 if not found then raise exception 'file not found' using errcode='P0002'; end if;
 if f.name !~* '\.(sldprt|sldasm|slddrw)$' then return false; end if;
 select * into p from public.armory_projects where id=f.project_id;
 if p_saved_release is not null and p_saved_release > p.pinned_release then
  raise exception 'SolidWorks % cannot upload to a vault pinned to %. Keep this private draft or save to %.',p_saved_release,p.pinned_release,p.pinned_release using errcode='22023';
 end if;
 if p_saved_release is null and p.release_gate='enforce' then
  raise exception 'The saved SolidWorks release is unknown; keep the local draft until it can be read.' using errcode='22023';
 end if;
 return true;
end $$;
create function public.armory_record_release(p_version uuid,p_file uuid,p_side boolean,p_saved_release smallint) returns void language plpgsql security definer set search_path='' as $$
begin insert into public.armory_version_releases(version_id,file_id,side,saved_release,recorded_by) values(p_version,p_file,p_side,p_saved_release,public.armory_current_email()) on conflict (version_id) do nothing; end $$;

create function public.armory_commit_version_with_release(p_file uuid,p_parent uuid,p_key text,p_hash text,p_bytes bigint,p_device uuid,p_operation uuid,p_saved_release smallint) returns table(version_id uuid, advanced boolean) language plpgsql security definer set search_path='' as $$
declare r jsonb; v uuid; a boolean; solidworks boolean;
begin r:=public.armory_replay(p_operation,'armory_commit_version_with_release'); if r is not null then return query select (r->>'version_id')::uuid,(r->>'advanced')::boolean; return; end if;
 solidworks:=public.armory_check_release(p_file,p_saved_release);
 if exists(select 1 from public.armory_files where id=p_file and deleted_at is not null) then
  -- A removed file never advances, even for its lock holder: the bytes become a side version.
  v:=public.armory_save_side_version(p_file,p_parent,p_key,p_hash,p_bytes,'file deleted',p_device,public.armory_derived_operation(p_operation,'commit-deleted')); a:=false;
 else
  -- 002's armory_commit_version stays the one source of the lock and parent rules.
  select c.version_id,c.advanced into v,a from public.armory_commit_version(p_file,p_parent,p_key,p_hash,p_bytes,p_device,public.armory_derived_operation(p_operation,'commit')) c;
 end if;
 if solidworks then perform public.armory_record_release(v,p_file,not a,p_saved_release); end if;
 perform public.armory_remember(p_operation,'armory_commit_version_with_release',jsonb_build_object('version_id',v,'advanced',a));
 return query select v,a; end $$;
create function public.armory_save_side_version_with_release(p_file uuid,p_parent uuid,p_key text,p_hash text,p_bytes bigint,p_reason text,p_device uuid,p_operation uuid,p_saved_release smallint) returns uuid language plpgsql security definer set search_path='' as $$
declare r jsonb; v uuid; solidworks boolean;
begin r:=public.armory_replay(p_operation,'armory_save_side_version_with_release'); if r is not null then return (r->>'version_id')::uuid; end if;
 solidworks:=public.armory_check_release(p_file,p_saved_release);
 v:=public.armory_save_side_version(p_file,p_parent,p_key,p_hash,p_bytes,p_reason,p_device,public.armory_derived_operation(p_operation,'side'));
 if solidworks then perform public.armory_record_release(v,p_file,true,p_saved_release); end if;
 perform public.armory_remember(p_operation,'armory_save_side_version_with_release',jsonb_build_object('version_id',v)); return v; end $$;

create function public.armory_set_release_gate(p_project uuid,p_mode text,p_operation uuid) returns boolean language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); r jsonb; old text; answer boolean;
begin r:=public.armory_replay(p_operation,'armory_set_release_gate'); if r is not null then return (r->>'result')::boolean; end if;
 perform public.armory_require_role(p_project,array['mentor']::public.armory_member_role[],'only a mentor may change the release gate');
 if p_mode is null or p_mode not in ('enforce','warn') then raise exception 'the release gate is enforce or warn' using errcode='22023'; end if;
 select release_gate into old from public.armory_projects where id=p_project for update;
 answer:=old<>p_mode;
 if answer then
  update public.armory_projects set release_gate=p_mode where id=p_project;
  perform public.armory_add_change(p_project,'release_gate_changed',p_project,jsonb_build_object('mode',p_mode,'previous',old,'by',e));
 end if;
 perform public.armory_remember(p_operation,'armory_set_release_gate',jsonb_build_object('result',answer)); return answer; end $$;
create function public.armory_raise_pinned_release(p_project uuid,p_release smallint,p_operation uuid) returns boolean language plpgsql security definer set search_path='' as $$
declare e text:=public.armory_current_email(); r jsonb; old smallint;
begin r:=public.armory_replay(p_operation,'armory_raise_pinned_release'); if r is not null then return (r->>'result')::boolean; end if;
 perform public.armory_require_role(p_project,array['mentor']::public.armory_member_role[],'only a mentor may raise the pinned release');
 select pinned_release into old from public.armory_projects where id=p_project for update;
 -- SolidWorksVersionGate.TryRaise: the pin must strictly increase from a valid release.
 if p_release is null or p_release<=old then raise exception 'The pinned release must increase from %.',old using errcode='22023'; end if;
 update public.armory_projects set pinned_release=p_release where id=p_project;
 perform public.armory_add_change(p_project,'pinned_release_raised',p_project,jsonb_build_object('release',p_release,'previous',old,'by',e));
 perform public.armory_remember(p_operation,'armory_raise_pinned_release',jsonb_build_object('result',true)); return true; end $$;

-- Read snapshots for the agent. They return only what RLS would show a member.
create function public.armory_my_projects() returns jsonb language plpgsql stable security definer set search_path='' as $$
declare e text:=public.armory_current_email();
begin return coalesce((select jsonb_agg(jsonb_build_object('id',p.id,'name',p.name,'season',p.season,'role',m.role,
  'pinned_release',p.pinned_release,'release_gate',p.release_gate) order by lower(p.name),p.id)
 from public.armory_projects p join public.armory_members m on m.project_id=p.id and m.email=e),'[]'::jsonb); end $$;
create function public.armory_project_files(p_project uuid) returns jsonb language plpgsql stable security definer set search_path='' as $$
begin
 if not public.armory_is_member(p_project) then raise exception 'not a project member' using errcode='42501'; end if;
 return coalesce((select jsonb_agg(jsonb_build_object(
  'id',f.id,'folder',f.folder,'name',f.name,'deleted',f.deleted_at is not null,'created_at',f.created_at,
  'current',case when v.id is null then null else jsonb_build_object('id',v.id,'hash',v.content_sha256,'bytes',v.byte_length,
    'author',v.author_email,'created_at',v.created_at,'saved_release',rel.saved_release,'release_checked',rel.release_checked) end,
  'lock',case when l.file_id is null then null else jsonb_build_object('holder_email',l.holder_email,'holder_device_id',l.holder_device_id,
    'holder_device_name',d.name,'acquired_at',l.acquired_at,'broken_at',l.broken_at,'broken_by',l.broken_by,
    'broken_holder_email',l.broken_holder_email,'broken_holder_device_id',l.broken_holder_device_id) end)
  order by f.folder,lower(f.name),f.id)
 from public.armory_files f
 left join public.armory_versions v on v.id=f.current_version_id
 left join public.armory_version_releases rel on rel.version_id=v.id
 left join public.armory_locks l on l.file_id=f.id
 left join public.armory_devices d on d.id=l.holder_device_id
 where f.project_id=p_project),'[]'::jsonb); end $$;
create function public.armory_file_history(p_file uuid) returns jsonb language plpgsql stable security definer set search_path='' as $$
declare p uuid;
begin
 select project_id into p from public.armory_files where id=p_file;
 if p is null or not public.armory_is_member(p) then raise exception 'not a project member' using errcode='42501'; end if;
 return coalesce((select jsonb_agg(h order by (h->>'created_at')::timestamptz desc,h->>'id') from (
  select jsonb_build_object('id',v.id,'kind','version','author',v.author_email,'created_at',v.created_at,'bytes',v.byte_length,
   'hash',v.content_sha256,'parent',v.parent_version_id,'reason',null,'saved_release',rel.saved_release,'release_checked',rel.release_checked) h
  from public.armory_versions v left join public.armory_version_releases rel on rel.version_id=v.id where v.file_id=p_file
  union all
  select jsonb_build_object('id',s.id,'kind','side_version','author',s.author_email,'created_at',s.created_at,'bytes',s.byte_length,
   'hash',s.content_sha256,'parent',s.parent_version_id,'reason',s.reason,'saved_release',rel.saved_release,'release_checked',rel.release_checked)
  from public.armory_side_versions s left join public.armory_version_releases rel on rel.version_id=s.id where s.file_id=p_file
  union all
  select jsonb_build_object('id',t.file_id,'kind','tombstone','author',t.author_email,'created_at',t.created_at,'bytes',0,
   'hash',null,'parent',t.version_id,'reason',null,'saved_release',null,'release_checked',null)
  from public.armory_tombstones t where t.file_id=p_file) x(h)),'[]'::jsonb); end $$;

revoke all on function public.armory_valid_segment(text),public.armory_valid_folder(text),public.armory_derived_operation(uuid,text),
 public.armory_require_role(uuid,public.armory_member_role[],text),public.armory_name_taken(uuid,text,uuid),
 public.armory_check_release(uuid,smallint),public.armory_record_release(uuid,uuid,boolean,smallint) from public,anon,authenticated;
revoke all on function public.armory_create_project(text,smallint,uuid),public.armory_add_member(uuid,text,public.armory_member_role,uuid),
 public.armory_remove_member(uuid,text,uuid),public.armory_create_file(uuid,text,text,uuid,uuid),public.armory_move_file(uuid,text,text,uuid,uuid),
 public.armory_commit_version_with_release(uuid,uuid,text,text,bigint,uuid,uuid,smallint),
 public.armory_save_side_version_with_release(uuid,uuid,text,text,bigint,text,uuid,uuid,smallint),
 public.armory_set_release_gate(uuid,text,uuid),public.armory_raise_pinned_release(uuid,smallint,uuid),
 public.armory_my_projects(),public.armory_project_files(uuid),public.armory_file_history(uuid) from public,anon;
grant execute on function public.armory_create_project(text,smallint,uuid),public.armory_add_member(uuid,text,public.armory_member_role,uuid),
 public.armory_remove_member(uuid,text,uuid),public.armory_create_file(uuid,text,text,uuid,uuid),public.armory_move_file(uuid,text,text,uuid,uuid),
 public.armory_commit_version_with_release(uuid,uuid,text,text,bigint,uuid,uuid,smallint),
 public.armory_save_side_version_with_release(uuid,uuid,text,text,bigint,text,uuid,uuid,smallint),
 public.armory_set_release_gate(uuid,text,uuid),public.armory_raise_pinned_release(uuid,smallint,uuid),
 public.armory_my_projects(),public.armory_project_files(uuid),public.armory_file_history(uuid) to authenticated;
revoke all on public.armory_version_releases from public,anon,authenticated;
grant select on public.armory_version_releases to authenticated;
alter table public.armory_version_releases enable row level security;
create policy armory_version_releases_read on public.armory_version_releases for select to authenticated
 using(exists(select 1 from public.armory_files f where f.id=file_id and public.armory_is_member(f.project_id)));
