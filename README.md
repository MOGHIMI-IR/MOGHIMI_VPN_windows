# Moghimi VPN for Windows

## آخرین نصب‌کننده تأییدشده
- نسخه: **1.0.1 Beta 1**
- معماری: Windows x64
- فایل: `MoghimiVPN-Setup-x64.exe` (فقط نصب‌کننده؛ بدون Portable)
- اندازه: **94,711,109 بایت**
- SHA-256: `16bedca60fc5650033396ad7c2ab7521392f18ef6388d703b14865f44dba6cf8`
- [دانلود مستقیم Setup](https://github.com/MOGHIMI-IR/MOGHIMI_VPN_windows/releases/download/windows-v1.0.1-beta.1/MoghimiVPN-Setup-x64.exe)
- [گزارش CI ساخت، اجرای اولیه، نصب و Uninstall](https://github.com/MOGHIMI-IR/MOGHIMI_VPN_windows/actions/runs/44338986232)
- Installer هنوز Authenticode امضای ناشر ندارد؛ ممکن است Windows SmartScreen هشدار Unknown Publisher بدهد. Defender را خاموش نکن.

کلاینت دسکتاپ ویندوز مقیمی؛ پروژه‌ای مستقل از نسخه‌های Android و iOS.

## زیرساخت مستقل ویندوز
- Supabase مخصوص ویندوز: hxfnoaeyjnkecegpppcy
- GitHub مخصوص ویندوز: MOGHIMI-IR/MOGHIMI_VPN_windows
- جدول‌ها، کدهای فعال‌سازی، اشتراک، دستگاه‌ها، مصرف، انتشار Installer و سفارش‌های ویندوز فقط در پروژه windows-MG نگهداری می‌شوند.
- هیچ Service Role یا Secret در برنامه دسکتاپ قرار نمی‌گیرد.
- کانفیگ‌های سرور از جدول windows_server_entries خوانده می‌شوند؛ سرور جدید را فقط در همین جدول پروژه ویندوز وارد کن.
- پلن پیش‌فرض ویندوز: ۱۰ گیگابایت، ۱۱ روز، ۵۸٬۰۰۰ تومان؛ مقدار آن در windows_purchase_plans قرار دارد.

## امکانات
- VPN Mode (TUN): عبور ترافیک ویندوز از تونل Wintun؛ UAC فقط برای ساخت رابط شبکه.
- Proxy Mode: استفاده از System Proxy با بازگردانی تنظیم قبلی هنگام Disconnect.
- Smart Connect: سنجش HTTPS واقعی سرورهای ابری Moghimi با حداکثر ۴ تست هم‌زمان و انتخاب کمترین تأخیر معتبر.
- پروتکل‌های VLESS، VMess، Trojan، Shadowsocks، Hysteria2، TUIC، AnyTLS، WireGuard، SOCKS5 و HTTP، مشروط به سازگاری Transport با sing-box.
- افزودن کانفیگ شخصی با لینک، Paste، فایل و QR؛ ویرایش، حذف، تست تأخیر، کپی لینک و Export QR برای کانفیگ شخصی.
- کانفیگ‌های اختصاصی Moghimi قابل Copy/Share/Export/QR یا ویرایش نیستند و در فایل محلی پروفایل‌ها ذخیره نمی‌شوند.
- اشتراک شخصی و به‌روزرسانی آن، QR Import، تنظیم DNS، عبور LAN، فیلتر دامنه‌ها، گزارش محلی و بازیابی خودکار محدود پس از خروج غیرمنتظره هسته؛ VPN/TUN فقط یک تلاش مجدد دارد تا پنجره UAC پشت‌سرهم باز نشود و Proxy Mode حداکثر سه تلاش با فاصله افزایشی دارد.
- کد فعال‌سازی یک‌بارمصرف ویندوز؛ توکن دستگاه با Windows DPAPI رمزگذاری می‌شود.
- گزارش مصرف آپلود/دانلود از API محلی sing-box هر ۳۰ ثانیه و هنگام قطع اتصال.
- فونت فارسی Estedad با پنج وزن Regular/Medium/SemiBold/Bold/ExtraBold به‌صورت داخلی در Installer بسته‌بندی می‌شود؛ اندازه نوشته‌های رابط حداقل ۱۴ تا ۱۵ پیکسل نگه داشته شده است.

## معماری VPN و سنجش مصرف
هسته ثابت‌شده در CI، sing-box 1.14.4 است. API محلی Clash فقط روی loopback، با پورت تصادفی و Secret تصادفی فعال است. آمار ترافیک از endpoint جریان ترافیک موتور خوانده می‌شود و هر ۳۰ ثانیه به windows-api ارسال می‌شود؛ API و داده‌های مصرف از پروژه Supabase موبایل جدا هستند.

## بات تلگرام ویندوز و موبایل
تابع مرکزی moghimi-telegram-router در Supabase ویندوز مستقر شده است. با شروع مکالمه، کاربر نسخه ویندوز یا موبایل را انتخاب می‌کند. این Router وب‌هوک واحد بات است؛ داده‌های ویندوز مستقیماً در دیتابیس windows-MG می‌مانند و پیام‌های موبایل فقط به تابع قبلی موبایل Forward می‌شوند.
- سفارش‌ها، رسیدها، کدهای فعال‌سازی و پشتیبانی ویندوز در جدول‌های windows_* پروژه ویندوز ذخیره می‌شوند.
- برای موبایل، رویدادها به تابع قبلی بات در پروژه موبایل Forward می‌شوند؛ منطق خرید اندروید حفظ شده و جدول‌های موبایل تغییر نکرده‌اند. تابع سلامت بات موبایل فقط پس از آماده بودن Router و Secret فوروارد، وب‌هوک مرکزی را فعال می‌کند.
- Router مستقل ویندوز فعال است، اما تا زمانی که Secretهای لازم در Supabase ویندوز ثبت و Health آن موفق نشود، وب‌هوک زنده نباید منتقل شود.

### Secretهای لازم در Supabase ویندوز
در Project Settings / Edge Functions / Secrets پروژه hxfnoaeyjnkecegpppcy این مقادیر را ثبت کن:
- TELEGRAM_BOT_TOKEN: همان توکن بات فعلی؛ آن را در چت یا سورس قرار نده.
- TELEGRAM_WEBHOOK_SECRET: دقیقاً همان مقدار TELEGRAM_WEBHOOK_SECRET در پروژه فعلی بات qaxi...؛ این مقدار باید با Secret موجود یکی باشد، چون تابع سلامت فعلی بات مقصد وب‌هوک را به Router ویندوز تغییر می‌دهد.
- MOBILE_BOT_WEBHOOK_SECRET: دقیقاً همان مقدار TELEGRAM_WEBHOOK_SECRET پروژه qaxi...؛ برای فوروارد امن پیام‌های موبایل به تابع قبلی استفاده می‌شود.
- PAYMENT_CARD_NUMBER: همان شماره کارت پرداخت که بات فعلی استفاده می‌کند.
- TELEGRAM_ADMIN_IDS: شناسه‌های عددی مدیران، جداشده با کاما؛ یا مدیرها را در جدول windows_telegram_admins ثبت کن.

مقادیر بالا را فقط در بخش Secrets ثبت کن. هیچ‌کدام را در GitHub سورس، برنامه ویندوز یا پیام عمومی قرار نده. متغیرهای استاندارد SUPABASE_URL و SUPABASE_SERVICE_ROLE_KEY توسط محیط Supabase فراهم می‌شوند و نباید به کلاینت داده شوند.

پس از ثبت همه Secretها، با باز کردن endpoint سلامت تابع بات فعلی، مهاجرت وب‌هوک انجام می‌شود:
https://qaxiunpklkbgfjwmpwfp.supabase.co/functions/v1/moghimi-telegram

این بررسی فقط وقتی وب‌هوک را به Router منتقل می‌کند که Router ویندوز پاسخ سلامت کامل بدهد و مسیر فوروارد موبایل هم آماده باشد؛ تا آن زمان وب‌هوک قبلی دست‌نخورده می‌ماند. مقصد نهایی:
https://hxfnoaeyjnkecegpppcy.supabase.co/functions/v1/moghimi-telegram-router

تابع بات موبایل به منطق قبلی اندروید بازگردانده شده و فقط مقصد Health/Webhook آن به Router منتقل شده است؛ ساختار و جدول‌های دیتابیس موبایل نباید تغییر کنند.

## انتشار Installer در Supabase ویندوز
Bucket عمومی دانلود فقط برای فایل نصب‌کننده: windows-releases. نوشتن در Storage فقط از CI و با Service Role پروژه ویندوز انجام می‌شود؛ از کلاینت ویندوز انجام نمی‌شود.

برای آپلود خودکار از GitHub Actions، Secret زیر را در مخزن GitHub ویندوز اضافه کن:
- WINDOWS_SUPABASE_SERVICE_ROLE_KEY: کلید Service Role پروژه windows-MG؛ هرگز کلید پروژه موبایل را اینجا نگذار.

پس از اضافه شدن Secret، Workflow بعد از تست نصب‌کننده:
1. فایل Setup x64 را در Bucket windows-releases قرار می‌دهد.
2. SHA-256 را محاسبه می‌کند.
3. رکورد نسخه را در windows_app_releases ثبت می‌کند.
4. هم‌زمان GitHub Release و Artifact نصب‌کننده را منتشر می‌کند.

تا وقتی Secret فوق به مخزن GitHub اضافه نشده، Build و GitHub Release انجام می‌شوند، اما فایل در windows-releases آپلود و رکورد windows_app_releases ثبت نمی‌شود؛ در نتیجه به‌روزرسانی درون‌برنامه‌ای هنوز فعال نیست.

## ساخت و بسته‌بندی
- Windows 10/11 x64 و .NET 8 self-contained
- رابط WPF فارسی RTL؛ رنگ‌های سرمه‌ای، بنفش و طلایی با کارت‌های شیشه‌ای، نورهای نرم پس‌زمینه و موشن سبک
- دانلود sing-box و Wintun از منابع رسمی با کنترل SHA-256؛ امضای Authenticode فایل Wintun نیز در CI بررسی می‌شود.
- خروجی فقط MoghimiVPN-Setup-x64.exe است؛ Portable/ZIP تولید نمی‌شود.
- CI نصب، اجرای اولیه، تست تنظیمات sing-box و حذف نصب را بررسی می‌کند.

## مدل امنیتی
- Defender خاموش نمی‌شود و استثنای آنتی‌ویروس ساخته نمی‌شود.
- هیچ Service Role در برنامه یا سورس قابل انتشار قرار نمی‌گیرد.
- داده‌های حساس فعال‌سازی با DPAPI نگهداری می‌شوند.
- کانفیگ‌های ابری Moghimi در ProfileStore محلی ذخیره نمی‌شوند؛ فقط در حافظه حین استفاده نگهداری می‌شوند.
- Proxy Mode فقط برنامه‌های سازگار با Windows proxy را پوشش می‌دهد؛ برای تونل سراسری از VPN Mode استفاده کن.
- SmartScreen را نمی‌توان تضمینی حذف کرد. بدون گواهی معتبر ناشر، ممکن است هشدار ناشر ناشناس نمایش داده شود؛ Defender را برای نصب خاموش نکن.

## اعتبارسنجی
CI کامپایل و بسته‌بندی را بررسی می‌کند، تنظیمات sing-box را برای پروتکل‌ها در VPN/Proxy Mode اعتبارسنجی می‌کند، DNSهای UDP/TLS/HTTPS را می‌آزماید، رفت‌وبرگشت لینک و QR را بررسی می‌کند و نصب/اجرای اولیه/حذف نصب را روی Runner ویندوز تست می‌کند. این تست‌ها جایگزین تست روی رایانه ویندوز واقعی با سرور واقعی نیستند؛ قبل از انتشار Stable باید TUN، DNS، IPv4/IPv6، Sleep/Resume، تغییر شبکه، Proxy restoration و پروتکل‌ها روی Windows 10/11 واقعی آزمایش شوند.
