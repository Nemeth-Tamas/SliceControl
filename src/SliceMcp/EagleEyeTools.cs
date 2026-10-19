using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SliceMcp;

[McpServerToolType]
public sealed class EagleEyeTools
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, int> Axes = new() { ["pan"] = 0, ["tilt"] = 1, ["zoom"] = 3 };
    [McpServerTool, Description("Read local Polycom EagleEye IV USB pan/tilt/zoom support, limits and current positions without moving motors.")]
    public Task<string> eagleeye_status() => Run(null, 0);

    [McpServerTool, Description("Move locally attached EagleEye IV USB on a single axis by a small bounded number of hardware steps. axis: pan, tilt, zoom. steps: -5 through 5.")]
    public Task<string> eagleeye_nudge(string axis, int steps)
    {
        if (!Axes.ContainsKey(axis) || steps < -5 || steps > 5)
            return Task.FromResult("{\"error\":\"Use pan/tilt/zoom and steps -5..5\"}");
        return Run(axis, steps);
    }

    [McpServerTool, Description("Center local EagleEye USB pan and tilt at their reported range midpoint, not a stored camera preset.")]
    public Task<string> eagleeye_center() => Run("center", 0);

    private static async Task<string> Run(string? axis, int steps)
    {
        await Gate.WaitAsync();
        try
        {
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { result.TrySetResult(Operate(axis, steps)); }
                catch (Exception e) { result.TrySetResult(JsonSerializer.Serialize(new { error = e.Message })); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await result.Task;
        }
        finally { Gate.Release(); }
    }

    private static string Operate(string? axis, int steps)
    {
        object? devObject = null, listObject = null, filter = null;
        IMoniker? camera = null;
        try
        {
            devObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"))!);
            var category = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
            int hr = ((ICreateDevEnum)devObject!).CreateClassEnumerator(ref category, out var devices, 0);
            if (hr != 0 || devices is null) return "{\"error\":\"No DirectShow video devices\"}";
            listObject = devices;
            var matches = new List<(string, IMoniker)>();
            var entries = new IMoniker[1];
            while (devices.Next(1, entries, IntPtr.Zero) == 0)
            {
                var entry = entries[0]; object? bag = null;
                try
                {
                    var iid = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");
                    entry.BindToStorage(null, null, ref iid, out bag);
                    var code = ((IPropertyBag)bag).Read("FriendlyName", out object value, IntPtr.Zero);
                    var name = code == 0 ? value?.ToString() ?? "" : "";
                    if (name.Contains("EagleEye", StringComparison.OrdinalIgnoreCase))
                        matches.Add((name, entry));
                    else Marshal.ReleaseComObject(entry);
                }
                catch { Marshal.ReleaseComObject(entry); }
                finally { if (bag != null) Marshal.ReleaseComObject(bag); }
            }
            if (matches.Count != 1)
            {
                foreach (var m in matches) Marshal.ReleaseComObject(m.Item2);
                return JsonSerializer.Serialize(new { error = matches.Count == 0 ? "No EagleEye USB camera found" : "Multiple EagleEye cameras; refusing ambiguous command", devices = matches.Select(m=>m.Item1) });
            }
            camera = matches[0].Item2;
            var controlId = typeof(IAMCameraControl).GUID;
            camera.BindToObject(null, null, ref controlId, out filter);
            if (filter is not IAMCameraControl control)
                return "{\"error\":\"Camera filter does not expose IAMCameraControl\"}";
            var props = new Dictionary<string, object>();
            var supported = new Dictionary<string, (int min, int max, int step, int value)>();
            foreach (var (name, id) in Axes)
            {
                hr = control.GetRange(id, out var min, out var max, out var step, out var def, out var caps);
                if (hr < 0 || step <= 0 || max < min)
                {
                    props[name] = new { supported = false, hresult = $"0x{hr:X8}" };
                    continue;
                }
                var read = control.Get(id, out var value, out var flags);
                props[name] = new { supported = true, min, max, step, default_value = def, capabilities = caps, value = read >= 0 ? (int?)value : null };
                if (read >= 0) supported[name] = (min, max, step, value);
            }
            string? requested = null;
            if (axis == "center")
            {
                if (!supported.ContainsKey("pan") || !supported.ContainsKey("tilt"))
                    return JsonSerializer.Serialize(new { error = "Pan and tilt must both be supported for center", properties = props });
                foreach (var name in new[] { "pan", "tilt" })
                {
                    var r = supported[name];
                    int target = r.min + ((r.max - r.min) / (2 * r.step)) * r.step;
                    Marshal.ThrowExceptionForHR(control.Set(Axes[name], target, 2));
                }
                requested = "pan/tilt midpoint";
            }
            else if (axis != null)
            {
                if (!supported.TryGetValue(axis, out var r))
                    return JsonSerializer.Serialize(new { error = "Axis unavailable or unreadable", axis, properties = props });
                int target = (int)Math.Clamp((long)r.value + (long)steps * r.step, r.min, r.max);
                Marshal.ThrowExceptionForHR(control.Set(Axes[axis], target, 2));
                requested = $"{axis} {r.value} -> {target}";
            }
            var readback = new Dictionary<string, int?>();
            foreach (var (name, id) in Axes)
                if (supported.ContainsKey(name))
                    readback[name] = control.Get(id, out var val, out _) >= 0 ? val : null;
            return JsonSerializer.Serialize(new { camera = matches[0].Item1, requested, properties = props, readback });
        }
        finally
        {
            if (filter != null) Marshal.ReleaseComObject(filter);
            if (camera != null) Marshal.ReleaseComObject(camera);
            if (listObject != null) Marshal.ReleaseComObject(listObject);
            if (devObject != null) Marshal.ReleaseComObject(devObject);
        }
    }

    [ComImport, Guid("B6C292BC-7C88-11D0-8D18-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum { [PreserveSig] int CreateClassEnumerator(ref Guid category, out IEnumMoniker? result, int flags); }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string key, [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr log);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string key, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    [ComImport, Guid("C6E13370-30AC-11D0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMCameraControl
    {
        [PreserveSig] int GetRange(int property, out int min, out int max, out int step, out int defaultValue, out int flags);
        [PreserveSig] int Set(int property, int value, int flags);
        [PreserveSig] int Get(int property, out int value, out int flags);
    }
}
