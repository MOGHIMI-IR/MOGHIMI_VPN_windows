-- Windows-MG isolated schema reference.
-- Safe to apply to a fresh Windows-MG project. No Android/mobile tables are referenced.
-- Service-only data access: all windows_* tables use RLS with no anon/authenticated policies.

create extension if not exists pgcrypto;

create table if not exists public.windows_purchase_orders (
  id uuid primary key default gen_random_uuid(),
  telegram_user_id bigint not null,
  telegram_username text not null,
  telegram_chat_id bigint not null,
  phone text not null check (phone ~ '^09[0-9]{9}$'),
  plan_kind text not null check (plan_kind in ('fixed','custom')),
  plan_bytes bigint not null check (plan_bytes > 0),
  plan_days integer not null check (plan_days between 1 and 3650),
  amount_toman bigint check (amount_toman is null or amount_toman between 1000 and 1000000000),
  status text not null default 'awaiting_payment'
    check (status in ('awaiting_quote','awaiting_payment','receipt_submitted','approved','rejected','expired','cancelled')),
  receipt_file_id text,
  receipt_message_id bigint,
  receipt_submitted_at timestamptz,
  created_at timestamptz not null default now(),
  payment_deadline_at timestamptz not null,
  reviewed_at timestamptz,
  reviewed_by bigint,
  admin_note text,
  activation_code_id uuid,
  bot_sync_status text not null default 'synced'
    check (bot_sync_status in ('pending','synced','failed')),
  bot_synced_at timestamptz
);

create table if not exists public.windows_activation_codes (
  id uuid primary key default gen_random_uuid(),
  order_id uuid not null references public.windows_purchase_orders(id) on delete restrict,
  code_hash text not null unique check (code_hash ~ '^[A-Fa-f0-9]{64}$'),
  telegram_user_id bigint not null,
  telegram_username text not null,
  phone text not null check (phone ~ '^09[0-9]{9}$'),
  granted_bytes bigint not null check (granted_bytes > 0),
  duration_days integer not null check (duration_days between 1 and 3650),
  expires_at timestamptz not null,
  status text not null default 'issued' check (status in ('issued','claimed','expired','revoked')),
  claimed_device_id uuid,
  claimed_at timestamptz,
  created_at timestamptz not null default now(),
  constraint windows_activation_codes_order_id_key unique (order_id)
);

create table if not exists public.windows_devices (
  id uuid primary key,
  telegram_user_id bigint not null,
  activation_code_id uuid references public.windows_activation_codes(id) on delete set null,
  machine_fingerprint_hash text,
  created_at timestamptz not null default now(),
  last_seen_at timestamptz,
  status text not null default 'active' check (status in ('active','blocked','revoked')),
  device_token_hash text
);

create table if not exists public.windows_subscriptions (
  id uuid primary key default gen_random_uuid(),
  device_id uuid not null references public.windows_devices(id) on delete restrict,
  activation_code_id uuid not null references public.windows_activation_codes(id) on delete restrict unique,
  telegram_user_id bigint not null,
  granted_bytes bigint not null check (granted_bytes > 0),
  used_bytes bigint not null default 0 check (used_bytes >= 0),
  starts_at timestamptz not null default now(),
  expires_at timestamptz not null,
  status text not null default 'active' check (status in ('active','expired','exhausted','revoked')),
  created_at timestamptz not null default now()
);

create table if not exists public.windows_activation_attempts (
  id uuid primary key default gen_random_uuid(),
  device_id uuid,
  attempt_hash text,
  succeeded boolean not null default false,
  created_at timestamptz not null default now()
);

create table if not exists public.windows_usage_sessions (
  device_id uuid primary key references public.windows_devices(id) on delete cascade,
  session_id uuid not null,
  last_sequence bigint not null default 0 check (last_sequence >= 0),
  last_rx_bytes bigint not null default 0 check (last_rx_bytes >= 0),
  last_tx_bytes bigint not null default 0 check (last_tx_bytes >= 0),
  bytes_accounted bigint not null default 0 check (bytes_accounted >= 0),
  started_at timestamptz not null default now(),
  last_report_at timestamptz
);

