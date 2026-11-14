using System.Drawing;
using QRCoder;
using ZXing;
using ZXing.Common;
using ZXing.Windows.Compatibility;

namespace MoghimiVPN;

public static class QrCodeService
{
    public static string DecodeFromImage(string imagePath)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("فایل تصویر QR پیدا نشد.", imagePath);

        using var image = Image.FromFile(imagePath);
        using var bitmap = new Bitmap(image);
        var reader = new BarcodeReader
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }
            }
        };

        var result = reader.Decode(bitmap);
        if (result is null || string.IsNullOrWhiteSpace(result.Text))
            throw new InvalidOperationException("کد QR خوانده نشد؛ تصویر واضح‌تر یا فایل دیگری را امتحان کن.");
        return result.Text.Trim();
    }

    public static byte[] CreatePng(string payload, int pixelsPerModule = 10)
    {
        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("متن QR خالی است.", nameof(payload));

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var qr = new PngByteQRCode(data);
        return qr.GetGraphic(Math.Clamp(pixelsPerModule, 4, 24));
    }
}
