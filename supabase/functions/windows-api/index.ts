import "jsr:@supabase/functions-js/edge-runtime.d.ts";

const SUPABASE_URL = (Deno.env.get("SUPABASE_URL") ?? "").replace(/\/$/, "");
const SERVICE_KEY = (() => {
  const direct = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") ?? "";
  if (direct) return direct;
  try {
    const values = JSON.parse(Deno.env.get("SUPABASE_SECRET_KEYS") ?? "{}");
    return String(values.default ?? "");
  } catch {
    return "";
  }
})();

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), {
  status,
  headers: {
    "content-type": "application/json; charset=utf-8",
    "cache-control": "no-store, max-age=0",
    "access-control-allow-origin": "*",
    "access-control-allow-methods": "POST, OPTIONS",
    "access-control-allow-headers": "content-type, x-moghimi-device-token, apikey, authorization"
  }
});

function headers(prefer?: string): Headers {
  const result = new Headers({
    apikey: SERVICE_KEY,
    authorization: "Bearer " + SERVICE_KEY,
    "content-type": "application/json"
  });
  if (prefer) result.set("prefer", prefer);
  return result;
}

async function db(path: string, method = "GET", body?: unknown, prefer?: string) {
  const response = await fetch(SUPABASE_URL + "/rest/v1/" + path, {
    method,
    headers: headers(prefer),
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const text = await response.text();
  if (!response.ok) {
    console.error("windows_db_error", method, path.split("?")[0], response.status);
    throw new Error("database_unavailable");
  }
  return text ? JSON.parse(text) : null;
}

async function sha256(value: string): Promise<string> {
  const bytes = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return Array.from(new Uint8Array(bytes), b => b.toString(16).padStart(2, "0")).join("");
}

function randomHex(length: number): string {
  const bytes = crypto.getRandomValues(new Uint8Array(length));
  return Array.from(bytes, b => b.toString(16).padStart(2, "0")).join("");
}

function normalizeCode(input: unknown): string {
  return String(input ?? "").trim().toUpperCase().replace(/[^A-F0-9]/g, "");
}

function validInstallationId(input: unknown): string | null {
  const value = String(input ?? "").trim().toLowerCase();
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value) ? value : null;
}

async function getAuthenticatedDevice(request: Request) {
  const token = request.headers.get("x-moghimi-device-token") ?? "";
  if (!/^[a-f0-9]{64}$/i.test(token)) return null;

  const tokenHash = await sha256(token.toLowerCase());
  const devices = await db(
    "windows_devices?device_token_hash=eq." + tokenHash +
    "&status=eq.active&select=id,telegram_user_id,status"
  );
  if (!Array.isArray(devices) || devices.length !== 1) return null;

  const device = devices[0];
  const customers = await db(
    "windows_customers?telegram_user_id=eq." + device.telegram_user_id +
    "&select=is_blocked&limit=1"
  );
  if (!Array.isArray(customers) || customers.length !== 1 || customers[0].is_blocked === true) {
    return null;
  }

  const subscriptions = await db(
    "windows_subscriptions?device_id=eq." + device.id +
    "&select=id,granted_bytes,used_bytes,starts_at,expires_at,status" +
    "&order=starts_at.desc&limit=1"
  );
  const subscription = Array.isArray(subscriptions) ? subscriptions[0] : null;
  if (!subscription) return { device, subscription: null, active: false };

  const active = subscription.status === "active" &&
    Date.parse(subscription.starts_at) <= Date.now() &&
    Date.parse(subscription.expires_at) > Date.now() &&
    Number(subscription.used_bytes) < Number(subscription.granted_bytes);

  await db(
    "windows_devices?id=eq." + device.id,
    "PATCH",
    { last_seen_at: new Date().toISOString() },
    "return=minimal"
  );

  return { device, subscription, active };
}

async function handleActivate(body: Record<string, unknown>) {
  const code = normalizeCode(body.activation_code);
  const installationId = validInstallationId(body.installation_id);
  if (code.length !== 32 || !installationId) {
    return json({ ok: false, code: "invalid_input", message: "کد فعال‌سازی یا شناسه نصب معتبر نیست." }, 400);
  }

  const [codeHash, machineHash] = await Promise.all([
    sha256(code),
    sha256("MoghimiVPN-Windows-Install|" + installationId)
  ]);
  const deviceToken = randomHex(32);
  const tokenHash = await sha256(deviceToken);

  const result = await db("rpc/windows_claim_activation", "POST", {
    p_code_hash: codeHash,
    p_machine_fingerprint_hash: machineHash,
    p_device_token_hash: tokenHash
  });
  const row = Array.isArray(result) ? result[0] : result;
  if (!row?.ok) {
    const messages: Record<string, string> = {
      invalid_or_expired_code: "کد فعال‌سازی نامعتبر، منقضی یا قبلاً استفاده شده است.",
      too_many_attempts: "تعداد تلاش‌ها بیش از حد مجاز است؛ کمی بعد دوباره امتحان کن.",
      account_blocked: "دسترسی این حساب مسدود شده است.",
      invalid_request: "اطلاعات فعال‌سازی معتبر نیست."
    };
    return json({
      ok: false,
      code: row?.error_code ?? "activation_failed",
      message: messages[row?.error_code] ?? "فعال‌سازی انجام نشد."
    }, 400);
  }

  return json({
    ok: true,
    device_token: deviceToken,
    device_id: row.device_id,
    granted_bytes: Number(row.granted_bytes),
    expires_at: row.expires_at,
    message: "فعال‌سازی با موفقیت انجام شد."
  });
}

async function handleStatus(request: Request) {
  const auth = await getAuthenticatedDevice(request);
  if (!auth) return json({ ok: false, code: "device_not_registered" }, 401);
  if (!auth.subscription) return json({ ok: true, active: false, code: "no_subscription" });

  const sub = auth.subscription;
  const granted = Number(sub.granted_bytes);
  const used = Number(sub.used_bytes);
  const remaining = Math.max(0, granted - used);
  return json({
    ok: true,
    active: auth.active,
    device_id: auth.device.id,
    granted_bytes: granted,
    used_bytes: used,
    remaining_bytes: remaining,
    expires_at: sub.expires_at,
    status: auth.active ? "active" : (remaining <= 0 ? "exhausted" : "expired")
  });
}

async function handleProfiles(request: Request) {
  const auth = await getAuthenticatedDevice(request);
  if (!auth) return json({ ok: false, code: "device_not_registered" }, 401);
  if (!auth.active) return json({ ok: false, code: "subscription_inactive" }, 403);

  const rows = await db(
    "windows_server_entries?is_active=eq.true&select=id,title,config_text,priority&order=priority.desc,created_at.desc&limit=2000"
  );
  if (!Array.isArray(rows) || rows.length > 2000) {
    return json({ ok: false, code: "catalog_invalid" }, 502);
  }

  let aggregateBytes = 0;
  const entries = rows.map((entry: any) => {
    const config = typeof entry.config_text === "string" ? entry.config_text : "";
    aggregateBytes += new TextEncoder().encode(config).length;
    return {
      title: String(entry.title ?? "سرور Moghimi"),
      entry_type: "text",
      config_text: config
    };
  });
  if (aggregateBytes > 16 * 1024 * 1024) {
    return json({ ok: false, code: "catalog_too_large" }, 413);
  }

  return json({ ok: true, entries });
}

async function handleUsage(request: Request, body: Record<string, unknown>) {
  const auth = await getAuthenticatedDevice(request);
  if (!auth) return json({ ok: false, code: "device_not_registered" }, 401);

  const sessionId = String(body.session_id ?? "");
  const sequence = Number(body.sequence);
  const rx = Number(body.rx_bytes);
  const tx = Number(body.tx_bytes);
  if (!/^[0-9a-f-]{36}$/i.test(sessionId) ||
      !Number.isSafeInteger(sequence) || sequence < 0 ||
      !Number.isSafeInteger(rx) || rx < 0 ||
      !Number.isSafeInteger(tx) || tx < 0) {
    return json({ ok: false, code: "invalid_usage_report" }, 400);
  }

  const result = await db("rpc/windows_report_usage", "POST", {
    p_device_id: auth.device.id,
    p_session_id: sessionId,
    p_sequence: sequence,
    p_rx_bytes: rx,
    p_tx_bytes: tx
  });
  const row = Array.isArray(result) ? result[0] : result;
  return json({
    ok: Boolean(row?.ok),
    code: row?.error_code ?? null,
    used_bytes: row?.used_bytes === null ? null : Number(row?.used_bytes),
    remaining_bytes: row?.remaining_bytes === null ? null : Number(row?.remaining_bytes),
    expires_at: row?.expires_at ?? null
  }, row?.ok ? 200 : 409);
}

async function handleLatestRelease() {
  const rows = await db(
    "windows_app_releases?is_active=eq.true&architecture=eq.x64" +
    "&select=version_name,version_code,release_channel,installer_url,sha256,file_size,release_notes,is_mandatory,minimum_supported_version,published_at" +
    "&order=published_at.desc,version_code.desc&limit=1"
  );
  const release = Array.isArray(rows) ? rows[0] : null;
  if (!release) return json({ ok: true, available: false });
  if (!/^https:\/\//i.test(String(release.installer_url ?? "")) ||
      !/^[a-f0-9]{64}$/i.test(String(release.sha256 ?? ""))) {
    return json({ ok: false, code: "release_metadata_invalid" }, 502);
  }
  return json({ ok: true, available: true, release });
}

Deno.serve(async (request: Request) => {
  if (request.method === "OPTIONS") return json({ ok: true });
  if (request.method !== "POST") return json({ ok: false, code: "method_not_allowed" }, 405);
  if (!SUPABASE_URL || !SERVICE_KEY) return json({ ok: false, code: "server_not_configured" }, 503);

  try {
    const body = await request.json();
    if (!body || typeof body !== "object" || Array.isArray(body)) {
      return json({ ok: false, code: "invalid_request" }, 400);
    }

    switch (String((body as Record<string, unknown>).action ?? "")) {
      case "activate":
        return await handleActivate(body as Record<string, unknown>);
      case "status":
        return await handleStatus(request);
      case "profiles":
        return await handleProfiles(request);
      case "usage":
        return await handleUsage(request, body as Record<string, unknown>);
      case "latest_release":
        return await handleLatestRelease();
      default:
        return json({ ok: false, code: "unknown_action" }, 400);
    }
  } catch (error) {
    console.error("windows-api", error instanceof Error ? error.message : "unexpected_error");
    return json({ ok: false, code: "service_unavailable", message: "ارتباط با سرویس ویندوز موقتاً در دسترس نیست." }, 503);
  }
});
