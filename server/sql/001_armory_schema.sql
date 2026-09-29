-- Draft contract only. idea-app will assign the migration number after review.
create extension if not exists pgcrypto;

create type public.armory_member_role as enum ('student', 'cad_lead', 'mentor', 'instructor');
create table public.armory_projects (
 id uuid primary key default gen_random_uuid(), name text not null,
 part_number_pattern text not null default '5669-{YY}-{SS}{NN}', season smallint not null,
 created_at timestamptz not null default now());
create table public.armory_members (
 project_id uuid not null references public.armory_projects on delete cascade,
 email text not null check (email=lower(btrim(email))), role public.armory_member_role not null,
 primary key(project_id,email));
create table public.armory_devices (
 id uuid primary key default gen_random_uuid(), owner_email text not null,
 name text not null check (btrim(name) <> ''), registered_at timestamptz not null default now(),
 unique(owner_email,id));
create table public.armory_files (
 id uuid primary key default gen_random_uuid(), project_id uuid not null references public.armory_projects,
 name text not null, current_version_id uuid, deleted_at timestamptz,
 created_at timestamptz not null default now());
-- normalize(..., NFC) is PostgreSQL's Unicode normalization, not an application convention.
create unique index armory_files_project_normalized_name_key
 on public.armory_files(project_id, lower(normalize(name, NFC)));
create table public.armory_versions (
 id uuid primary key default gen_random_uuid(), file_id uuid not null references public.armory_files,
 parent_version_id uuid references public.armory_versions, object_key text not null,
 content_sha256 text not null check(content_sha256 ~ '^[0-9a-f]{64}$'), byte_length bigint not null check(byte_length>=0),
 author_email text not null, created_at timestamptz not null default now());
alter table public.armory_files add constraint armory_files_current_version_fk
 foreign key(current_version_id) references public.armory_versions;
create table public.armory_side_versions (
 id uuid primary key default gen_random_uuid(), file_id uuid not null references public.armory_files,
 parent_version_id uuid references public.armory_versions, object_key text not null,
 content_sha256 text not null check(content_sha256 ~ '^[0-9a-f]{64}$'), byte_length bigint not null check(byte_length>=0),
 author_email text not null, reason text not null, created_at timestamptz not null default now());
create table public.armory_locks (
 file_id uuid primary key references public.armory_files, holder_email text not null,
 holder_device_id uuid not null references public.armory_devices,
 acquired_at timestamptz not null default now(), broken_at timestamptz, broken_by text,
 broken_holder_email text, broken_holder_device_id uuid references public.armory_devices);
create table public.armory_tombstones (
 file_id uuid primary key references public.armory_files, version_id uuid references public.armory_versions,
 author_email text not null, created_at timestamptz not null default now());
create table public.armory_part_number_allocations (
 id uuid primary key default gen_random_uuid(), project_id uuid not null references public.armory_projects,
 season smallint not null, subsystem smallint not null check(subsystem between 0 and 99),
 sequence integer not null check(sequence between 0 and 99), part_number text not null,
 allocated_by text not null, allocated_at timestamptz not null default now(),
 unique(project_id,season,subsystem,sequence), unique(project_id,part_number));
create table public.armory_change_feed (
 cursor bigint generated always as identity primary key, project_id uuid not null references public.armory_projects,
 kind text not null, entity_id uuid not null, payload jsonb not null default '{}',
 created_at timestamptz not null default now());
create table public.armory_operation_receipts (
 operation_id uuid primary key, caller_email text not null, rpc_name text not null,
 result jsonb not null, completed_at timestamptz not null default now());

create function public.armory_refuse_version_mutation() returns trigger language plpgsql set search_path='' as $$
begin raise exception 'Armory versions are immutable' using errcode='55000'; end $$;
create trigger armory_versions_immutable before update or delete on public.armory_versions
 for each row execute function public.armory_refuse_version_mutation();
create trigger armory_side_versions_immutable before update or delete on public.armory_side_versions
 for each row execute function public.armory_refuse_version_mutation();
