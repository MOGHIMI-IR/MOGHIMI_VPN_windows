# Moghimi Windows Supabase API

This folder contains the source of the deployed `windows-api` Edge Function and the
incremental SQL migration for the Windows-only monthly free allowance.

## Windows free allowance

- Independent from the Android project and Android's quota counter.
- 8 GiB (8,589,934,592 bytes) per Windows installation per UTC calendar month.
- The free allowance is consumed first; active Windows paid subscriptions are used next.
- The installation ID is hashed server-side and stored separately from the device token.
- The DPAPI-protected Windows identity file preserves the installation ID and device tokens.
- Usage reports use monotonically increasing per-session sequence numbers and cumulative
  upload/download counters, so retries after a lost response do not double-charge.

## Apply/deploy

The migration assumes the base Windows tables (including `windows_devices`,
`windows_subscriptions`, and `windows_customers`) have already been created.

Apply `migrations/20261119090000_windows_free_quota.sql` before deploying the Edge Function.
Deploy `functions/windows-api/index.ts` as `windows-api` with JWT verification disabled;
the function validates its device token and talks to Postgres using the service-role key,
which must remain an Edge Function secret and must never be embedded in the desktop client.

The deployed function source was copied into this repository from Supabase project
`hxfnoaeyjnkecegpppcy` after its free quota actions were verified.
