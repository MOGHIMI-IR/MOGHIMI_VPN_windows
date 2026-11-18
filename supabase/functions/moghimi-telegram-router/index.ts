import "jsr:@supabase/functions-js/edge-runtime.d.ts";

const WIN_URL = (Deno.env.get("SUPABASE_URL") ?? "").replace(/\/$/, "");
const WIN_KEY = (() => {
  const direct = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") ?? "";
  if (direct) return direct;
  try {
    return String(JSON.parse(Deno.env.get("SUPABASE_SECRET_KEYS") ?? "{}").default ?? "");
  } catch {
    return "";
  }
})();
const BOT_TOKEN = Deno.env.get("TELEGRAM_BOT_TOKEN") ?? "";
const BOT_SECRET = Deno.env.get("TELEGRAM_WEBHOOK_SECRET") ?? "";
const MOBILE_WEBHOOK_SECRET = Deno.env.get("MOBILE_BOT_WEBHOOK_SECRET") ?? "";
const MOBILE_FUNCTION_URL = "https://qaxiunpklkbgfjwmpwfp.supabase.co/functions/v1/moghimi-telegram";
const CARD = Deno.env.get("PAYMENT_CARD_NUMBER") ?? "";
const ENV_ADMINS = new Set((Deno.env.get("TELEGRAM_ADMIN_IDS") ?? "").split(",").map(x => x.trim()).filter(Boolean));
const TG = "https://api.telegram.org/bot" + BOT_TOKEN;

const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), {
  status,
  headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" }
});
const html = (value: unknown) => String(value ?? "")
  .replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");

