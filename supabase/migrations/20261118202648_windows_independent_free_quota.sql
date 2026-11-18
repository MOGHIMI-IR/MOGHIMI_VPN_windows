-- Independent Windows quota. Kept separate from Android devices and Android free usage.
-- The installation ID is DPAPI-persisted on Windows; the server stores only its hash and a token hash.
CREATE TABLE IF NOT EXISTS public.windows_free_devices (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  installation_id_hash text NOT NULL UNIQUE CHECK (installation_id_hash ~ '^[a-f0-9]{64}$'),
  device_token_hash text NOT NULL UNIQUE CHECK (device_token_hash ~ '^[a-f0-9]{64}$'),
  free_used_bytes bigint NOT NULL DEFAULT 0 CHECK (free_used_bytes >= 0),
  free_month_start date NOT NULL DEFAULT date_trunc('month', timezone('UTC', now()))::date,
  status text NOT NULL DEFAULT 'active' CHECK (status IN ('active','blocked')),
  created_at timestamptz NOT NULL DEFAULT now(),
  last_seen_at timestamptz
);
CREATE TABLE IF NOT EXISTS public.windows_free_usage_sessions (
  installation_id_hash text PRIMARY KEY
    REFERENCES public.windows_free_devices(installation_id_hash) ON DELETE CASCADE,
  session_id uuid NOT NULL,
  last_sequence bigint NOT NULL DEFAULT 0 CHECK (last_sequence >= 0),
  last_rx_bytes bigint NOT NULL DEFAULT 0 CHECK (last_rx_bytes >= 0),
  last_tx_bytes bigint NOT NULL DEFAULT 0 CHECK (last_tx_bytes >= 0),
  bytes_accounted bigint NOT NULL DEFAULT 0 CHECK (bytes_accounted >= 0),
  started_at timestamptz NOT NULL DEFAULT now(),
  last_report_at timestamptz
);

ALTER TABLE public.windows_free_devices ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.windows_free_usage_sessions ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON TABLE public.windows_free_devices FROM anon, authenticated;
REVOKE ALL ON TABLE public.windows_free_usage_sessions FROM anon, authenticated;
GRANT ALL ON TABLE public.windows_free_devices TO service_role;
GRANT ALL ON TABLE public.windows_free_usage_sessions TO service_role;

CREATE OR REPLACE FUNCTION public.windows_register_free_device(
  p_installation_id_hash text, p_device_token_hash text
)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp
AS $function$
DECLARE v_role text; v_id uuid; v_status text;
BEGIN
  v_role:=nullif(current_setting('request.jwt.claim.role',true),'');
  IF v_role IS NULL THEN
    BEGIN
      v_role:=nullif((nullif(current_setting('request.jwt.claims',true),'')::jsonb)->>'role','');
    EXCEPTION WHEN others THEN v_role:=NULL;
    END;
  END IF;
  IF v_role IS DISTINCT FROM 'service_role' THEN RAISE EXCEPTION 'not_authorized'; END IF;
  IF p_installation_id_hash !~ '^[a-f0-9]{64}$' OR p_device_token_hash !~ '^[a-f0-9]{64}$' THEN
    RETURN jsonb_build_object('ok',false,'code','invalid_device_credentials');
  END IF;
  INSERT INTO public.windows_free_devices(installation_id_hash,device_token_hash,last_seen_at)
    VALUES(p_installation_id_hash,p_device_token_hash,now())
    ON CONFLICT(installation_id_hash) DO UPDATE SET
      device_token_hash=EXCLUDED.device_token_hash,last_seen_at=now()
      WHERE public.windows_free_devices.status='active'
    RETURNING id,status INTO v_id,v_status;
  IF NOT FOUND THEN
    SELECT d.id,d.status INTO v_id,v_status FROM public.windows_free_devices AS d
    WHERE d.installation_id_hash=p_installation_id_hash;
  END IF;
  IF v_id IS NULL OR v_status IS DISTINCT FROM 'active' THEN
    RETURN jsonb_build_object('ok',false,'code','device_blocked');
  END IF;
  RETURN jsonb_build_object('ok',true,'device_id',v_id,'status','active');
END;
$function$;

