# تغییر مبنای ویندوز: v2rayN

این شاخه، مبنای جدید را به‌صورت مرحله‌ای و قابل بازگشت آماده می‌کند. مخزن رسمی v2rayN به‌صورت Git submodule و روی commit ثابت ثبت شده است:

- Upstream: https://github.com/2dust/v2rayN
- Pinned commit: 2b5c7ca75b9163ec49cb4941b18388a061c3d96d
- پروژهٔ میزبان: vendor/v2rayN/v2rayN/v2rayN.csproj
- فریم‌ورک upstream: .NET 10 / WPF
- مجوز upstream: GPL-3.0

## اصول ادغام

1. موتور، parser، مدیریت پروفایل‌ها، subscription، tray و TUN از v2rayN گرفته می‌شود؛ هستهٔ اتصال اختصاصی قدیمی حذف نخواهد شد تا نسخهٔ جدید در CI و روی ویندوز واقعی تأیید شود.
2. سرویس‌های Moghimi باید به API ویندوز windows-api و پروژهٔ مستقل Supabase ویندوز متصل باشند؛ کلید service-role هرگز داخل برنامه قرار نمی‌گیرد.
3. سهمیهٔ رایگان سمت سرور اکنون 8 GiB است و ماهانه در UTC بازنشانی می‌شود. سهمیهٔ خریداری‌شده بعد از آن مصرف می‌شود.
4. شناسهٔ نصب باید از شناسهٔ پایدار ویندوز ساخته شود تا Uninstall/Reinstall برنامه سهمیهٔ دستگاه را صفر نکند؛ توکن دستگاه باید محلی و رمزگذاری‌شده نگه‌داری شود.
5. مصرف از آمار خود هسته گزارش می‌شود؛ نباید ترافیک مستقیم/خارج از تونل یا ترافیک غیرمرتبط را به اشتراک نسبت داد.
6. کانفیگ‌های Moghimi فقط از جدول windows_server_entries دریافت می‌شوند و نباید کپی/اشتراک‌گذاری/Export شوند. کانفیگ‌های شخصی v2rayN باید مستقل باقی بمانند.
7. رابط فارسی، RTL، glassmorphism و motion باید روی UI upstream اعمال شود؛ کنترل اتصال و چرخهٔ عمر هسته نباید با انیمیشن‌ها دستکاری شود.

## اجرای محلی

```powershell
git clone --recurse-submodules https://github.com/MOGHIMI-IR/MOGHIMI_VPN_windows.git
cd MOGHIMI_VPN_windows
dotnet publish vendor/v2rayN/v2rayN/v2rayN.csproj -c Release -r win-x64 --self-contained true
```

این مرحله فقط پایهٔ upstream را می‌سازد. ادغام quota، sync، قفل کانفیگ ابری و طراحی نهایی باید قبل از جایگزینی نسخهٔ منتشرشده تکمیل و تست شوند.

## مجوز

v2rayN با GPL-3.0 منتشر شده است. نسخهٔ مشتق‌شدهٔ توزیع‌شده باید الزامات GPL-3.0، از جمله ارائهٔ Corresponding Source و حفظ notices و license متناظر را رعایت کند. این مخزن commit دقیق منبع upstream و تغییرات Moghimi را ثبت می‌کند.