-- Keep Windows free allowance aligned with the app and live Supabase project.
-- Idempotent: safe on fresh databases where the preceding migration already uses 8 GiB,
-- and on databases where the original 7 GiB function definitions were already applied.
DO $migration$
DECLARE
  v_definition text;
BEGIN
  v_definition := pg_get_functiondef(
    'public.windows_free_device_status(text)'::regprocedure
  );
  IF position('7516192768' IN v_definition) > 0 THEN
    EXECUTE replace(v_definition, '7516192768', '8589934592');
  ELSIF position('8589934592' IN v_definition) = 0 THEN
    RAISE EXCEPTION 'Unexpected Windows quota in windows_free_device_status';
  END IF;

  v_definition := pg_get_functiondef(
    'public.windows_report_free_usage(text,uuid,bigint,bigint,bigint)'::regprocedure
  );
  IF position('7516192768' IN v_definition) > 0 THEN
    EXECUTE replace(v_definition, '7516192768', '8589934592');
  ELSIF position('8589934592' IN v_definition) = 0 THEN
    RAISE EXCEPTION 'Unexpected Windows quota in windows_report_free_usage';
  END IF;
END;
$migration$;