function dbHeaders(prefer?: string): Headers {
  const h = new Headers({ apikey: WIN_KEY, authorization: "Bearer " + WIN_KEY, "content-type": "application/json" });
  if (prefer) h.set("prefer", prefer);
  return h;
}
async function db(path: string, method = "GET", body?: unknown, prefer?: string) {
  const response = await fetch(WIN_URL + "/rest/v1/" + path, {
    method, headers: dbHeaders(prefer),
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const raw = await response.text();
  if (!response.ok) {
    console.error("windows_bot_db_error", method, path.split("?")[0], response.status);
    throw new Error("windows_bot_database_error");
  }
  return raw ? JSON.parse(raw) : null;
}
async function tg(method: string, data: Record<string, unknown>) {
  const response = await fetch(TG + "/" + method, {
    method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(data)
  });
  const value = await response.json();
  if (!value.ok) throw new Error("telegram_" + method + "_failed");
  return value.result;
}
async function send(chatId: number | string, text: string, markup?: unknown) {
  return tg("sendMessage", { chat_id: chatId, text, parse_mode: "HTML", ...(markup ? { reply_markup: markup } : {}) });
}
async function sendAdmins(text: string, markup?: unknown) {
  const admins = new Set(ENV_ADMINS);
  try {
    const rows = await db("windows_telegram_admins?enabled=eq.true&select=telegram_user_id");
    for (const row of rows ?? []) admins.add(String(row.telegram_user_id));
  } catch (error) {
    console.error("windows_admin_lookup_failed", error instanceof Error ? error.message : "failed");
  }
  for (const admin of admins) {
    try { await send(Number(admin), text, markup); }
    catch (error) { console.error("windows_admin_notify_failed", String(error)); }
  }
}
async function sendAdminsFile(message: any, caption: string) {
  const admins = new Set(ENV_ADMINS);
  try {
    const rows = await db("windows_telegram_admins?enabled=eq.true&select=telegram_user_id");
    for (const row of rows ?? []) admins.add(String(row.telegram_user_id));
  } catch (error) {
    console.error("windows_admin_file_lookup_failed", error instanceof Error ? error.message : "failed");
  }
  const photo = message.photo?.at(-1)?.file_id;
  const document = message.document?.file_id;
  const video = message.video?.file_id;
  const voice = message.voice?.file_id;
  for (const admin of admins) {
    try {
      const chat_id = Number(admin);
      if (photo) await tg("sendPhoto", { chat_id, photo, caption: caption.slice(0, 900) });
      else if (document) await tg("sendDocument", { chat_id, document, caption: caption.slice(0, 900) });
      else if (video) await tg("sendVideo", { chat_id, video, caption: caption.slice(0, 900) });
      else if (voice) await tg("sendVoice", { chat_id, voice, caption: caption.slice(0, 900) });
    } catch (error) {
      console.error("windows_admin_file_send_failed", error instanceof Error ? error.message : "failed");
    }
  }
}
async function setState(userId: number, chatId: number, state: string, data: Record<string, unknown> = {}) {
  await db("windows_bot_user_states?on_conflict=telegram_user_id", "POST", {
    telegram_user_id: userId, telegram_chat_id: chatId, state, data, updated_at: new Date().toISOString()
  }, "resolution=merge-duplicates,return=minimal");
}
async function getState(userId: number) {
  const rows = await db("windows_bot_user_states?telegram_user_id=eq." + userId + "&select=*");
  return rows?.[0] ?? null;
}
async function setPlatform(userId: number, chatId: number, platform: "windows" | "mobile" | null) {
  await db("windows_bot_platform_sessions?on_conflict=telegram_user_id", "POST", {
    telegram_user_id: userId, telegram_chat_id: chatId, platform,
    selected_at: platform ? new Date().toISOString() : null,
    updated_at: new Date().toISOString()
  }, "resolution=merge-duplicates,return=minimal");
  if (platform === "windows") {
    await db("windows_customers?on_conflict=telegram_user_id", "POST", {
      telegram_user_id: userId, telegram_chat_id: chatId,
      updated_at: new Date().toISOString()
    }, "resolution=merge-duplicates,return=minimal");
  }
}
async function getPlatform(userId: number): Promise<"windows" | "mobile" | null> {
  const rows = await db("windows_bot_platform_sessions?telegram_user_id=eq." + userId + "&select=platform");
  const value = rows?.[0]?.platform;
  return value === "windows" || value === "mobile" ? value : null;
}
async function forwardToMobile(update: unknown) {
  if (!MOBILE_WEBHOOK_SECRET) {
    throw new Error("mobile_bot_forward_secret_missing");
  }
  const response = await fetch(MOBILE_FUNCTION_URL, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      "x-telegram-bot-api-secret-token": MOBILE_WEBHOOK_SECRET
    },
    body: JSON.stringify(update)
  });
  if (!response.ok) {
    console.error("mobile_bot_forward_failed", response.status);
    throw new Error("mobile_bot_temporarily_unavailable");
  }
}
function platformMenu() {
  return { inline_keyboard: [
    [{ text: "🖥 خدمات نسخه ویندوز", callback_data: "platform:windows" }],
    [{ text: "📱 خدمات نسخه موبایل", callback_data: "platform:mobile" }]
  ] };
}
function windowsMenu() {
  return { inline_keyboard: [
    [{ text: "🛒 خرید اشتراک ویندوز", callback_data: "win:buy" }],
    [{ text: "💬 پشتیبانی ویندوز", callback_data: "win:support" }],
    [{ text: "🔄 تغییر پلتفرم", callback_data: "platform:choose" }]
  ] };
}
function mobileMenu() {
  return { inline_keyboard: [
    [{ text: "🛒 خرید اشتراک", callback_data: "buy" }],
    [{ text: "💬 ارتباط با پشتیبانی", callback_data: "support" }]
  ] };
}
function receiptButtons(orderId: string) {
  return { inline_keyboard: [
    [{ text: "✅ تأیید و صدور کد ویندوز", callback_data: "win:approve:" + orderId }],
    [{ text: "❌ رد رسید ویندوز", callback_data: "win:reject:" + orderId }]
  ] };
}
function makeCode(): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), x => x.toString(16).padStart(2, "0")).join("").toUpperCase();
}
async function expireOrders(userId: number) {
  await db("windows_purchase_orders?telegram_user_id=eq." + userId +
    "&status=in.(awaiting_quote,awaiting_payment)&payment_deadline_at=lt." +
    encodeURIComponent(new Date().toISOString()),
    "PATCH", { status: "expired" }, "return=minimal");
}
async function orderPayment(order: any) {
  if (!CARD) throw new Error("windows_payment_card_not_configured");
  const text =
    "سفارش ویندوز شما ثبت شد ✅\n\n" +
    "نام کاربری: <b>@" + html(order.telegram_username) + "</b>\n" +
    "مدت: <b>" + Number(order.plan_days) + " روز</b>\n" +
    "حجم: <b>" + (Number(order.plan_bytes) / 1073741824).toFixed(2) + " گیگابایت</b>\n" +
    "مبلغ: <b>" + Number(order.amount_toman).toLocaleString("en-US") + " تومان</b>\n\n" +
    "شماره کارت:\n<code>" + html(CARD) + "</code>\n\n" +
    "پس از واریز، تصویر رسید را همین‌جا ارسال کن. مهلت پرداخت ۱۲ ساعت است.";
  await send(order.telegram_chat_id, text);
  await setState(Number(order.telegram_user_id), Number(order.telegram_chat_id), "receipt", { order_id: order.id });
}
async function activeSupportThread(userId: number) {
  const rows = await db("windows_support_threads?telegram_user_id=eq." + userId +
    "&status=eq.open&select=*&order=opened_at.desc&limit=1");
  return rows?.[0] ?? null;
}
async function createWindowsOrder(user: any, phone: string, state: any) {
  const data = state.data ?? {};
  const order = {
    telegram_user_id: user.id,
    telegram_username: data.username,
    telegram_chat_id: user.chat_id,
    phone,
    plan_kind: data.plan_kind,
    plan_bytes: data.plan_bytes,
    plan_days: data.plan_days,
    amount_toman: data.amount_toman ?? null,
    status: data.plan_kind === "fixed" ? "awaiting_payment" : "awaiting_quote",
    payment_deadline_at: new Date(Date.now() + 12 * 3600000).toISOString()
  };
  const rows = await db("windows_purchase_orders", "POST", order, "return=representation");
  const created = rows?.[0];
  if (!created) throw new Error("windows_order_create_failed");

  await db("windows_customers?on_conflict=telegram_user_id", "POST", {
    telegram_user_id: user.id, telegram_chat_id: user.chat_id,
    telegram_username: data.username, phone, updated_at: new Date().toISOString()
  }, "resolution=merge-duplicates,return=minimal");

  if (data.plan_kind === "fixed") {
    await orderPayment(created);
  } else {
    await setState(user.id, user.chat_id, "idle");
    await send(user.chat_id, "درخواست پلن دلخواه ویندوز ثبت شد؛ مبلغ پس از بررسی مدیر به همین‌جا ارسال می‌شود.");
    await sendAdmins(
      "🧾 درخواست قیمت‌گذاری اشتراک ویندوز\nسفارش: <code>" + created.id + "</code>\nکاربر: @" +
      html(created.telegram_username) + "\nشماره: " + created.phone + "\nمدت: " +
      created.plan_days + " روز\nحجم: " + (created.plan_bytes / 1073741824).toFixed(2) + " GB",
      { inline_keyboard: [[{ text: "💰 تعیین مبلغ ویندوز", callback_data: "win:quote:" + created.id }]] }
    );
  }
}
async function windowsPlanMenu(chatId: number) {
  const plans = await db("windows_purchase_plans?is_active=eq.true&select=plan_key,title,plan_kind,amount_toman&order=display_order.asc");
  const buttons = (plans ?? []).map((plan: any) => [{
    text: plan.plan_kind === "fixed" && Number.isFinite(Number(plan.amount_toman))
      ? String(plan.title) + " · " + Number(plan.amount_toman).toLocaleString("en-US") + " تومان"
      : String(plan.title),
    callback_data: "win:plan:" + String(plan.plan_key)
  }]);
  buttons.push([{ text: "بازگشت", callback_data: "win:home" }]);
  return send(chatId, "نوع اشتراک ویندوز را انتخاب کن:", { inline_keyboard: buttons });
}
async function windowsCallback(update: any) {
  const q = update.callback_query;
  const user = q.from;
  const chat = q.message?.chat?.id ?? user.id;
  const data = String(q.data ?? "");
  await tg("answerCallbackQuery", { callback_query_id: q.id });

  if (data === "win:home") return send(chat, "خدمات نسخه ویندوز Moghimi V2Ray:", windowsMenu());
  if (data === "win:buy") return windowsPlanMenu(chat);
  if (data === "win:support") {
    const old = await activeSupportThread(user.id);
    if (old) {
      await setState(user.id, chat, "support", { thread_id: old.id });
      return send(chat, "مکالمه پشتیبانی ویندوز باز است. پیام یا فایل بفرست؛ برای پایان /close را بزن.");
    }
    const rows = await db("windows_support_threads", "POST", {
      telegram_user_id: user.id, telegram_chat_id: chat,
      telegram_username: user.username ?? null, status: "open"
    }, "return=representation");
    const thread = rows?.[0];
    await setState(user.id, chat, "support", { thread_id: thread.id });
    await send(chat, "به پشتیبانی نسخه ویندوز وصل شدی. پیام یا فایل خودت را بفرست؛ برای پایان /close را بزن.");
    await sendAdmins("💬 پشتیبانی ویندوز جدید\nکاربر: @" + html(user.username ?? user.id) +
      "\nشناسه: <code>" + thread.id + "</code>",
      { inline_keyboard: [[
        { text: "↩️ پاسخ", callback_data: "win:reply:" + thread.id },
        { text: "🔒 بستن", callback_data: "win:close:" + thread.id }
      ]] });
    return;
  }
  if (data.startsWith("win:plan:")) {
    const key = data.slice("win:plan:".length);
    const rows = await db("windows_purchase_plans?plan_key=eq." + encodeURIComponent(key) +
      "&is_active=eq.true&select=*");
    const plan = rows?.[0];
    if (!plan) return send(chat, "این پلن در حال حاضر فعال نیست.");
    if (plan.plan_kind === "custom") {
      await setState(user.id, chat, "days");
      return send(chat, "مدت اشتراک را به روز وارد کن (۱ تا ۳۶۵۰).");
    }
    await setState(user.id, chat, "username", {
      plan_kind: "fixed", plan_days: Number(plan.duration_days),
      plan_bytes: Number(plan.plan_bytes), amount_toman: Number(plan.amount_toman)
    });
    return send(chat, "نام کاربری تلگرامت را دقیق وارد کن (بدون @).");
  }

  if (!ENV_ADMINS.has(String(user.id))) {
    const admins = await db("windows_telegram_admins?telegram_user_id=eq." + user.id +
      "&enabled=eq.true&select=telegram_user_id");
    if (!admins?.length) return send(chat, "این عملیات در دسترس نیست.");
  }

  if (data.startsWith("win:quote:")) {
    await setPlatform(user.id, chat, "windows");
    await setState(user.id, chat, "admin_quote", { order_id: data.slice("win:quote:".length) });
    return send(chat, "مبلغ اشتراک ویندوز را به تومان و فقط به‌صورت عدد وارد کن.");
  }
  if (data.startsWith("win:approve:")) {
    const id = data.slice("win:approve:".length);
    const rows = await db("windows_purchase_orders?id=eq." + id + "&select=*");
    const order = rows?.[0];
    if (!order || order.status !== "receipt_submitted") return send(chat, "این سفارش برای تأیید در وضعیت معتبر نیست.");

    const rawCode = makeCode();
    const codeHash = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(rawCode));
    const hash = Array.from(new Uint8Array(codeHash), x => x.toString(16).padStart(2, "0")).join("");
    const codeRows = await db("windows_activation_codes", "POST", {
      order_id: order.id, code_hash: hash,
      telegram_user_id: order.telegram_user_id,
      telegram_username: order.telegram_username, phone: order.phone,
      granted_bytes: order.plan_bytes, duration_days: order.plan_days,
      expires_at: new Date(Date.now() + 7 * 86400000).toISOString(),
      status: "issued"
    }, "return=representation");
    await db("windows_purchase_orders?id=eq." + id, "PATCH", {
      status: "approved", reviewed_at: new Date().toISOString(),
      reviewed_by: user.id, activation_code_id: codeRows[0].id
    }, "return=minimal");
    await send(order.telegram_chat_id,
      "پرداخت اشتراک ویندوز تأیید شد ✅\nکد فعال‌سازی ویندوز:\n<code>" + rawCode +
      "</code>\n\nدر برنامه ویندوز، بخش تنظیمات، گزینه «ثبت کد فعال‌سازی» را بزن. کد یک‌بارمصرف است.");
    return send(chat, "رسید تأیید شد و کد فعال‌سازی ویندوز برای کاربر ارسال شد.");
  }
  if (data.startsWith("win:reject:")) {
    const id = data.slice("win:reject:".length);
    const rows = await db("windows_purchase_orders?id=eq." + id + "&select=*");
    const order = rows?.[0];
    if (order?.status === "receipt_submitted") {
      await db("windows_purchase_orders?id=eq." + id, "PATCH", {
        status: "rejected", reviewed_at: new Date().toISOString(), reviewed_by: user.id
      }, "return=minimal");
      await send(order.telegram_chat_id, "رسید پرداخت ویندوز تأیید نشد؛ برای پیگیری با پشتیبانی پیام بده.");
      await send(chat, "رسید ویندوز رد شد.");
    }
    return;
  }
  if (data.startsWith("win:reply:")) {
    await setPlatform(user.id, chat, "windows");
    await setState(user.id, chat, "admin_reply", { thread_id: data.slice("win:reply:".length) });
    return send(chat, "متن یا فایل پاسخ پشتیبانی ویندوز را ارسال کن.");
  }
  if (data.startsWith("win:close:")) {
    const id = data.slice("win:close:".length);
    const rows = await db("windows_support_threads?id=eq." + id + "&select=telegram_chat_id,status");
    const thread = rows?.[0];
    if (thread?.status === "open") {
      await db("windows_support_threads?id=eq." + id, "PATCH", {
        status: "closed", closed_at: new Date().toISOString(), closed_by: user.id
      }, "return=minimal");
      await send(thread.telegram_chat_id, "مکالمه پشتیبانی ویندوز پایان یافت.");
      await send(chat, "مکالمه بسته شد.");
    }
  }
}
async function windowsAdminMessage(message: any, state: any) {
  const user = message.from, chat = message.chat.id;
  const text = String(message.text ?? message.caption ?? "").trim();
  if (state?.state === "admin_quote") {
    const amount = Number(text.replace(/[,\s٬]/g, ""));
    if (!Number.isSafeInteger(amount) || amount < 1000 || amount > 1000000000)
      return send(chat, "مبلغ معتبر نیست؛ مبلغ را به تومان وارد کن.");
    const id = String(state.data.order_id);
    const rows = await db("windows_purchase_orders?id=eq." + id + "&select=*");
    const order = rows?.[0];
    if (!order || order.status !== "awaiting_quote" || Date.now() > Date.parse(order.payment_deadline_at)) {
      await setState(user.id, chat, "idle");
      return send(chat, "این سفارش ویندوز دیگر قابل قیمت‌گذاری نیست.");
    }
    await db("windows_purchase_orders?id=eq." + id, "PATCH", {
      amount_toman: amount, status: "awaiting_payment",
      payment_deadline_at: new Date(Date.now() + 12 * 3600000).toISOString()
    }, "return=minimal");
    await orderPayment({ ...order, amount_toman: amount });
    await setState(user.id, chat, "idle");
    return send(chat, "مبلغ ثبت شد و اطلاعات پرداخت ویندوز ارسال شد.");
  }
  if (state?.state === "admin_reply") {
    const id = String(state.data.thread_id);
    const rows = await db("windows_support_threads?id=eq." + id + "&status=eq.open&select=*");
    const thread = rows?.[0];
    if (!thread) {
      await setState(user.id, chat, "idle");
      return send(chat, "این مکالمه ویندوز بسته شده است.");
    }
    const photo = message.photo?.at(-1)?.file_id;
    const doc = message.document?.file_id;
    const video = message.video?.file_id;
    const voice = message.voice?.file_id;
    const file = photo ?? doc ?? video ?? voice ?? null;
    const type = photo ? "photo" : doc ? "document" : video ? "video" : voice ? "voice" : "text";
    await db("windows_support_messages", "POST", {
      thread_id: id, sender_kind: "admin", sender_telegram_id: user.id,
      message_type: type, message_text: text || null, telegram_file_id: file,
      telegram_message_id: message.message_id
    }, "return=minimal");
    if (photo) await tg("sendPhoto", { chat_id: thread.telegram_chat_id, photo, caption: text || "پاسخ پشتیبانی ویندوز" });
    else if (doc) await tg("sendDocument", { chat_id: thread.telegram_chat_id, document: doc, caption: text || "پاسخ پشتیبانی ویندوز" });
    else if (video) await tg("sendVideo", { chat_id: thread.telegram_chat_id, video, caption: text || "پاسخ پشتیبانی ویندوز" });
    else if (voice) await tg("sendVoice", { chat_id: thread.telegram_chat_id, voice });
    else await send(thread.telegram_chat_id, "پشتیبانی ویندوز:\n" + html(text));
    await db("windows_support_threads?id=eq." + id, "PATCH", { last_message_at: new Date().toISOString() }, "return=minimal");
    await setState(user.id, chat, "idle");
    return send(chat, "پاسخ پشتیبانی ویندوز ارسال شد.");
  }
  if (text === "/orders" || text === "/windows_orders") {
    const orders = await db("windows_purchase_orders?status=in.(awaiting_quote,awaiting_payment,receipt_submitted)" +
      "&select=id,telegram_username,phone,plan_bytes,plan_days,amount_toman,status&order=created_at.desc&limit=25");
    if (!orders?.length) return send(chat, "سفارش بازی برای ویندوز وجود ندارد.");
    for (const order of orders) {
      const markup = order.status === "receipt_submitted"
        ? receiptButtons(order.id)
        : order.status === "awaiting_quote"
          ? { inline_keyboard: [[{ text: "تعیین مبلغ", callback_data: "win:quote:" + order.id }]] }
          : undefined;
      await send(chat, "سفارش ویندوز <code>" + order.id + "</code>\n@" +
        html(order.telegram_username) + " · " + order.plan_days + " روز · " +
        (order.plan_bytes / 1073741824).toFixed(2) + "GB · " + order.status, markup);
    }
    return;
  }
  if (text === "/threads" || text === "/windows_threads") {
    const threads = await db("windows_support_threads?status=eq.open&select=id,telegram_user_id,telegram_username&order=last_message_at.desc&limit=20");
    if (!threads?.length) return send(chat, "گفت‌وگوی باز ویندوز وجود ندارد.");
    for (const thread of threads) {
      await send(chat, "پشتیبانی ویندوز <code>" + thread.id + "</code>\n@" +
        html(thread.telegram_username ?? thread.telegram_user_id),
        { inline_keyboard: [[
          { text: "پاسخ", callback_data: "win:reply:" + thread.id },
          { text: "بستن", callback_data: "win:close:" + thread.id }
        ]] });
    }
  }
}
async function windowsUserMessage(message: any, state: any) {
  const user = message.from, chat = message.chat.id;
  const text = String(message.text ?? message.caption ?? "").trim();
  await expireOrders(user.id);

  if (state?.state === "days") {
    const days = Number(text);
    if (!Number.isInteger(days) || days < 1 || days > 3650)
      return send(chat, "مدت باید عدد صحیح بین ۱ تا ۳۶۵۰ روز باشد.");
    await setState(user.id, chat, "gigabytes", { plan_days: days });
    return send(chat, "حجم اشتراک را به گیگابایت وارد کن (۱ تا ۵۰۰۰).");
  }
  if (state?.state === "gigabytes") {
    const gb = Number(text.replace(",", "."));
    if (!Number.isFinite(gb) || gb < 1 || gb > 5000)
      return send(chat, "حجم باید بین ۱ تا ۵۰۰۰ گیگابایت باشد.");
    await setState(user.id, chat, "username", {
      plan_kind: "custom", plan_days: state.data.plan_days,
      plan_bytes: Math.round(gb * 1073741824)
    });
    return send(chat, "نام کاربری تلگرامت را دقیق وارد کن (بدون @).");
  }
  if (state?.state === "username") {
    const username = text.replace(/^@/, "");
    if (!/^[A-Za-z0-9_]{5,32}$/.test(username) ||
        !user.username || username.toLowerCase() !== user.username.toLowerCase())
      return send(chat, "نام کاربری واردشده باید دقیقاً با نام کاربری همین حساب تلگرامت یکسان باشد.");
    await setState(user.id, chat, "phone", { ...state.data, username });
    return send(chat, "شماره موبایل را وارد کن؛ باید با 09 شروع شود و دقیقاً ۱۱ رقم باشد.");
  }
  if (state?.state === "phone") {
    if (!/^09\d{9}$/.test(text))
      return send(chat, "شماره موبایل باید با ۰۹ شروع شود و دقیقاً ۱۱ رقم باشد.");
    return createWindowsOrder({ id: user.id, chat_id: chat }, text, state);
  }
  if (state?.state === "receipt") {
    const orderId = state.data.order_id;
    const rows = await db("windows_purchase_orders?id=eq." + orderId +
      "&telegram_user_id=eq." + user.id + "&select=*");
    const order = rows?.[0];
    if (!order || order.status !== "awaiting_payment" || Date.now() > Date.parse(order.payment_deadline_at)) {
      await setState(user.id, chat, "idle");
      return send(chat, "مهلت پرداخت پایان یافته یا سفارش ویندوز معتبر نیست. برای سفارش جدید /start را بزن.");
    }
    const photo = message.photo?.at(-1)?.file_id;
    const doc = message.document?.file_id;
    const file = photo ?? doc;
    if (!file) return send(chat, "لطفاً تصویر فیش را به‌صورت عکس یا فایل ارسال کن.");
    await db("windows_purchase_orders?id=eq." + orderId, "PATCH", {
      status: "receipt_submitted", receipt_file_id: file,
      receipt_message_id: message.message_id, receipt_submitted_at: new Date().toISOString()
    }, "return=minimal");
    const receiptCaption = "📥 رسید پرداخت ویندوز\nسفارش: <code>" + orderId +
      "</code>\nکاربر: @" + html(order.telegram_username) + "\nتلفن: " +
      order.phone + "\nمبلغ: " + Number(order.amount_toman).toLocaleString("en-US") + " تومان";
    await sendAdmins(receiptCaption, receiptButtons(orderId));
    await sendAdminsFile(message, "رسید خرید ویندوز · سفارش " + orderId);
    await setState(user.id, chat, "receipt_submitted", { order_id: orderId });
    return send(chat, "رسید خرید ویندوز دریافت شد ✅ پس از تأیید مدیر، کد فعال‌سازی همین‌جا ارسال می‌شود.");
  }
  if (state?.state === "receipt_submitted")
    return send(chat, "رسید خرید ویندوز در صف بررسی است؛ پس از تأیید کد فعال‌سازی را دریافت می‌کنی.");

  if (text === "/close") {
    const thread = await activeSupportThread(user.id);
    if (!thread) return send(chat, "مکالمه پشتیبانی ویندوز بازی نداری.");
    await db("windows_support_threads?id=eq." + thread.id, "PATCH", {
      status: "closed", closed_at: new Date().toISOString(), close_reason: "closed_by_user"
    }, "return=minimal");
    await setState(user.id, chat, "idle");
    await sendAdmins("کاربر @" + html(user.username ?? user.id) + " مکالمه پشتیبانی ویندوز را بست.");
    return send(chat, "مکالمه پشتیبانی ویندوز بسته شد.");
  }

  const thread = await activeSupportThread(user.id);
  if (thread && (state?.state === "support" || !text.startsWith("/"))) {
    const photo = message.photo?.at(-1)?.file_id;
    const doc = message.document?.file_id;
    const video = message.video?.file_id;
    const voice = message.voice?.file_id;
    const file = photo ?? doc ?? video ?? voice ?? null;
    const type = photo ? "photo" : doc ? "document" : video ? "video" : voice ? "voice" : "text";
    await db("windows_support_messages", "POST", {
      thread_id: thread.id, sender_kind: "user", sender_telegram_id: user.id,
      message_type: type, message_text: text || null, telegram_file_id: file,
      telegram_message_id: message.message_id
    }, "return=minimal");
    await db("windows_support_threads?id=eq." + thread.id, "PATCH", { last_message_at: new Date().toISOString() }, "return=minimal");
    const supportCaption = "💬 پیام پشتیبانی ویندوز\nکاربر: @" + html(user.username ?? user.id) +
      "\n" + html(text).slice(0, 1200);
    await sendAdmins(supportCaption, { inline_keyboard: [[
      { text: "↩️ پاسخ", callback_data: "win:reply:" + thread.id },
      { text: "🔒 بستن", callback_data: "win:close:" + thread.id }
    ]] });
    await sendAdminsFile(message, "پیوست پیام پشتیبانی ویندوز · " + thread.id);
    return send(chat, "پیامت برای پشتیبانی نسخه ویندوز ارسال شد ✅");
  }

  return send(chat, "برای خرید یا پشتیبانی ویندوز یکی از گزینه‌ها را انتخاب کن:", windowsMenu());
}
async function handlePlatformSelection(update: any) {
  const q = update.callback_query;
  const user = q.from;
  const chat = q.message?.chat?.id ?? user.id;
  const data = String(q.data ?? "");
  await tg("answerCallbackQuery", { callback_query_id: q.id });

  if (data === "platform:choose") {
    await setPlatform(user.id, chat, null);
    return send(chat, "برای ادامه، پلتفرم موردنظرت را انتخاب کن:", platformMenu());
  }
  if (data === "platform:windows") {
    await setPlatform(user.id, chat, "windows");
    await setState(user.id, chat, "idle");
    return send(chat, "نسخه ویندوز انتخاب شد 🖥\nخدمات ویندوز در دیتابیس مستقل ویندوز ثبت می‌شوند:", windowsMenu());
  }
  if (data === "platform:mobile") {
    await setPlatform(user.id, chat, "mobile");
    return send(chat, "نسخه موبایل انتخاب شد 📱\nخرید و پشتیبانی موبایل مانند قبل از طریق سیستم اندروید انجام می‌شود:", mobileMenu());
  }
}
async function processUpdate(update: any) {
  if (update.callback_query) {
    const q = update.callback_query;
    const userId = q.from.id;
    const data = String(q.data ?? "");
    if (data.startsWith("platform:")) return handlePlatformSelection(update);
    if (data.startsWith("win:")) return windowsCallback(update);

    const platform = await getPlatform(userId);
    const mobileAdminAction = data.startsWith("approve:") || data.startsWith("reject:") ||
      data.startsWith("quote:") || data.startsWith("reply:") || data.startsWith("close:");
    if (platform === "mobile" || mobileAdminAction) {
      // Persist the admin's platform context before forwarding; follow-up free text replies
      // must continue to reach the original mobile bot instead of the Windows state machine.
      if (mobileAdminAction) await setPlatform(userId, q.message?.chat?.id ?? userId, "mobile");
      return forwardToMobile(update);
    }
    await tg("answerCallbackQuery", { callback_query_id: q.id });
    return send(q.message?.chat?.id ?? userId, "ابتدا پلتفرم را انتخاب کن:", platformMenu());
  }

  if (update.message) {
    const message = update.message;
    const userId = message.from?.id;
    if (!userId) return;
    const chatId = message.chat?.id ?? userId;
    const text = String(message.text ?? message.caption ?? "").trim();

    if (text === "/start" || text === "/platform") {
      await setPlatform(userId, chatId, null);
      await setState(userId, chatId, "idle");
      return send(chatId, "سلام 👋\nقبل از ادامه، مشخص کن برای کدام نسخه خدمات می‌خواهی:", platformMenu());
    }

    const platform = await getPlatform(userId);
    if (platform === "mobile") return forwardToMobile(update);
    if (platform !== "windows") return send(chatId, "ابتدا پلتفرم را انتخاب کن:", platformMenu());

    const admins = new Set(ENV_ADMINS);
    try {
      const rows = await db("windows_telegram_admins?telegram_user_id=eq." + userId +
        "&enabled=eq.true&select=telegram_user_id");
      if (rows?.length) admins.add(String(userId));
    } catch { }
    const state = await getState(userId);
    if (admins.has(String(userId))) return windowsAdminMessage(message, state);
    return windowsUserMessage(message, state);
  }
}
Deno.serve(async (request: Request) => {
  if (request.method === "GET" || request.method === "HEAD") {
    let databaseAdminsConfigured = false;
    if (WIN_URL && WIN_KEY && ENV_ADMINS.size === 0) {
      try {
        const rows = await db("windows_telegram_admins?enabled=eq.true&select=telegram_user_id&limit=1");
        databaseAdminsConfigured = Array.isArray(rows) && rows.length > 0;
      } catch (error) {
        console.error("windows_admin_health_check_failed", error instanceof Error ? error.message : "failed");
      }
    }
    const adminsConfigured = ENV_ADMINS.size > 0 || databaseAdminsConfigured;
    const ready = Boolean(WIN_URL && WIN_KEY && BOT_TOKEN && BOT_SECRET &&
      MOBILE_WEBHOOK_SECRET && CARD && adminsConfigured);
    const response = json({
      ok: ready,
      service: "moghimi-telegram-router",
      windows_database: Boolean(WIN_URL && WIN_KEY),
      telegram_configured: Boolean(BOT_TOKEN && BOT_SECRET),
      mobile_forward_configured: Boolean(MOBILE_WEBHOOK_SECRET),
      payment_configured: Boolean(CARD),
      admins_configured: adminsConfigured
    }, ready ? 200 : 503);
    return request.method === "HEAD" ? new Response(null, { status: response.status, headers: response.headers }) : response;
  }
  if (request.method !== "POST") return json({ ok: false, code: "method_not_allowed" }, 405);
  if (!WIN_URL || !WIN_KEY || !BOT_TOKEN || !BOT_SECRET) return json({ ok: false, code: "not_configured" }, 503);
  if (request.headers.get("x-telegram-bot-api-secret-token") !== BOT_SECRET)
    return json({ ok: false }, 401);

  let updateId: number | null = null;
  try {
    const update = await request.json();
    const candidateId = Number(update.update_id);
    updateId = Number.isSafeInteger(candidateId) ? candidateId : null;

    if (updateId !== null) {
      const inserted = await db("windows_telegram_updates?on_conflict=update_id", "POST", {
        update_id: updateId, status: "received", error_code: null
      }, "resolution=ignore-duplicates,return=representation");

      if (Array.isArray(inserted) && inserted.length === 0) {
        const prior = await db("windows_telegram_updates?update_id=eq." + updateId + "&select=status");
        if (prior?.[0]?.status !== "failed") return json({ ok: true, duplicate: true });
        await db("windows_telegram_updates?update_id=eq." + updateId, "PATCH", {
          status: "received", error_code: null
        }, "return=minimal");
      }
    }

    await processUpdate(update);

    if (updateId !== null) {
      await db("windows_telegram_updates?update_id=eq." + updateId, "PATCH", {
        status: "processed", processed_at: new Date().toISOString(), error_code: null
      }, "return=minimal");
    }
    return json({ ok: true });
  } catch (error) {
    if (updateId !== null) {
      try {
        await db("windows_telegram_updates?update_id=eq." + updateId, "PATCH", {
          status: "failed", error_code: "handler_failed"
        }, "return=minimal");
      } catch { }
    }
    console.error("moghimi-telegram-router", error instanceof Error ? error.message : "failed");
    return json({ ok: false }, 500);
  }
});