CREATE OR REPLACE FUNCTION public.windows_free_device_status(p_installation_id_hash text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp
AS $function$
DECLARE
  v_device public.windows_free_devices%rowtype;
  v_month date:=date_trunc('month',timezone('UTC',now()))::date;
  v_limit bigint:=7516192768;
  v_free_remaining bigint;
  v_paid_remaining bigint:=0;
  v_paid_used bigint:=0;
  v_paid_granted bigint:=0;
  v_paid_expires timestamptz;
BEGIN
  SELECT d.* INTO v_device FROM public.windows_free_devices AS d
    WHERE d.installation_id_hash=p_installation_id_hash FOR UPDATE;
  IF NOT FOUND THEN RETURN jsonb_build_object('ok',false,'code','device_not_registered'); END IF;
  IF v_device.status<>'active' THEN RETURN jsonb_build_object('ok',false,'code','device_blocked'); END IF;
  IF v_device.free_month_start<>v_month THEN
    UPDATE public.windows_free_devices AS d SET free_month_start=v_month,free_used_bytes=0,last_seen_at=now()
      WHERE d.id=v_device.id RETURNING d.* INTO v_device;
  ELSE
    UPDATE public.windows_free_devices AS d SET last_seen_at=now() WHERE d.id=v_device.id;
  END IF;
  SELECT coalesce(sum(greatest(0,s.granted_bytes-s.used_bytes)),0)::bigint,
         coalesce(sum(least(s.used_bytes,s.granted_bytes)),0)::bigint,
         coalesce(sum(s.granted_bytes),0)::bigint,
         min(s.expires_at) FILTER (WHERE s.used_bytes<s.granted_bytes)
    INTO v_paid_remaining,v_paid_used,v_paid_granted,v_paid_expires
    FROM public.windows_subscriptions AS s
    JOIN public.windows_devices AS d ON d.id=s.device_id
    WHERE d.machine_fingerprint_hash=p_installation_id_hash AND d.status='active'
      AND s.status IN ('active','exhausted') AND s.starts_at<=now() AND s.expires_at>now();
  v_free_remaining:=greatest(0,v_limit-v_device.free_used_bytes);
  RETURN jsonb_build_object(
    'ok',true,'free_limit_bytes',v_limit,'free_used_bytes',v_device.free_used_bytes,
    'free_remaining_bytes',v_free_remaining,'free_month_start',v_device.free_month_start,
    'paid_granted_bytes',v_paid_granted,'paid_used_bytes',v_paid_used,
    'paid_remaining_bytes',v_paid_remaining,'paid_expires_at',v_paid_expires,
    'access_allowed',(v_free_remaining>0 OR v_paid_remaining>0),
    'status',CASE WHEN v_free_remaining>0 OR v_paid_remaining>0 THEN 'active' ELSE 'exhausted' END
  );
END;
$function$;

CREATE OR REPLACE FUNCTION public.windows_report_free_usage(
  p_installation_id_hash text,p_session_id uuid,p_sequence bigint,p_rx_total bigint,p_tx_total bigint
)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp
AS $function$
DECLARE
  v_device public.windows_free_devices%rowtype;
  v_usage public.windows_free_usage_sessions%rowtype;
  v_sub public.windows_subscriptions%rowtype;
  v_month date:=date_trunc('month',timezone('UTC',now()))::date;
  v_limit bigint:=7516192768;
  v_delta bigint;
  v_free_remaining bigint;
  v_free_delta bigint;
  v_paid_left bigint;
  v_paid_part bigint;
  v_paid_remaining bigint:=0;
  v_paid_used bigint:=0;
  v_paid_granted bigint:=0;
  v_paid_expires timestamptz;
BEGIN
  IF p_installation_id_hash !~ '^[a-f0-9]{64}$' OR p_session_id IS NULL
     OR p_sequence<1 OR p_rx_total<0 OR p_tx_total<0
     OR p_rx_total>9223372036854775807-p_tx_total THEN
    RETURN jsonb_build_object('ok',false,'code','invalid_usage_report');
  END IF;
  SELECT d.* INTO v_device FROM public.windows_free_devices AS d
    WHERE d.installation_id_hash=p_installation_id_hash FOR UPDATE;
  IF NOT FOUND THEN RETURN jsonb_build_object('ok',false,'code','device_not_registered'); END IF;
  IF v_device.status<>'active' THEN RETURN jsonb_build_object('ok',false,'code','device_blocked'); END IF;

  IF v_device.free_month_start<>v_month THEN
    UPDATE public.windows_free_devices AS d SET free_month_start=v_month,free_used_bytes=0
      WHERE d.id=v_device.id RETURNING d.* INTO v_device;
  END IF;

  SELECT u.* INTO v_usage FROM public.windows_free_usage_sessions AS u
    WHERE u.installation_id_hash=p_installation_id_hash FOR UPDATE;
  IF NOT FOUND THEN
    IF p_sequence<>1 THEN
      RETURN jsonb_build_object('ok',false,'code','sequence_gap','expected_sequence',1);
    END IF;
    INSERT INTO public.windows_free_usage_sessions(
      installation_id_hash,session_id,last_sequence,last_rx_bytes,last_tx_bytes,bytes_accounted,started_at,last_report_at
    ) VALUES(p_installation_id_hash,p_session_id,0,0,0,0,now(),now()) RETURNING * INTO v_usage;
  ELSIF v_usage.session_id<>p_session_id THEN
    IF p_sequence<>1 THEN RETURN jsonb_build_object('ok',false,'code','stale_session'); END IF;
    UPDATE public.windows_free_usage_sessions AS u
      SET session_id=p_session_id,last_sequence=0,last_rx_bytes=0,last_tx_bytes=0,
          bytes_accounted=0,started_at=now(),last_report_at=now()
      WHERE u.installation_id_hash=p_installation_id_hash RETURNING u.* INTO v_usage;
  END IF;

  IF p_sequence<=v_usage.last_sequence THEN
    RETURN public.windows_free_device_status(p_installation_id_hash)||jsonb_build_object('duplicate',true);
  END IF;
  IF p_sequence<>v_usage.last_sequence+1 THEN
    RETURN jsonb_build_object('ok',false,'code','sequence_gap','expected_sequence',v_usage.last_sequence+1);
  END IF;
  IF p_rx_total<v_usage.last_rx_bytes OR p_tx_total<v_usage.last_tx_bytes THEN
    RETURN jsonb_build_object('ok',false,'code','counter_reset');
  END IF;

  v_delta:=(p_rx_total-v_usage.last_rx_bytes)+(p_tx_total-v_usage.last_tx_bytes);
  IF v_delta>268435456 THEN
    RETURN jsonb_build_object('ok',false,'code','batch_too_large','max_delta_bytes',268435456);
  END IF;

  v_free_remaining:=greatest(0,v_limit-v_device.free_used_bytes);
  v_free_delta:=least(v_delta,v_free_remaining);
  v_paid_left:=v_delta-v_free_delta;

  UPDATE public.windows_free_devices AS d
    SET free_used_bytes=least(v_limit,d.free_used_bytes+v_free_delta),last_seen_at=now()
    WHERE d.id=v_device.id
    RETURNING greatest(0,v_limit-d.free_used_bytes) INTO v_free_remaining;

  IF v_paid_left>0 THEN
    FOR v_sub IN
      SELECT s.* FROM public.windows_subscriptions AS s
      JOIN public.windows_devices AS d ON d.id=s.device_id
      WHERE d.machine_fingerprint_hash=p_installation_id_hash AND d.status='active'
        AND s.status='active' AND s.starts_at<=now() AND s.expires_at>now()
        AND s.used_bytes<s.granted_bytes
      ORDER BY s.expires_at ASC,s.created_at ASC,s.id ASC FOR UPDATE OF s
    LOOP
      v_paid_part:=least(v_paid_left,greatest(0,v_sub.granted_bytes-v_sub.used_bytes));
      IF v_paid_part>0 THEN
        UPDATE public.windows_subscriptions AS s
          SET used_bytes=least(s.granted_bytes,s.used_bytes+v_paid_part),
              status=CASE WHEN s.used_bytes+v_paid_part>=s.granted_bytes THEN 'exhausted' ELSE 'active' END
          WHERE s.id=v_sub.id;
        v_paid_left:=v_paid_left-v_paid_part;
      END IF;
      EXIT WHEN v_paid_left<=0;
    END LOOP;
  END IF;

  UPDATE public.windows_free_usage_sessions AS u
    SET last_sequence=p_sequence,last_rx_bytes=p_rx_total,last_tx_bytes=p_tx_total,
        bytes_accounted=u.bytes_accounted+v_delta,last_report_at=now()
    WHERE u.installation_id_hash=p_installation_id_hash;

  SELECT coalesce(sum(greatest(0,s.granted_bytes-s.used_bytes)),0)::bigint,
         coalesce(sum(least(s.used_bytes,s.granted_bytes)),0)::bigint,
         coalesce(sum(s.granted_bytes),0)::bigint,
         min(s.expires_at) FILTER (WHERE s.used_bytes<s.granted_bytes)
    INTO v_paid_remaining,v_paid_used,v_paid_granted,v_paid_expires
    FROM public.windows_subscriptions AS s
    JOIN public.windows_devices AS d ON d.id=s.device_id
    WHERE d.machine_fingerprint_hash=p_installation_id_hash AND d.status='active'
      AND s.status IN ('active','exhausted') AND s.starts_at<=now() AND s.expires_at>now();

  RETURN jsonb_build_object(
    'ok',true,'accepted_bytes',v_delta,'free_limit_bytes',v_limit,
    'free_used_bytes',v_limit-v_free_remaining,'free_remaining_bytes',v_free_remaining,
    'free_month_start',v_month,'paid_granted_bytes',v_paid_granted,'paid_used_bytes',v_paid_used,
    'paid_remaining_bytes',v_paid_remaining,'paid_expires_at',v_paid_expires,
    'uncovered_bytes',greatest(0,v_paid_left),
    'access_allowed',(v_free_remaining>0 OR v_paid_remaining>0),
    'status',CASE WHEN v_free_remaining>0 OR v_paid_remaining>0 THEN 'active' ELSE 'exhausted' END,
    'server_time',now()
  );
END;
$function$;

REVOKE ALL ON FUNCTION public.windows_register_free_device(text,text) FROM PUBLIC,anon,authenticated;
GRANT EXECUTE ON FUNCTION public.windows_register_free_device(text,text) TO service_role;
REVOKE ALL ON FUNCTION public.windows_free_device_status(text) FROM PUBLIC,anon,authenticated;
GRANT EXECUTE ON FUNCTION public.windows_free_device_status(text) TO service_role;
REVOKE ALL ON FUNCTION public.windows_report_free_usage(text,uuid,bigint,bigint,bigint) FROM PUBLIC,anon,authenticated;
GRANT EXECUTE ON FUNCTION public.windows_report_free_usage(text,uuid,bigint,bigint,bigint) TO service_role;