create table if not exists public.windows_app_releases (
  id uuid primary key default gen_random_uuid(),
  version_name text not null,
  version_code bigint not null unique check (version_code > 0),
  architecture text not null default 'x64' check (architecture in ('x64','arm64')),
  release_channel text not null default 'beta' check (release_channel in ('beta','stable')),
  installer_url text not null,
  sha256 text not null check (sha256 ~ '^[A-Fa-f0-9]{64}$'),
  file_size bigint check (file_size is null or file_size > 0),
  release_notes text not null default '',
  is_active boolean not null default true,
  is_mandatory boolean not null default false,
  minimum_supported_version text,
  published_at timestamptz not null default now(),
  created_at timestamptz not null default now()
);

create table if not exists public.windows_purchase_plans (
  plan_key text primary key,
  title text not null check (length(btrim(title)) between 1 and 100),
  plan_kind text not null check (plan_kind in ('fixed','custom')),
  plan_bytes bigint,
  duration_days integer,
  amount_toman bigint,
  is_active boolean not null default true,
  display_order integer not null default 0,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint windows_purchase_plans_values_check check (
    (plan_kind='fixed' and plan_bytes > 0 and duration_days between 1 and 3650 and amount_toman >= 1000)
    or
    (plan_kind='custom' and plan_bytes is null and duration_days is null and amount_toman is null)
  )
);

