# معماری شاخهٔ v2rayn-foundation

این شاخه برای جایگزینی کلاینت اختصاصی قبلی ویندوز با **v2rayN رسمی (WPF)** ساخته شده است.

## منبع ثابت
- مخزن: https://github.com/2dust/v2rayN
- commit: `2b5c7ca75b9163ec49cb4941b18388a061c3d96d`
- Git submodule: `vendor/v2rayN`
- پروژهٔ WPF: `vendor/v2rayN/v2rayN/v2rayN.csproj` (مسیر واقعی نسبت به ریشهٔ upstream)
- فریم‌ورک: .NET 10 / WPF
- مجوز: GPL-3.0

## لایهٔ Moghimi
- `integrations/MoghimiDeviceService.cs`: ثبت دستگاه، سهمیهٔ رایگان ۸ GiB ماهانه، وضعیت حجم اشتراک، گزارش مصرف و همگام‌سازی پروفایل‌های ابری.
- `integrations/patch-v2rayn.ps1`: Patch زمان Build برای ادغام سرویس، کنترل سهمیه پیش از شروع Core، گزارش Proxy counters، محافظت از گروه ابری، رابط فارسی RTL، Glass UI، موشن و برندینگ بسته‌بندی.
- `supabase/functions/windows-api`: API ویندوز برای ثبت دستگاه، فعال‌سازی، وضعیت اشتراک، دریافت کاتالوگ و ثبت مصرف.
- `supabase/functions/moghimi-telegram-router`: Router بات تلگرام ویندوز/موبایل.
- `supabase/migrations`: طرح و RPCهای مخصوص دیتابیس ویندوز.

## بسته‌بندی
Workflow شاخهٔ `v2rayn-foundation`:
1. Submodule upstream را روی commit ثابت دریافت می‌کند.
2. Patchهای مقیمی را در checkout موقت upstream اعمال می‌کند.
3. اسکریپت رسمی `package-windows.ps1` را برای x64/WPF اجرا می‌کند تا Xray، sing-box، فایل‌های rule و مجوزهای upstream هم بسته‌بندی شوند.
4. وجود فایل‌ها، اندازهٔ هسته‌ها، مجوز GPL و اجرای اولیهٔ برنامه را بررسی می‌کند.
5. نصب‌کننده را به‌صورت Artifact در GitHub Actions منتشر می‌کند.

## سهمیه و شناسه
شناسهٔ نصب از MachineGuid ویندوز به GUID پایدار تبدیل می‌شود. API ثبت رایگان و فعال‌سازی خرید از یک روش هش مشترک استفاده می‌کنند تا اشتراک خریداری‌شده به همان دستگاه وصل شود. توکن و شناسه در DPAPI نگهداری می‌شوند. سهمیهٔ رایگان ۸ GiB در ماه UTC است و پس از آن، در صورت وجود، اشتراک پولی مصرف می‌شود.

## اصول ایمنی
- هیچ Service Role Key در برنامه قرار نمی‌گیرد.
- در نبود پاسخ معتبر entitlement، اتصال جدید fail-closed می‌شود.
- کانفیگ‌های ابری Moghimi از عملیات Copy/Share/Export/Edit/Delete محافظت می‌شوند؛ کانفیگ‌های شخصی مستقل باقی می‌مانند.
- برنامهٔ خام upstream نباید از مسیر updater جایگزین نسخهٔ Moghimi شود؛ به‌روزرسانی هسته‌ها جدا از به‌روزرسانی خود GUI است.
- تغییرات این شاخه تا پایان تست روی Windows واقعی نباید به main یا Stable منتقل شوند.

## مجوز
v2rayN تحت GPL-3.0 است. توزیع نسخهٔ مشتق‌شده مستلزم رعایت GPL-3.0، حفظ اعلان‌ها و ارائهٔ سورس متناظر شامل Patchهای مقیمی است.
