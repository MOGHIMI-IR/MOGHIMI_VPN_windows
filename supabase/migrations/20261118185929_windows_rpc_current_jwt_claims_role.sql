CREATE OR REPLACE FUNCTION public.windows_claim_activation(p_code_hash text, p_machine_fingerprint_hash text, p_device_token_hash text)
 RETURNS TABLE(ok boolean, error_code text, device_id uuid, subscription_id uuid, telegram_user_id bigint, granted_bytes bigint, expires_at timestamp with time zone)
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public', 'pg_temp'
AS $function$
declare
  v_code public.windows_activation_codes%rowtype;
  v_customer public.windows_customers%rowtype;
  v_device_id uuid;
  v_subscription_id uuid;
  v_expires timestamptz;
  v_role text;
  v_machine_attempts integer;
  v_code_attempts integer;
begin
  v_role := nullif(current_setting('request.jwt.claim.role', true), '');
  if v_role is null then
    begin
      v_role := nullif(
        (nullif(current_setting('request.jwt.claims', true), '')::jsonb)->>'role',
        ''
      );
    exception when others then
      v_role := null;
    end;
  end if;
  if v_role is distinct from 'service_role' then
    raise exception 'not_authorized';
  end if;

  if p_code_hash !~ '^[a-f0-9]{64}$'
     or p_machine_fingerprint_hash !~ '^[a-f0-9]{64}$'
     or p_device_token_hash !~ '^[a-f0-9]{64}$' then
    return query select false,'invalid_request',null::uuid,null::uuid,null::bigint,null::bigint,null::timestamptz;
    return;
  end if;

  -- Serialize activation attempts for this installation so parallel requests
  -- cannot race past the per-installation limit.
  perform pg_advisory_xact_lock(hashtext(p_machine_fingerprint_hash));

  select count(*) into v_machine_attempts
  from public.windows_activation_attempts
  where machine_fingerprint_hash=p_machine_fingerprint_hash
    and created_at>now()-interval '30 minutes';

  if v_machine_attempts>=15 then
    insert into public.windows_activation_attempts(attempt_hash,machine_fingerprint_hash,succeeded)
    values(p_code_hash,p_machine_fingerprint_hash,false);
    return query select false,'too_many_attempts',null::uuid,null::uuid,null::bigint,null::bigint,null::timestamptz;
    return;
  end if;

  select count(*) into v_code_attempts
  from public.windows_activation_attempts
  where attempt_hash=p_code_hash and created_at>now()-interval '30 minutes';

  if v_code_attempts>=12 then
    insert into public.windows_activation_attempts(attempt_hash,machine_fingerprint_hash,succeeded)
    values(p_code_hash,p_machine_fingerprint_hash,false);
    return query select false,'too_many_attempts',null::uuid,null::uuid,null::bigint,null::bigint,null::timestamptz;
    return;
  end if;

  select * into v_code
  from public.windows_activation_codes
  where code_hash=p_code_hash
  for update;

  if not found or v_code.status<>'issued' or v_code.expires_at<=now() then
    insert into public.windows_activation_attempts(attempt_hash,machine_fingerprint_hash,succeeded)
    values(p_code_hash,p_machine_fingerprint_hash,false);
    return query select false,'invalid_or_expired_code',null::uuid,null::uuid,null::bigint,null::bigint,null::timestamptz;
    return;
  end if;

  select * into v_customer
  from public.windows_customers
  where telegram_user_id=v_code.telegram_user_id
  for update;

  if found and v_customer.is_blocked then
    insert into public.windows_activation_attempts(attempt_hash,machine_fingerprint_hash,succeeded)
    values(p_code_hash,p_machine_fingerprint_hash,false);
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
    v_code.telegram_user_id,v_code.telegram_user_id,v_code.telegram_username,
    v_code.phone,now()
  )
  on conflict (telegram_user_id) do update set
    telegram_username=excluded.telegram_username,
    phone=excluded.phone,
    updated_at=now();

  insert into public.windows_activation_attempts(
    device_id,attempt_hash,machine_fingerprint_hash,succeeded
  ) values (
    v_device_id,p_code_hash,p_machine_fingerprint_hash,true
  );

  return query select true,null::text,v_device_id,v_subscription_id,
    v_code.telegram_user_id,v_code.granted_bytes,v_expires;
end;
$function$
;

CREATE OR REPLACE FUNCTION public.windows_claim_telegram_update(p_update_id bigint, p_stale_seconds integer DEFAULT 120)
 RETURNS TABLE(claimed boolean)
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public', 'pg_temp'
AS $function$
declare
  v_role text;
  v_claimed boolean := false;
  v_stale integer := least(greatest(coalesce(p_stale_seconds,120),30),900);
