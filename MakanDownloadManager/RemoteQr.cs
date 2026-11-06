using QRCoder;

namespace MakanDownloadManager;

/// <summary>Makes the QR code picture (PNG bytes) for the remote-control address.</summary>
public static class RemoteQr
{
    public static byte[] Png(string text, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        using var qr = new PngByteQRCode(data);
        return qr.GetGraphic(Math.Clamp(pixelsPerModule, 1, 40));
    }
}
