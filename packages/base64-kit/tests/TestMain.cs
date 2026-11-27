using System.IO;
using System.Text;
using Base64Kit;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

var root = Path.Combine(Path.GetTempPath(), "rt-b64-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var png = Path.Combine(root, "icon.png");
    var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF };
    File.WriteAllBytes(png, payload);

    var dataUri = Kit.EncodeFile(png, Kit.DataUri);
    Assert(dataUri.StartsWith("data:image/png;base64,", StringComparison.Ordinal), "data uri prefix wrong: " + dataUri);
    var plain = Kit.EncodeFile(png, Kit.PlainBase64);
    Assert(!plain.StartsWith("data:", StringComparison.Ordinal), "plain mode must not add a prefix");
    Assert(plain == Convert.ToBase64String(payload), "plain base64 mismatch");
    Assert(Kit.MimeOf(png) == "image/png", "mime lookup wrong");
    Assert(Kit.ExtensionForMime("image/png") == ".png", "mime reverse lookup wrong");
    Assert(Kit.MimeOf(Path.Combine(root, "x.unknownext")) == "application/octet-stream", "unknown mime should be octet-stream");

    var (decoded, extension) = Kit.Decode(dataUri);
    Assert(decoded.SequenceEqual(payload), "data uri round-trip failed");
    Assert(extension == ".png", "extension guess wrong");

    var (fromPlain, plainExtension) = Kit.Decode(plain);
    Assert(fromPlain.SequenceEqual(payload) && plainExtension == "", "plain decode should not guess an extension");

    // 换行与空白必须被忽略
    var wrapped = "  " + string.Join("\n", Convert.ToBase64String(payload).Chunk(4).Select(chars => new string(chars))) + "  ";
    Assert(Kit.Decode(wrapped).Bytes.SequenceEqual(payload), "whitespace should be ignored");

    Assert(Kit.SafeExtension("jpg") == ".jpg" && Kit.SafeExtension(".jpg") == ".jpg", "safe extension normalisation wrong");
    Assert(Kit.SafeExtension("../x") == "" && Kit.SafeExtension("") == "", "unsafe extension must be rejected");

    try { Kit.Decode("   "); throw new Exception("empty payload should throw"); }
    catch (InvalidDataException) { }
    try { Kit.Decode("!!!!"); throw new Exception("invalid base64 should throw"); }
    catch (InvalidDataException) { }
    try { Kit.EncodeFile(Path.Combine(root, "missing.bin"), Kit.DataUri); throw new Exception("missing file should throw"); }
    catch (FileNotFoundException) { }

    Console.WriteLine("PASS base64-kit unit tests");
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { }
}
