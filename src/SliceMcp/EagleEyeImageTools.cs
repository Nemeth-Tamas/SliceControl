using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenCvSharp;

namespace SliceMcp;

/// <summary>On-demand, memory-only USB camera snapshots for the existing Slice MCP service.</summary>
[McpServerToolType]
public sealed class EagleEyeImageTools
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    [McpServerTool]
    [Description("Take one fresh JPEG photograph from the EagleEye IV USB connected directly to this Windows Slice and return actual image content to ECHO. Capture is on demand only, never background recording. Can fail if another app owns the camera.")]
    public async Task<IEnumerable<ContentBlock>> eagleeye_snapshot()
    {
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(15)))
            return [new TextContentBlock { Text = "EagleEye capture busy; retry." }];
        try
        {
            // The DirectShow enumeration order normally corresponds to CAP_DSHOW camera indices.
            // Refuse to guess if there is no uniquely named EagleEye device.
            var devices = FindVideoDevices();
            var matches = devices.Select((name, index) => new { name, index })
                .Where(x => x.name.Contains("EagleEye", StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
                return [new TextContentBlock { Text = matches.Count == 0
                    ? "EagleEye camera not found in DirectShow video inputs."
                    : "Several EagleEye cameras detected; cannot safely choose a capture device." }];

            var selected = matches[0];
            using var capture = new VideoCapture(selected.index, VideoCaptureAPIs.DSHOW);
            if (!capture.IsOpened())
                return [new TextContentBlock { Text = "Unable to open " + selected.name + ". Close the browser/OBS/other camera applications and retry." }];
            capture.Set(VideoCaptureProperties.FrameWidth, 1280);
            capture.Set(VideoCaptureProperties.FrameHeight, 720);
            using var frame = new Mat();
            bool gotFrame = false;
            for (int i = 0; i < 12; i++)
            {
                if (capture.Read(frame) && !frame.Empty()) { gotFrame = true; if (i >= 2) break; }
                Thread.Sleep(80);
            }
            if (!gotFrame || frame.Empty())
                return [new TextContentBlock { Text = "Camera opened but returned no video frame." }];

            // Bound the payload before returning it to a vision-capable MCP client.
            if (frame.Width > 1280 || frame.Height > 960)
            {
                double ratio = Math.Min(1280d / frame.Width, 960d / frame.Height);
                using var scaled = new Mat();
                Cv2.Resize(frame, scaled, new OpenCvSharp.Size(
                    Math.Max(1, (int)(frame.Width * ratio)),
                    Math.Max(1, (int)(frame.Height * ratio))));
                Cv2.ImEncode(".jpg", scaled, out byte[] encoded, new ImageEncodingParam(ImwriteFlags.JpegQuality, 82));
                return ToBlocks(selected.name, scaled.Width, scaled.Height, encoded);
            }
            Cv2.ImEncode(".jpg", frame, out byte[] jpg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 82));
            return ToBlocks(selected.name, frame.Width, frame.Height, jpg);
        }
        catch (Exception error)
        {
            return [new TextContentBlock { Text = "EagleEye snapshot failed: " + error.Message }];
        }
        finally { Gate.Release(); }
    }

    private static IEnumerable<ContentBlock> ToBlocks(string name, int width, int height, byte[] jpg)
    {
        if (jpg.Length == 0 || jpg.Length > 5_000_000)
            return [new TextContentBlock { Text = "Snapshot JPEG is empty or exceeds the 5 MB limit." }];
        return [
            new TextContentBlock { Text = $"Fresh USB image from {name}; {width}x{height}; {jpg.Length} bytes. Camera handle released after capture." },
            ImageContentBlock.FromBytes(jpg, "image/jpeg")
        ];
    }

    private static List<string> FindVideoDevices()
    {
        object? devObject = null, enumObject = null;
        var names = new List<string>();
        try
        {
            devObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"))!);
            var category = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
            int hr = ((ICreateDevEnum)devObject!).CreateClassEnumerator(ref category, out var devices, 0);
            if (hr != 0 || devices == null) return names;
            enumObject = devices;
            var entries = new IMoniker[1];
            while (devices.Next(1, entries, IntPtr.Zero) == 0)
            {
                var entry = entries[0];
                object? bag = null;
                try
                {
                    var iid = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");
                    entry.BindToStorage(null, null, ref iid, out bag);
                    int code = ((IPropertyBag)bag!).Read("FriendlyName", out object value, IntPtr.Zero);
                    names.Add(code == 0 ? value?.ToString() ?? "" : "");
                }
                catch { names.Add(""); }
                finally
                {
                    if (bag != null) Marshal.ReleaseComObject(bag);
                    Marshal.ReleaseComObject(entry);
                }
            }
            return names;
        }
        finally
        {
            if (enumObject != null) Marshal.ReleaseComObject(enumObject);
            if (devObject != null) Marshal.ReleaseComObject(devObject);
        }
    }

    [ComImport, Guid("B6C292BC-7C88-11D0-8D18-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator(ref Guid category, out IEnumMoniker? result, int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string key, [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr log);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string key, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