begin
  v_role := nullif(current_setting('request.jwt.claim.role', true), '');
  if v_role is null then
    begin
      v_role := nullif(
        (nullif(current_setting('request.jwt.claims', true), '')::jsonb)->>'role',
        ''
      );
    exception when others then
      v_role := null;
    end;
  end if;
  if v_role is distinct from 'service_role' then
    raise exception 'not_authorized';
  end if;

  if p_update_id is null or p_update_id < 0 then
    return query select false;
    return;
  end if;

  insert into public.windows_telegram_updates(update_id,received_at,processed_at,status,error_code)
  values(p_update_id,now(),null,'received',null)
  on conflict(update_id) do update set
    received_at=now(),
    processed_at=null,
    status='received',
    error_code=null
  where public.windows_telegram_updates.status='failed'
     or (
       public.windows_telegram_updates.status='received'
       and public.windows_telegram_updates.received_at < now() - make_interval(secs => v_stale)
     )
  returning true into v_claimed;

  return query select coalesce(v_claimed,false);
end;
$function$
;

CREATE OR REPLACE FUNCTION public.windows_report_usage(p_device_id uuid, p_session_id uuid, p_sequence bigint, p_rx_bytes bigint, p_tx_bytes bigint)
 RETURNS TABLE(ok boolean, error_code text, used_bytes bigint, remaining_bytes bigint, expires_at timestamp with time zone)
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public', 'pg_temp'
AS $function$
declare
  v_role text;
  v_sub public.windows_subscriptions%rowtype;
  v_usage public.windows_usage_sessions%rowtype;
  v_delta bigint;
  v_new_used bigint;
  v_found boolean;
begin
  v_role := nullif(current_setting('request.jwt.claim.role', true), '');
  if v_role is null then
    begin
      v_role := nullif(
        (nullif(current_setting('request.jwt.claims', true), '')::jsonb)->>'role',
        ''
      );
    exception when others then
      v_role := null;
    end;
  end if;
  if v_role is distinct from 'service_role' then
    raise exception 'not_authorized';
  end if;

  if p_device_id is null or p_session_id is null
     or p_sequence < 0 or p_rx_bytes < 0 or p_tx_bytes < 0 then
    return query select false,'invalid_usage',null::bigint,null::bigint,null::timestamptz;
    return;
  end if;

  select * into v_sub
  from public.windows_subscriptions
  where device_id=p_device_id and status='active'
  order by starts_at desc
  limit 1
  for update;

  if not found or v_sub.expires_at <= now() then
    return query select false,'subscription_inactive',null::bigint,null::bigint,v_sub.expires_at;
    return;
  end if;

  if v_sub.used_bytes >= v_sub.granted_bytes then
    update public.windows_subscriptions set status='exhausted' where id=v_sub.id;
    return query select false,'quota_exhausted',v_sub.used_bytes,0::bigint,v_sub.expires_at;
    return;
  end if;

  select * into v_usage
  from public.windows_usage_sessions
  where device_id=p_device_id
  for update;
  v_found := found;

  if not v_found or v_usage.session_id <> p_session_id then
    v_delta := least(p_rx_bytes + p_tx_bytes, v_sub.granted_bytes - v_sub.used_bytes);
    insert into public.windows_usage_sessions(
      device_id,session_id,last_sequence,last_rx_bytes,last_tx_bytes,
      bytes_accounted,started_at,last_report_at
    ) values (
      p_device_id,p_session_id,p_sequence,p_rx_bytes,p_tx_bytes,
      v_delta,now(),now()
    )
    on conflict (device_id) do update set
      session_id=excluded.session_id,
      last_sequence=excluded.last_sequence,
      last_rx_bytes=excluded.last_rx_bytes,
      last_tx_bytes=excluded.last_tx_bytes,
      bytes_accounted=excluded.bytes_accounted,
      started_at=now(),
      last_report_at=now();
  else
    if p_sequence <= v_usage.last_sequence
       or p_rx_bytes < v_usage.last_rx_bytes
       or p_tx_bytes < v_usage.last_tx_bytes then
      return query select false,'stale_or_non_monotonic_usage',
        v_sub.used_bytes,greatest(v_sub.granted_bytes-v_sub.used_bytes,0),v_sub.expires_at;
      return;
    end if;
    v_delta := (p_rx_bytes - v_usage.last_rx_bytes) + (p_tx_bytes - v_usage.last_tx_bytes);
    insert into public.windows_usage_sessions(
      device_id,session_id,last_sequence,last_rx_bytes,last_tx_bytes,
      bytes_accounted,started_at,last_report_at
    ) values (
      p_device_id,p_session_id,p_sequence,p_rx_bytes,p_tx_bytes,
      v_delta,v_usage.started_at,now()
    )
    on conflict (device_id) do update set
      session_id=excluded.session_id,
      last_sequence=excluded.last_sequence,
      last_rx_bytes=excluded.last_rx_bytes,
      last_tx_bytes=excluded.last_tx_bytes,
      bytes_accounted=public.windows_usage_sessions.bytes_accounted+excluded.bytes_accounted,
      last_report_at=now();
  end if;

  v_new_used := least(v_sub.granted_bytes,v_sub.used_bytes+greatest(v_delta,0));
  update public.windows_subscriptions
  set used_bytes=v_new_used,
      status=case when v_new_used>=granted_bytes then 'exhausted' else 'active' end
  where id=v_sub.id;

  return query select true,null::text,v_new_used,
    greatest(v_sub.granted_bytes-v_new_used,0),v_sub.expires_at;
end;
$function$
;