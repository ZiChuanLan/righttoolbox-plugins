using System.IO;
using QRCoder;
using QrCodeGen;
using ZXing;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

// 把 QRCoder 的模块矩阵转成 ZXing 能读的灰度图，闭环验证「扫得出来」。
static string? Decode(QRCodeData data)
{
    var modules = data.ModuleMatrix.Count;
    const int Scale = 4;
    var size = modules * Scale;
    var gray = new byte[size * size];
    for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            gray[y * size + x] = data.ModuleMatrix[y / Scale][x / Scale] ? (byte)0 : (byte)255;

    var reader = new BarcodeReaderGeneric
    {
        Options = new ZXing.Common.DecodingOptions
        {
            PossibleFormats = [BarcodeFormat.QR_CODE],
            TryHarder = true
        }
    };
    return reader.Decode(gray, size, size, RGBLuminanceSource.BitmapFormat.Gray8)?.Text;
}

var texts = new[]
{
    "https://example.com/righttoolbox",
    "Hello RightToolbox",
    "中文内容也要能扫出来 🚀",
    "WIFI:T:WPA;S:my-hotspot;P:p@ssw0rd!;;"
};

foreach (var text in texts)
{
    foreach (var level in new[] { QRCodeGenerator.ECCLevel.L, QRCodeGenerator.ECCLevel.M, QRCodeGenerator.ECCLevel.Q, QRCodeGenerator.ECCLevel.H })
    {
        using var data = QrBuilder.CreateData(text, level);
        var decoded = Decode(data);
        Assert(decoded == text, $"round-trip failed for '{text}' at {level}: got '{decoded}'");
    }
}

// PNG 产物必须是真的 PNG，且尺寸随边长参数变化
var png = QrBuilder.Render("https://example.com", QRCodeGenerator.ECCLevel.M, 512, QrBuilder.ParseDarkColor("黑色"));
Assert(png.Length > 200 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47, "render did not produce a PNG");
var small = QrBuilder.Render("https://example.com", QRCodeGenerator.ECCLevel.M, 128, QrBuilder.ParseDarkColor("黑色"));
Assert(small.Length < png.Length, "smaller size should produce a smaller file");

// 长内容会自动升版本，仍然要能扫
var longText = new string('A', 300);
using (var data = QrBuilder.CreateData(longText, QRCodeGenerator.ECCLevel.M))
{
    Assert(data.ModuleMatrix.Count > 21, "long content should use a larger version");
    Assert(Decode(data) == longText, "long content round-trip failed");
}

// 参数校验
Assert(QrBuilder.ParseLevel("H (30%)") == QRCodeGenerator.ECCLevel.H, "level parsing wrong");
try { QrBuilder.ParseLevel("bogus"); throw new Exception("bogus level should throw"); }
catch (InvalidDataException) { }
Assert(QrBuilder.ParseDarkColor("深蓝")[0] == 0x14, "colour parsing wrong");
try { QrBuilder.ParseDarkColor("bogus"); throw new Exception("bogus colour should throw"); }
catch (InvalidDataException) { }
try { QrBuilder.CreateData("   ", QRCodeGenerator.ECCLevel.M); throw new Exception("empty content should throw"); }
catch (InvalidDataException) { }
try { QrBuilder.CreateData(new string('A', 1300), QRCodeGenerator.ECCLevel.M); throw new Exception("oversized content should throw"); }
catch (InvalidDataException) { }

Console.WriteLine("PASS qr-code unit tests");
