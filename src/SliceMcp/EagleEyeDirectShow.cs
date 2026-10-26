using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Diagnostics;

namespace SliceMcp;

internal sealed record CameraAxis(int Id, bool Supported, bool Manual, int Min, int Max,
    int ReportedStep, int Step, int DefaultValue, int? Value, int? ReportedValue,
    string ValueSource, double? UncertaintyDegrees, int Flags, string? Error);

/// <summary>Owns every DirectShow COM reference for one uniquely selected EagleEye.</summary>
internal sealed class EagleEyeDirectShow : IDisposable
{
    private object? filter;
    private IAMCameraControl? control;
    private readonly Dictionary<string, (int Target, long Timestamp)> commanded = new();
    public string Name { get; }
    public int Index { get; }
    public static readonly IReadOnlyDictionary<string, int> AxisIds = new Dictionary<string, int>
        { ["pan"] = 0, ["tilt"] = 1, ["zoom"] = 3, ["focus"] = 6 };

    private EagleEyeDirectShow(string name, int index, object filter)
    {
        Name = name; Index = index; this.filter = filter;
        control = filter as IAMCameraControl ?? throw new InvalidOperationException("EagleEye does not expose IAMCameraControl.");
    }

    public static EagleEyeDirectShow Open()
    {
        object? enumerator = null;
        IEnumMoniker? devices = null;
        var matches = new List<(string Name, int Index, IMoniker Moniker)>();
        try
        {
            enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"))!);
            var category = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
            int hr = ((ICreateDevEnum)enumerator!).CreateClassEnumerator(ref category, out devices, 0);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (devices is null) throw new InvalidOperationException("No DirectShow video inputs found.");
            var entries = new IMoniker[1];
            int index = 0;
            while (devices.Next(1, entries, IntPtr.Zero) == 0)
            {
                var moniker = entries[0]; object? bag = null; bool keep = false;
                try
                {
                    var iid = typeof(IPropertyBag).GUID;
                    moniker.BindToStorage(null!, null, ref iid, out bag);
                    hr = ((IPropertyBag)bag).Read("FriendlyName", out object value, IntPtr.Zero);
                    string name = hr >= 0 ? value?.ToString() ?? "" : "";
                    if (name.Contains("EagleEye", StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add((name, index, moniker)); keep = true;
                    }
                }
                finally
                {
                    if (bag != null) Marshal.ReleaseComObject(bag);
                    if (!keep) Marshal.ReleaseComObject(moniker);
                }
                index++;
            }
            if (matches.Count != 1) throw new InvalidOperationException(matches.Count == 0
                ? "No EagleEye camera found." : "Multiple EagleEye cameras found; refusing ambiguous selection.");
            var selected = matches[0];
            var filterId = new Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770"); // IBaseFilter
            selected.Moniker.BindToObject(null!, null, ref filterId, out object filter);
            try { return new EagleEyeDirectShow(selected.Name, selected.Index, filter); }
            catch { Marshal.ReleaseComObject(filter); throw; }
        }
        finally
        {
            foreach (var match in matches) Marshal.ReleaseComObject(match.Moniker);
            if (devices != null) Marshal.ReleaseComObject(devices);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    public Dictionary<string, CameraAxis> ReadAxes()
    {
        var axes = new Dictionary<string, CameraAxis>();
        foreach (var (name, id) in AxisIds)
        {
            int hr = control!.GetRange(id, out int min, out int max, out int step, out int def, out int caps);
            int read = control.Get(id, out int value, out int flags);
            bool supported = hr >= 0 && read >= 0 && max >= min && step >= 0;
            int? effective = read >= 0 ? value : null;
            string source = "driver";
            // This EagleEye truncates near-integer degree readback (e.g. Set(4), Get()=3).
            // Preserve a known command only within that measured one-degree uncertainty;
            // otherwise accept the driver's new position, including external/IR movement.
            if (supported && step == 0 && (name is "pan" or "tilt") && commanded.TryGetValue(name, out var command))
            {
                if (Math.Abs((long)value - command.Target) <= 1) { effective = command.Target; source = "last_command_within_one_degree_of_driver"; }
                else if (Stopwatch.GetElapsedTime(command.Timestamp) > TimeSpan.FromSeconds(15)) commanded.Remove(name);
            }
            axes[name] = new CameraAxis(id, supported, (caps & 2) != 0, min, max, step,
                Math.Max(1, step), def, effective, read >= 0 ? value : null, source,
                (name is "pan" or "tilt") && step == 0 ? 1 : null, flags,
                supported ? null : $"GetRange=0x{hr:X8}; Get=0x{read:X8}");
        }
        return axes;
    }

    public void Set(string axis, int target)
    {
        Marshal.ThrowExceptionForHR(control!.Set(AxisIds[axis], target, 2));
        commanded[axis] = (target, Stopwatch.GetTimestamp());
    }

    public void Dispose()
    {
        control = null;
        if (filter != null) { Marshal.ReleaseComObject(filter); filter = null; }
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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

    [ComImport, Guid("C6E13370-30AC-11D0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMCameraControl
    {
        [PreserveSig] int GetRange(int property, out int min, out int max, out int step, out int defaultValue, out int flags);
        [PreserveSig] int Set(int property, int value, int flags);
        [PreserveSig] int Get(int property, out int value, out int flags);
    }
}