create table if not exists public.windows_server_entries (
  id uuid primary key default gen_random_uuid(),
  title text not null check (length(btrim(title)) between 1 and 120),
  entry_type text not null default 'text' check (entry_type = 'text'),
  config_text text not null check (length(btrim(config_text)) between 1 and 262144),
  is_active boolean not null default true,
  priority integer not null default 0,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table if not exists public.windows_customers (
  telegram_user_id bigint primary key,
  telegram_chat_id bigint not null,
  telegram_username text,
  phone text check (phone is null or phone ~ '^09[0-9]{9}$'),
  first_seen_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  is_blocked boolean not null default false
);

create table if not exists public.windows_bot_platform_sessions (
  telegram_user_id bigint primary key,
  telegram_chat_id bigint not null,
  platform text check (platform is null or platform in ('mobile','windows')),
  selected_at timestamptz,
  updated_at timestamptz not null default now()
);

create table if not exists public.windows_bot_user_states (
  telegram_user_id bigint primary key,
  telegram_chat_id bigint not null,
  state text not null default 'idle',
  data jsonb not null default '{}'::jsonb,
  updated_at timestamptz not null default now()
);

create table if not exists public.windows_telegram_admins (
  telegram_user_id bigint primary key,
  display_name text,
  enabled boolean not null default true,
  created_at timestamptz not null default now()
);

create table if not exists public.windows_telegram_updates (
  update_id bigint primary key,
  received_at timestamptz not null default now(),
  processed_at timestamptz,
  status text not null default 'received' check (status in ('received','processed','failed')),
  error_code text
);

create table if not exists public.windows_support_threads (
  id uuid primary key default gen_random_uuid(),
  telegram_user_id bigint not null,
  telegram_chat_id bigint not null,
  telegram_username text,
  status text not null default 'open' check (status in ('open','closed')),
  opened_at timestamptz not null default now(),
  last_message_at timestamptz not null default now(),
  closed_at timestamptz,
  closed_by bigint,
  close_reason text
);

create table if not exists public.windows_support_messages (
  id uuid primary key default gen_random_uuid(),
  thread_id uuid not null references public.windows_support_threads(id) on delete cascade,
  sender_kind text not null check (sender_kind in ('user','admin','system')),
  sender_telegram_id bigint,
  message_type text not null default 'text'
    check (message_type in ('text','photo','document','video','voice','other')),
  message_text text,
  telegram_file_id text,
  telegram_message_id bigint,
  created_at timestamptz not null default now()
);

do $$
begin
  if not exists (
    select 1 from pg_constraint where conname='windows_purchase_orders_activation_code_id_fkey'
  ) then
    alter table public.windows_purchase_orders
      add constraint windows_purchase_orders_activation_code_id_fkey
      foreign key (activation_code_id) references public.windows_activation_codes(id) on delete set null;
  end if;
end $$;

create index if not exists windows_activation_attempts_device_time_idx
  on public.windows_activation_attempts(device_id,created_at desc);
create index if not exists windows_activation_attempts_hash_created_idx
  on public.windows_activation_attempts(attempt_hash,created_at desc);
create index if not exists windows_activation_codes_user_idx
  on public.windows_activation_codes(telegram_user_id,created_at desc);
create unique index if not exists windows_devices_activation_code_uidx
  on public.windows_devices(activation_code_id) where activation_code_id is not null;
create unique index if not exists windows_devices_device_token_hash_uidx
  on public.windows_devices(device_token_hash) where device_token_hash is not null;
create index if not exists windows_purchase_orders_status_created_idx
  on public.windows_purchase_orders(status,created_at desc);
create index if not exists windows_purchase_orders_user_created_idx
  on public.windows_purchase_orders(telegram_user_id,created_at desc);
create index if not exists windows_purchase_orders_activation_code_id_idx
  on public.windows_purchase_orders(activation_code_id) where activation_code_id is not null;
create index if not exists windows_subscriptions_device_status_idx
  on public.windows_subscriptions(device_id,status,expires_at desc);
create index if not exists windows_server_entries_active_priority_idx
  on public.windows_server_entries(is_active,priority desc,created_at desc);
create index if not exists windows_support_threads_status_idx
  on public.windows_support_threads(status,last_message_at desc);
create index if not exists windows_support_messages_thread_created_idx
  on public.windows_support_messages(thread_id,created_at desc);
create index if not exists windows_telegram_updates_received_idx
  on public.windows_telegram_updates(received_at desc);

do $$
declare t text;
begin
  foreach t in array array[
    'windows_activation_attempts','windows_activation_codes','windows_app_releases',
    'windows_bot_platform_sessions','windows_bot_user_states','windows_customers',
    'windows_devices','windows_purchase_orders','windows_purchase_plans',
    'windows_server_entries','windows_subscriptions','windows_support_messages',
    'windows_support_threads','windows_telegram_admins','windows_telegram_updates',
    'windows_usage_sessions'
  ] loop
    execute format('alter table public.%I enable row level security',t);
    execute format('revoke all on table public.%I from anon, authenticated',t);
    execute format('grant all on table public.%I to service_role',t);
  end loop;
end $$;

insert into public.windows_purchase_plans
(plan_key,title,plan_kind,plan_bytes,duration_days,amount_toman,is_active,display_order)
values
('fixed_10gb_11d','۱۰ گیگابایت · ۱۱ روز','fixed',10737418240,11,58000,true,1),
('custom','حجم و مدت دلخواه','custom',null,null,null,true,2)
on conflict (plan_key) do nothing;

insert into storage.buckets(id,name,public,file_size_limit,allowed_mime_types)
values('windows-releases','windows-releases',true,1073741824,
  array['application/octet-stream','application/x-msdownload'])
on conflict (id) do nothing;

drop function if exists public.windows_claim_activation(text,uuid);

create or replace function public.windows_claim_activation(
  p_code_hash text,
  p_machine_fingerprint_hash text,
  p_device_token_hash text
)
returns table (
  ok boolean,
  error_code text,
  device_id uuid,
  subscription_id uuid,
  telegram_user_id bigint,
  granted_bytes bigint,
  expires_at timestamptz
)
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
  v_code public.windows_activation_codes%rowtype;
  v_customer public.windows_customers%rowtype;
  v_device_id uuid;
  v_subscription_id uuid;
  v_expires timestamptz;
  v_role text;
begin
  v_role := current_setting('request.jwt.claim.role', true);
  if v_role is distinct from 'service_role' then
    raise exception 'not_authorized';
  end if;

  if p_code_hash !~ '^[a-f0-9]{64}$'
     or p_machine_fingerprint_hash !~ '^[a-f0-9]{64}$'
     or p_device_token_hash !~ '^[a-f0-9]{64}$' then
    return query select false,'invalid_request',null::uuid,null::uuid,null::bigint,null::bigint,null::timestamptz;
    return;
  end if;

  if (select count(*) from public.windows_activation_attempts
      where attempt_hash=p_code_hash and created_at>now()-interval '30 minutes')>=12 then
    insert into public.windows_activation_attempts(attempt_hash,succeeded)
    values(p_code_hash,false);
    return query select false,'too_many_attempts',null::uuid,null::uuid,null::bigint,null::bigint,null::timestamptz;
    return;
  end if;

  select * into v_code from public.windows_activation_codes
  where code_hash=p_code_hash for update;

  if not found or v_code.status<>'issued' or v_code.expires_at<=now() then
    insert into public.windows_activation_attempts(attempt_hash,succeeded)
    values(p_code_hash,false);
    return query select false,'invalid_or_expired_code',null::uuid,null::uuid,null::bigint,null::bigint,null::timestamptz;
    return;
  end if;

  select * into v_customer from public.windows_customers
  where telegram_user_id=v_code.telegram_user_id for update;

  if found and v_customer.is_blocked then
    insert into public.windows_activation_attempts(attempt_hash,succeeded)
    values(p_code_hash,false);
    return query select false,'account_blocked',null::uuid,null::uuid,null::bigint,null::bigint,null::timestamptz;
    return;
  end if;

  v_device_id:=gen_random_uuid();
  v_subscription_id:=gen_random_uuid();
  v_expires:=now()+make_interval(days=>v_code.duration_days);

  insert into public.windows_devices(
    id,telegram_user_id,activation_code_id,machine_fingerprint_hash,
    device_token_hash,status,last_seen_at
  ) values (
    v_device_id,v_code.telegram_user_id,v_code.id,p_machine_fingerprint_hash,
    p_device_token_hash,'active',now()
  );

  update public.windows_activation_codes
  set status='claimed',claimed_device_id=v_device_id,claimed_at=now()
  where id=v_code.id;

  insert into public.windows_subscriptions(
    id,device_id,activation_code_id,telegram_user_id,granted_bytes,
    used_bytes,starts_at,expires_at,status
  ) values (
    v_subscription_id,v_device_id,v_code.id,v_code.telegram_user_id,
    v_code.granted_bytes,0,now(),v_expires,'active'
  );

  insert into public.windows_customers(
    telegram_user_id,telegram_chat_id,telegram_username,phone,updated_at
  ) values (
    v_code.telegram_user_id,v_code.telegram_user_id,v_code.telegram_username,v_code.phone,now()
  )
  on conflict(telegram_user_id) do update set
    telegram_username=excluded.telegram_username,phone=excluded.phone,updated_at=now();

  insert into public.windows_activation_attempts(device_id,attempt_hash,succeeded)
  values(v_device_id,p_code_hash,true);

  return query select true,null::text,v_device_id,v_subscription_id,
    v_code.telegram_user_id,v_code.granted_bytes,v_expires;
end;
$$;

create or replace function public.windows_report_usage(
  p_device_id uuid,
  p_session_id uuid,
  p_sequence bigint,
  p_rx_bytes bigint,
  p_tx_bytes bigint
)
returns table (
  ok boolean,
  error_code text,
  used_bytes bigint,
  remaining_bytes bigint,
  expires_at timestamptz
)
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
  v_role text;
  v_sub public.windows_subscriptions%rowtype;
  v_usage public.windows_usage_sessions%rowtype;
  v_delta bigint;
  v_new_used bigint;
  v_found boolean;
begin
  v_role:=current_setting('request.jwt.claim.role',true);
  if v_role is distinct from 'service_role' then
    raise exception 'not_authorized';
  end if;

  if p_device_id is null or p_session_id is null
     or p_sequence<0 or p_rx_bytes<0 or p_tx_bytes<0 then
    return query select false,'invalid_usage',null::bigint,null::bigint,null::timestamptz;
    return;
  end if;

  select * into v_sub from public.windows_subscriptions
  where device_id=p_device_id and status='active'
  order by starts_at desc limit 1 for update;

  if not found or v_sub.expires_at<=now() then
    return query select false,'subscription_inactive',null::bigint,null::bigint,v_sub.expires_at;
    return;
  end if;

  if v_sub.used_bytes>=v_sub.granted_bytes then
    update public.windows_subscriptions set status='exhausted' where id=v_sub.id;
    return query select false,'quota_exhausted',v_sub.used_bytes,0::bigint,v_sub.expires_at;
    return;
  end if;

  select * into v_usage from public.windows_usage_sessions
  where device_id=p_device_id for update;
  v_found:=found;

  if not v_found or v_usage.session_id<>p_session_id then
    v_delta:=least(p_rx_bytes+p_tx_bytes,v_sub.granted_bytes-v_sub.used_bytes);
    insert into public.windows_usage_sessions(
      device_id,session_id,last_sequence,last_rx_bytes,last_tx_bytes,bytes_accounted,started_at,last_report_at
    ) values (
      p_device_id,p_session_id,p_sequence,p_rx_bytes,p_tx_bytes,v_delta,now(),now()
    )
    on conflict(device_id) do update set
      session_id=excluded.session_id,last_sequence=excluded.last_sequence,
      last_rx_bytes=excluded.last_rx_bytes,last_tx_bytes=excluded.last_tx_bytes,
      bytes_accounted=excluded.bytes_accounted,started_at=now(),last_report_at=now();
  else
    if p_sequence<=v_usage.last_sequence or p_rx_bytes<v_usage.last_rx_bytes or p_tx_bytes<v_usage.last_tx_bytes then
      return query select false,'stale_or_non_monotonic_usage',
        v_sub.used_bytes,greatest(v_sub.granted_bytes-v_sub.used_bytes,0),v_sub.expires_at;
      return;
    end if;
    v_delta:=(p_rx_bytes-v_usage.last_rx_bytes)+(p_tx_bytes-v_usage.last_tx_bytes);
    insert into public.windows_usage_sessions(
      device_id,session_id,last_sequence,last_rx_bytes,last_tx_bytes,bytes_accounted,started_at,last_report_at
    ) values (
      p_device_id,p_session_id,p_sequence,p_rx_bytes,p_tx_bytes,v_delta,v_usage.started_at,now()
    )
    on conflict(device_id) do update set
      session_id=excluded.session_id,last_sequence=excluded.last_sequence,
      last_rx_bytes=excluded.last_rx_bytes,last_tx_bytes=excluded.last_tx_bytes,
      bytes_accounted=public.windows_usage_sessions.bytes_accounted+excluded.bytes_accounted,
      last_report_at=now();
  end if;

  v_new_used:=least(v_sub.granted_bytes,v_sub.used_bytes+greatest(v_delta,0));
  update public.windows_subscriptions set
    used_bytes=v_new_used,
    status=case when v_new_used>=granted_bytes then 'exhausted' else 'active' end
  where id=v_sub.id;

  return query select true,null::text,v_new_used,greatest(v_sub.granted_bytes-v_new_used,0),v_sub.expires_at;
end;
$$;

revoke all on function public.windows_claim_activation(text,text,text) from public,anon,authenticated;
grant execute on function public.windows_claim_activation(text,text,text) to service_role;
revoke all on function public.windows_report_usage(uuid,uuid,bigint,bigint,bigint) from public,anon,authenticated;
grant execute on function public.windows_report_usage(uuid,uuid,bigint,bigint,bigint) to service_role;
