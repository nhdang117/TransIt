using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using Sdcb.PaddleDetection;

// Long-lived worker, launched as a child process by TransIt.Core.LayoutService.
// Runs in its own process so its Paddle/MKL native DLLs (mkldnn.dll, mklml.dll,
// libiomp5md.dll) never share a module table with the OCR engine's same-named
// but differently-built copies of those DLLs - the two can't coexist in one process.
// Protocol on stdin/stdout: each message is a 4-byte little-endian length prefix
// followed by that many bytes. Request body = PNG-encoded image. Response body =
// UTF8 JSON: {"ok":true,"regions":[{"category":"Title","x":..,"y":..,"w":..,"h":..,"confidence":..}]}
// or {"ok":false,"error":"..."}. EOF/short read on stdin ends the process.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: TransIt.LayoutWorker.exe <modelDir>");
    return 1;
}

string modelDir = args[0];
string cfgPath = Path.Combine(modelDir, "infer_cfg.yml");

PreloadNativeDependencies();

PaddleDetector detector;
try
{
    detector = new PaddleDetector(modelDir, cfgPath, cfg => cfg.MkldnnEnabled = true);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"failed to construct PaddleDetector: {ex}");
    return 1;
}

const float minConfidence = 0.5f;
using Stream stdin = Console.OpenStandardInput();
using Stream stdout = Console.OpenStandardOutput();

while (true)
{
    byte[]? requestBytes = ReadFrame(stdin);
    if (requestBytes is null) break;

    string responseJson;
    try
    {
        using var mat = Cv2.ImDecode(requestBytes, ImreadModes.Color);
        DetectionResult[] results = detector.Run(mat);

        var regions = results
            .Where(r => r.Confidence >= minConfidence)
            .Select(r => new RegionDto(
                MapCategory(r.LabelName), r.Rect.X, r.Rect.Y, r.Rect.Width, r.Rect.Height, r.Confidence))
            .ToList();

        responseJson = JsonSerializer.Serialize(new ResponseDto(true, regions, null));
    }
    catch (Exception ex)
    {
        responseJson = JsonSerializer.Serialize(new ResponseDto(false, null, ex.Message));
    }

    WriteFrame(stdout, Encoding.UTF8.GetBytes(responseJson));
}

return 0;

static string MapCategory(string labelName) => labelName.ToLowerInvariant() switch
{
    "title" => "Title",
    "list" => "List",
    "table" => "Table",
    "figure" => "Figure",
    _ => "Text",
};

// See LayoutService.PreloadNativeDependencies for why this is necessary: paddle_inference_c.dll's
// transitive deps live next to it in runtimes\win-x64\native, but Windows' default DLL search
// order doesn't search that folder for them - they must already be mapped into the process by
// absolute path before paddle_inference_c.dll itself is loaded.
static void PreloadNativeDependencies()
{
    string nativeDir = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
    string[] order =
    {
        "libiomp5md.dll",
        "mklml.dll",
        "onnxruntime.dll",
        "onnxruntime_providers_shared.dll",
        "paddle2onnx.dll",
        "openblas.dll",
        "mkldnn.dll",
    };

    foreach (string name in order)
    {
        NativeLibrary.Load(Path.Combine(nativeDir, name));
    }
}

static byte[]? ReadFrame(Stream stream)
{
    Span<byte> lenBuf = stackalloc byte[4];
    if (!ReadExact(stream, lenBuf)) return null;

    int length = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
    if (length < 0) return null;

    byte[] buf = new byte[length];
    if (!ReadExact(stream, buf)) return null;
    return buf;
}

static bool ReadExact(Stream stream, Span<byte> buffer)
{
    int total = 0;
    while (total < buffer.Length)
    {
        int read = stream.Read(buffer[total..]);
        if (read == 0) return false;
        total += read;
    }
    return true;
}

static void WriteFrame(Stream stream, byte[] payload)
{
    Span<byte> lenBuf = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(lenBuf, payload.Length);
    stream.Write(lenBuf);
    stream.Write(payload);
    stream.Flush();
}

record RegionDto(string category, int x, int y, int w, int h, float confidence);
record ResponseDto(bool ok, List<RegionDto>? regions, string? error);
