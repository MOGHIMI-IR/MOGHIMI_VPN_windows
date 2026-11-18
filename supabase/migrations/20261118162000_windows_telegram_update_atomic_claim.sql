create or replace function public.windows_claim_telegram_update(
  p_update_id bigint,
  p_stale_seconds integer default 120
)
returns table (claimed boolean)
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
  v_role text;
  v_claimed boolean := false;
  v_stale integer := least(greatest(coalesce(p_stale_seconds,120),30),900);
begin
  v_role := current_setting('request.jwt.claim.role', true);
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
$$;

revoke all on function public.windows_claim_telegram_update(bigint,integer) from public,anon,authenticated;
grant execute on function public.windows_claim_telegram_update(bigint,integer) to service_role;
