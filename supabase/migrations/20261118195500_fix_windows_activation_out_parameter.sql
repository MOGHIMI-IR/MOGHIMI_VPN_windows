-- Fix ambiguous PL/pgSQL OUT parameter in the activation lookup.
CREATE OR REPLACE FUNCTION public.windows_claim_activation(
  p_code_hash text,
  p_machine_fingerprint_hash text,
  p_device_token_hash text
)
RETURNS TABLE(ok boolean,error_code text,device_id uuid,subscription_id uuid,telegram_user_id bigint,granted_bytes bigint,expires_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp
AS $function$
DECLARE
  v_code public.windows_activation_codes%rowtype;
  v_customer public.windows_customers%rowtype;
  v_device_id uuid;
  v_subscription_id uuid;
  v_expires timestamptz;
  v_role text;
  v_machine_attempts integer;
  v_code_attempts integer;
BEGIN
  v_role:=nullif(current_setting('request.jwt.claim.role',true),'');
  IF v_role IS NULL THEN
    BEGIN
      v_role:=nullif((nullif(current_setting('request.jwt.claims',true),'')::jsonb)->>'role','');
    EXCEPTION WHEN others THEN v_role:=NULL;
    END;
  END IF;
  IF v_role IS DISTINCT FROM 'service_role' THEN RAISE EXCEPTION 'not_authorized'; END IF;
  IF p_code_hash !~ '^[a-f0-9]{64}$' OR p_machine_fingerprint_hash !~ '^[a-f0-9]{64}$' OR p_device_token_hash !~ '^[a-f0-9]{64}$' THEN
    RETURN QUERY SELECT false,'invalid_request',NULL::uuid,NULL::uuid,NULL::bigint,NULL::bigint,NULL::timestamptz; RETURN;
  END IF;
  PERFORM pg_advisory_xact_lock(hashtext(p_machine_fingerprint_hash));
  SELECT count(*) INTO v_machine_attempts FROM public.windows_activation_attempts AS waa
    WHERE waa.machine_fingerprint_hash=p_machine_fingerprint_hash AND waa.created_at>now()-interval '30 minutes';
  IF v_machine_attempts>=15 THEN
    INSERT INTO public.windows_activation_attempts(attempt_hash,machine_fingerprint_hash,succeeded) VALUES(p_code_hash,p_machine_fingerprint_hash,false);
    RETURN QUERY SELECT false,'too_many_attempts',NULL::uuid,NULL::uuid,NULL::bigint,NULL::bigint,NULL::timestamptz; RETURN;
  END IF;
  SELECT count(*) INTO v_code_attempts FROM public.windows_activation_attempts AS waa
    WHERE waa.attempt_hash=p_code_hash AND waa.created_at>now()-interval '30 minutes';
  IF v_code_attempts>=12 THEN
    INSERT INTO public.windows_activation_attempts(attempt_hash,machine_fingerprint_hash,succeeded) VALUES(p_code_hash,p_machine_fingerprint_hash,false);
    RETURN QUERY SELECT false,'too_many_attempts',NULL::uuid,NULL::uuid,NULL::bigint,NULL::bigint,NULL::timestamptz; RETURN;
  END IF;
  SELECT ac.* INTO v_code FROM public.windows_activation_codes AS ac WHERE ac.code_hash=p_code_hash FOR UPDATE;
  IF NOT FOUND OR v_code.status<>'issued' OR v_code.expires_at<=now() THEN
    INSERT INTO public.windows_activation_attempts(attempt_hash,machine_fingerprint_hash,succeeded) VALUES(p_code_hash,p_machine_fingerprint_hash,false);
    RETURN QUERY SELECT false,'invalid_or_expired_code',NULL::uuid,NULL::uuid,NULL::bigint,NULL::bigint,NULL::timestamptz; RETURN;
  END IF;
  SELECT wc.* INTO v_customer FROM public.windows_customers AS wc
    WHERE wc.telegram_user_id=v_code.telegram_user_id FOR UPDATE;
  IF FOUND AND v_customer.is_blocked THEN
    INSERT INTO public.windows_activation_attempts(attempt_hash,machine_fingerprint_hash,succeeded) VALUES(p_code_hash,p_machine_fingerprint_hash,false);
    RETURN QUERY SELECT false,'account_blocked',NULL::uuid,NULL::uuid,NULL::bigint,NULL::bigint,NULL::timestamptz; RETURN;
  END IF;
  v_device_id:=gen_random_uuid(); v_subscription_id:=gen_random_uuid(); v_expires:=now()+make_interval(days=>v_code.duration_days);
  INSERT INTO public.windows_devices(id,telegram_user_id,activation_code_id,machine_fingerprint_hash,device_token_hash,status,last_seen_at)
    VALUES(v_device_id,v_code.telegram_user_id,v_code.id,p_machine_fingerprint_hash,p_device_token_hash,'active',now());
  UPDATE public.windows_activation_codes AS ac SET status='claimed',claimed_device_id=v_device_id,claimed_at=now() WHERE ac.id=v_code.id;
  INSERT INTO public.windows_subscriptions(id,device_id,activation_code_id,telegram_user_id,granted_bytes,used_bytes,starts_at,expires_at,status)
    VALUES(v_subscription_id,v_device_id,v_code.id,v_code.telegram_user_id,v_code.granted_bytes,0,now(),v_expires,'active');
  INSERT INTO public.windows_customers(telegram_user_id,telegram_chat_id,telegram_username,phone,updated_at)
    VALUES(v_code.telegram_user_id,v_code.telegram_user_id,v_code.telegram_username,v_code.phone,now())
    ON CONFLICT(telegram_user_id) DO UPDATE SET telegram_username=EXCLUDED.telegram_username,phone=EXCLUDED.phone,updated_at=now();
  INSERT INTO public.windows_activation_attempts(device_id,attempt_hash,machine_fingerprint_hash,succeeded)
    VALUES(v_device_id,p_code_hash,p_machine_fingerprint_hash,true);
  RETURN QUERY SELECT true,NULL::text,v_device_id,v_subscription_id,v_code.telegram_user_id,v_code.granted_bytes,v_expires;
END;
$function$;
REVOKE ALL ON FUNCTION public.windows_claim_activation(text,text,text) FROM public,anon,authenticated;
GRANT EXECUTE ON FUNCTION public.windows_claim_activation(text,text,text) TO service_role;
