using System.Management;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

object Try(Func<object> fn, out string err)
{
    err = null;
    try { return fn(); }
    catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; return null; }
}

ManagementObject FirstObject(string wmiClass, out string err)
{
    err = null;
    try
    {
        using var searcher = new ManagementObjectSearcher("root\\WMI", $"SELECT * FROM {wmiClass}");
        using var collection = searcher.Get();
        foreach (ManagementObject mo in collection) return mo;
        err = "no instances";
        return null;
    }
    catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; return null; }
}

(string value, string error) Call(string wmiClass, string method, Dictionary<string, object> args = null)
{
    var obj = FirstObject(wmiClass, out var findErr);
    if (obj == null) return (null, "查不到类: " + findErr);
    try
    {
        using var inParams = obj.GetMethodParameters(method);
        if (args != null)
            foreach (var kv in args) inParams[kv.Key] = kv.Value;
        using var outParams = obj.InvokeMethod(method, inParams, new InvokeMethodOptions());
        if (outParams == null) return ("<null outParams>", null);
        var sb = new StringBuilder();
        foreach (PropertyData p in outParams.Properties)
        {
            var v = p.Value;
            string text = v switch
            {
                null => "null",
                Array a => "[" + string.Join(",", a.Cast<object>().Select(x => x?.ToString())) + "]",
                _ => v.ToString()
            };
            sb.Append(p.Name).Append('=').Append(text).Append(' ');
        }
        return (sb.ToString().Trim(), null);
    }
    catch (Exception ex)
    {
        return (null, ex.GetType().Name + ": " + ex.Message);
    }
}

Console.WriteLine("=== LENOVO_GAMEZONE_DATA ===");
string[] getters =
{
    "GetFanCount", "GetFan1Speed", "GetFan2Speed", "GetFanMaxSpeed", "GetCPUTemp", "GetGPUTemp", "GetIRTemp",
    "GetCpuFrequency", "GetThermalMode", "GetSmartFanMode", "GetSmartFanSetting", "IsSupportSmartFan",
    "IsSupportFanCooling", "GetFanCoolingStatus", "GetIntelligentSubMode", "GetProductInfo", "GetVersion",
    "GetThermalTableID", "GetPowerChargeMode", "GetHardwareInfoSupportVersion"
};
foreach (var g in getters)
{
    var (value, error) = Call("LENOVO_GAMEZONE_DATA", g);
    Console.WriteLine(error == null ? $"  {g,-32} -> {value}" : $"  {g,-32} -> FAIL {error}");
}

Console.WriteLine();
Console.WriteLine("=== LENOVO_FAN_METHOD: Fan_Get_Table ===");
for (var fan = 0; fan < 3; fan++)
{
    for (var sensor = 0; sensor < 3; sensor++)
    {
        var (value, error) = Call("LENOVO_FAN_METHOD", "Fan_Get_Table", new Dictionary<string, object>
        {
            ["FanID"] = (uint)fan,
            ["SensorID"] = (uint)sensor,
            ["FanTable"] = new byte[64],
            ["FanTableSize"] = (uint)64
        });
        Console.WriteLine(error == null
            ? $"  fan={fan} sensor={sensor} -> {value}"
            : $"  fan={fan} sensor={sensor} -> FAIL {error}");
    }
}

Console.WriteLine();
Console.WriteLine("=== LENOVO_FAN_TABLE_DATA (after table read) ===");
try
{
    using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM LENOVO_FAN_TABLE_DATA");
    using var collection = searcher.Get();
    var any = false;
    foreach (ManagementObject mo in collection)
    {
        any = true;
        var sb = new StringBuilder();
        foreach (PropertyData p in mo.Properties)
        {
            var v = p.Value;
            var text = v is Array a ? "[" + string.Join(",", a.Cast<object>().Take(24).Select(x => x?.ToString())) + $"... len={a.Length}]" : v?.ToString();
            sb.Append(p.Name).Append('=').Append(text).Append("; ");
        }
        Console.WriteLine("  " + sb);
    }
    if (!any) Console.WriteLine("  (no instances)");
}
catch (Exception ex) { Console.WriteLine("  FAIL " + ex.Message); }
