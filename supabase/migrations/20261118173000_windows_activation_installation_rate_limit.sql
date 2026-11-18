alter table public.windows_activation_attempts
  add column if not exists machine_fingerprint_hash text;

create index if not exists windows_activation_attempts_machine_time_idx
  on public.windows_activation_attempts(machine_fingerprint_hash,created_at desc);

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
  v_machine_attempts integer;
  v_code_attempts integer;
begin
  v_role := current_setting('request.jwt.claim.role', true);
  if v_role is distinct from 'service_role' then raise exception 'not_authorized'; end if;

  if p_code_hash !~ '^[a-f0-9]{64}$'
     or p_machine_fingerprint_hash !~ '^[a-f0-9]{64}$'
     or p_device_token_hash !~ '^[a-f0-9]{64}$' then
    return query select false,'invalid_request',null::uuid,null::uuid,null::bigint,null::bigint,null::timestamptz;
    return;
  end if;

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

  select * into v_code from public.windows_activation_codes where code_hash=p_code_hash for update;
  if not found or v_code.status<>'issued' or v_code.expires_at<=now() then
    insert into public.windows_activation_attempts(attempt_hash,machine_fingerprint_hash,succeeded)
    values(p_code_hash,p_machine_fingerprint_hash,false);
    return query select false,'invalid_or_expired_code',null::uuid,null::uuid,null::bigint,null::bigint,null::timestamptz;
    return;
  end if;

  select * into v_customer from public.windows_customers
  where telegram_user_id=v_code.telegram_user_id for update;
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
    id,telegram_user_id,activation_code_id,machine_fingerprint_hash,device_token_hash,status,last_seen_at
  ) values (
    v_device_id,v_code.telegram_user_id,v_code.id,p_machine_fingerprint_hash,p_device_token_hash,'active',now()
  );

  update public.windows_activation_codes
  set status='claimed',claimed_device_id=v_device_id,claimed_at=now()
  where id=v_code.id;

  insert into public.windows_subscriptions(
    id,device_id,activation_code_id,telegram_user_id,granted_bytes,used_bytes,starts_at,expires_at,status
  ) values (
    v_subscription_id,v_device_id,v_code.id,v_code.telegram_user_id,v_code.granted_bytes,0,now(),v_expires,'active'
  );

  insert into public.windows_customers(
    telegram_user_id,telegram_chat_id,telegram_username,phone,updated_at
  ) values (
    v_code.telegram_user_id,v_code.telegram_user_id,v_code.telegram_username,v_code.phone,now()
  )
  on conflict (telegram_user_id) do update set
    telegram_username=excluded.telegram_username,phone=excluded.phone,updated_at=now();

  insert into public.windows_activation_attempts(
    device_id,attempt_hash,machine_fingerprint_hash,succeeded
  ) values (
    v_device_id,p_code_hash,p_machine_fingerprint_hash,true
  );

  return query select true,null::text,v_device_id,v_subscription_id,
    v_code.telegram_user_id,v_code.granted_bytes,v_expires;
end;
$$;

revoke all on function public.windows_claim_activation(text,text,text) from public,anon,authenticated;
grant execute on function public.windows_claim_activation(text,text,text) to service_role;
