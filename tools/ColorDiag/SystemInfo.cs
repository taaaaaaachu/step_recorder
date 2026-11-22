using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace StepRecorder.ColorDiag;

/// <summary>原因の切り分けに要る環境の情報。PC 名・ユーザー名は集めない。</summary>
static class SystemInfo
{
    public static IEnumerable<string> Windows()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        yield return $"Windows {key?.GetValue("DisplayVersion")} (build {Environment.OSVersion.Version.Build}.{key?.GetValue("UBR")}) {key?.GetValue("EditionID")}";
    }

    public static IEnumerable<string> GpusAndMonitors()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                var desc = adapter.Description1;
                var driver = adapter.CheckInterfaceSupport(typeof(IDXGIDevice).GUID, out long v).Success
                    ? $"{v >> 48}.{v >> 32 & 0xFFFF}.{v >> 16 & 0xFFFF}.{v & 0xFFFF}"
                    : "不明";
                yield return $"GPU {a}: {desc.Description} (vendor {desc.VendorId:X4}, device {desc.DeviceId:X4}) ドライバー {driver}";
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    using (var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>())
                    {
                        if (output6 == null) continue;
                        var d = output6.Description1;
                        var hdr = d.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020 ? "オン" : "オフ";
                        yield return $"  モニター {o}: {d.DesktopCoordinates.Right - d.DesktopCoordinates.Left}x{d.DesktopCoordinates.Bottom - d.DesktopCoordinates.Top}, {d.BitsPerColor}bit, {d.ColorSpace}, HDR {hdr}, 最大 {d.MaxLuminance:0}nit";
                    }
                }
            }
        }
    }

    /// <summary>登録されている変換部品・エンコーダー・デコーダーと、その DLL。</summary>
    public static IEnumerable<string> Transforms()
    {
        yield return $"Color Converter DSP: {Server(MediaProbe.ColorConverter)}";
        yield return $"Video Processor MFT: {Server(MediaProbe.VideoProcessor)}";
        var categories = new[]
        {
            ("映像処理", TransformCategoryGuids.VideoProcessor),
            ("映像効果", TransformCategoryGuids.VideoEffect),
            ("エンコーダー", TransformCategoryGuids.VideoEncoder),
            ("デコーダー", TransformCategoryGuids.VideoDecoder),
        };
        const uint all = 0x3F; // 同期・非同期・ハードウェア・フィールド使用・ローカル・トランスコード専用
        foreach (var (label, category) in categories)
        {
            using var found = MediaFactory.MFTEnumEx(category, all, null, null);
            foreach (var mft in found)
            {
                string? name;
                string server;
                try { name = mft.GetAllocatedString(TransformAttributeKeys.MftFriendlyNameAttribute); }
                catch (Exception) { name = "(名前なし)"; }
                try { server = Server(mft.GetGUID(TransformAttributeKeys.MftTransformClsidAttribute)); }
                catch (Exception) { server = "(CLSID なし)"; }
                yield return $"[{label}] {name}: {server}";
            }
        }
        // 標準より優先して使う部品の指定(コーデックパックなどが書き換える)
        using var preferred = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Media Foundation\Transforms\Preferred");
        foreach (var name in preferred?.GetValueNames() ?? [])
            yield return $"[優先指定] {name} = {preferred!.GetValue(name)}";
    }

    static string Server(Guid clsid)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid:B}\InprocServer32");
        return key?.GetValue(null) is string path ? Describe(Environment.ExpandEnvironmentVariables(path.Trim('"'))) : $"{clsid:B}";
    }

    static string Describe(string path)
    {
        var shown = Anonymize(path);
        if (!Path.IsPathRooted(path)) path = Path.Combine(Environment.SystemDirectory, path);
        if (!File.Exists(path)) return shown;
        var v = FileVersionInfo.GetVersionInfo(path);
        return $"{shown} ({v.CompanyName}, {v.FileVersion})";
    }

    public static HashSet<string> LoadedModules()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule m in process.Modules)
            if (m.FileName is { } path) paths.Add(path);
        return paths;
    }

    public static IEnumerable<string> Modules(IEnumerable<string> paths) => paths.Order(StringComparer.OrdinalIgnoreCase).Select(Describe);

    public static string Anonymize(string text) =>
        text.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
}
