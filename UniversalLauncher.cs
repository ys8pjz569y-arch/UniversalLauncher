using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UniversalLauncher
{
    // ==================== 数据结构 ====================
    public sealed class InputMethodItem
    {
        public string Klid;
        public string Name;
        public override string ToString() { return Name + "  [" + Klid + "]"; }
    }

    public sealed class DeviceItem
    {
        public string Id;
        public string Name;
        public override string ToString() { return Name; }
    }

    public sealed class AppEntry
    {
        public string DisplayName;
        public string Path;
        public override string ToString() { return DisplayName; }
    }

    public sealed class ConfigData
    {
        public string Input = "";
        public string Capture = "";
        public string Render = "";
        public List<string> Apps = new List<string>();
        public List<string> Urls = new List<string>();
    }

    // ==================== 原生 API / COM ====================
    internal static class Native
    {
        public const uint KLF_ACTIVATE = 0x00000001;
        public const uint KLF_SUBSTITUTE_OK = 0x00000002;
        public const int WM_INPUTLANGCHANGEREQUEST = 0x0050;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint Flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern bool AllocConsole();

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PropVariant pvar);
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;
        [FieldOffset(8)] public IntPtr pVal;   // VT_LPWSTR
        [FieldOffset(8)] public uint ulVal;    // VT_UI4
        [FieldOffset(8)] public int lVal;      // VT_I4
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MmDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] object pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] object pClient);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    internal class CComPolicyConfig { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, int bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr pMixFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, int mode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, int cbPropId, IntPtr pKey, IntPtr pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, int cbPropId, IntPtr pKey, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint(string pszDeviceName, int role);
        [PreserveSig] int SetEndpointVisibility(string pszDeviceName, int bVisible);
    }

    // ==================== 枚举器 ====================
    internal static class Enumerator
    {
        private const int EStateMaskActive = 0x01; // 只列当前可用的设备

        private static readonly Dictionary<string, string> CommonLayoutNames = new Dictionary<string, string>
        {
            { "00000409", "英语(美国) · 美式键盘" },
            { "00000804", "简体中文(中国)" },
            { "00000809", "英语(英国)" },
            { "00000411", "日语" },
            { "00000412", "韩语" },
            { "00000407", "德语" },
            { "0000040c", "法语" },
            { "0000040a", "西班牙语" },
            { "00000419", "俄语" }
        };

        // ---------- 输入法：读用户实际预加载的键盘布局 ----------
        public static List<InputMethodItem> GetInputMethods()
        {
            var list = new List<InputMethodItem>();
            try
            {
                using (var preload = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload"))
                {
                    if (preload == null) return list;
                    var keys = new List<string>();
                    foreach (string v in preload.GetValueNames())
                    {
                        int n;
                        if (int.TryParse(v, out n)) keys.Add(v);
                    }
                    keys.Sort((a, b) => int.Parse(a).CompareTo(int.Parse(b)));
                    foreach (string v in keys)
                    {
                        string klid = preload.GetValue(v) as string;
                        if (string.IsNullOrEmpty(klid)) continue;
                        list.Add(new InputMethodItem { Klid = klid, Name = GetLayoutDisplayName(klid) });
                    }
                }
            }
            catch { }
            return list;
        }

        private static string GetLayoutDisplayName(string klid)
        {
            string known;
            if (CommonLayoutNames.TryGetValue(klid.ToLowerInvariant(), out known)) return known;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\" + klid))
                {
                    if (k != null)
                    {
                        string lt = k.GetValue("Layout Text") as string;
                        if (!string.IsNullOrEmpty(lt) && !lt.StartsWith("@")) return lt;
                    }
                }
            }
            catch { }
            return klid;
        }

        // ---------- 音频设备：dataFlow=0 输出，dataFlow=1 输入 ----------
        public static List<DeviceItem> GetAudioDevices(int dataFlow)
        {
            var list = new List<DeviceItem>();
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MmDeviceEnumeratorComObject();
                IMMDeviceCollection devices;
                int hr = enumerator.EnumAudioEndpoints(dataFlow, EStateMaskActive, out devices);
                if (hr < 0 || devices == null) return list;
                uint count;
                devices.GetCount(out count);
                try
                {
                    for (uint i = 0; i < count; i++)
                    {
                        IMMDevice dev;
                        if (devices.Item(i, out dev) < 0 || dev == null) continue;
                        try
                        {
                            string id;
                            dev.GetId(out id);
                            string name = GetDeviceFriendlyName(dev);
                            if (!string.IsNullOrEmpty(id))
                                list.Add(new DeviceItem { Id = id, Name = string.IsNullOrEmpty(name) ? id : name });
                        }
                        finally { Marshal.ReleaseComObject(dev); }
                    }
                }
                finally { Marshal.ReleaseComObject(devices); }
            }
            catch { }
            return list;
        }

        private static string GetDeviceFriendlyName(IMMDevice device)
        {
            IPropertyStore store;
            if (device.OpenPropertyStore(0, out store) < 0 || store == null) return null;
            try
            {
                var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                PropVariant pv;
                int hr = store.GetValue(ref key, out pv);
                if (hr < 0) return null;
                try
                {
                    if (pv.vt == 31) return Marshal.PtrToStringUni(pv.pVal);
                    return null;
                }
                finally { Native.PropVariantClear(ref pv); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        // ---------- 已安装应用：开始菜单 .lnk + 卸载注册表 ----------
        public static List<AppEntry> GetInstalledApps()
        {
            var map = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            string[] startDirs =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
            };
            foreach (string dir in startDirs)
            {
                try
                {
                    foreach (string lnk in Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories))
                    {
                        string target = ResolveLnkTarget(lnk);
                        if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            || !File.Exists(target) || IsNoiseApp(target) || map.ContainsKey(target))
                            continue;
                        // 用开始菜单快捷方式的名字做显示名（如“微信”）
                        map[target] = new AppEntry { DisplayName = Path.GetFileNameWithoutExtension(lnk), Path = target };
                    }
                }
                catch { }
            }
            CollectFromUninstall(map);

            var list = new List<AppEntry>(map.Values);
            list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName));
            return list;
        }

        private static string ResolveLnkTarget(string lnkPath)
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                return (string)shortcut.TargetPath;
            }
            catch { return null; }
        }

        // 过滤明显的安装器 / 修复工具 / 系统诊断程序，避免下拉列表全是噪音
        private static bool IsNoiseApp(string path)
        {
            string p = path.ToLowerInvariant();
            if (p.Contains(@"\package cache\") || p.Contains(@"\windows\installer\")
                || p.Contains(@"\windows\winsxs\") || p.Contains(@"\windows\servicing\")
                || p.Contains(@"\windows\syswow64\config\"))
                return true;
            string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            string[] noise = { "setup", "install", "unins", "upgrade", "migration", "repair",
                               "bugreport", "crashhandler", "appcertui", "appverif" };
            foreach (string n in noise)
                if (name.Contains(n)) return true;
            return false;
        }

        private static void CollectFromUninstall(Dictionary<string, AppEntry> map)
        {
            var roots = new[] { Registry.LocalMachine, Registry.CurrentUser };
            string[] subs =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (var root in roots)
                foreach (string sub in subs)
                {
                    if (root == Registry.CurrentUser && sub.Contains("WOW6432Node")) continue;
                    try
                    {
                        using (var key = root.OpenSubKey(sub))
                        {
                            if (key == null) continue;
                            foreach (string name in key.GetSubKeyNames())
                            {
                                try
                                {
                                    using (var sk = key.OpenSubKey(name))
                                    {
                                        if (sk == null) continue;
                                        string displayName = sk.GetValue("DisplayName") as string;
                                        string icon = sk.GetValue("DisplayIcon") as string;
                                        if (string.IsNullOrEmpty(icon)) continue;
                                        string path = icon;
                                        int comma = path.IndexOf(',');
                                        if (comma > 0) path = path.Substring(0, comma);
                                        path = path.Trim().Trim('"');
                                        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                            && File.Exists(path) && !IsNoiseApp(path) && !map.ContainsKey(path))
                                        {
                                            string appName = string.IsNullOrEmpty(displayName)
                                                ? Path.GetFileNameWithoutExtension(path)
                                                : displayName;
                                            map[path] = new AppEntry { DisplayName = appName, Path = path };
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
        }
    }

    // ==================== 执行器 ====================
    internal static class Switcher
    {
        public static string SwitchInput(string klid)
        {
            try
            {
                IntPtr hkl = Native.LoadKeyboardLayout(klid, Native.KLF_ACTIVATE | Native.KLF_SUBSTITUTE_OK);
                if (hkl == IntPtr.Zero) return "加载布局失败";
                IntPtr hwnd = Native.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return "无法获取前台窗口";
                if (!Native.PostMessage(hwnd, Native.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl))
                    return "发送切换消息失败";
                return "OK";
            }
            catch (Exception ex) { return "异常: " + ex.Message; }
        }

        public static string SetDefaultDevice(string deviceId)
        {
            try
            {
                var policy = (IPolicyConfig)new CComPolicyConfig();
                int okCount = 0, lastHr = 0;
                for (int role = 0; role <= 2; role++) // 0=控制台 1=多媒体 2=通信
                {
                    lastHr = policy.SetDefaultEndpoint(deviceId, role);
                    if (lastHr >= 0) okCount++;
                }
                if (okCount == 0) return "HRESULT=0x" + lastHr.ToString("X8");
                return "OK";
            }
            catch (Exception ex) { return "异常: " + ex.Message; }
        }

        public static string LaunchApp(string path)
        {
            try
            {
                var psi = new ProcessStartInfo(path)
                {
                    WorkingDirectory = Path.GetDirectoryName(path),
                    UseShellExecute = true
                };
                Process.Start(psi);
                return "OK";
            }
            catch (Exception ex) { return "失败: " + ex.Message; }
        }

        // ---------- 用系统默认浏览器打开网址 ----------
        public static string OpenUrl(string url)
        {
            try
            {
                var psi = new ProcessStartInfo(url) { UseShellExecute = true };
                Process.Start(psi);
                return "OK";
            }
            catch (Exception ex) { return "失败: " + ex.Message; }
        }

        // ---------- 核心流程：输入法 → 声音输出 → 声音输入 → 各应用 → 各网址 ----------
        public static List<string> ExecuteFlow(ConfigData cfg)
        {
            var results = new List<string>();

            if (!string.IsNullOrEmpty(cfg.Input))
            {
                string r = SwitchInput(cfg.Input);
                results.Add("输入法 → " + ResolveInputName(cfg.Input) + "：" + (r == "OK" ? "成功" : r));
            }
            else results.Add("输入法：未切换");

            if (!string.IsNullOrEmpty(cfg.Render))
            {
                string r = SetDefaultDevice(cfg.Render);
                results.Add("声音输出 → " + ResolveDeviceName(0, cfg.Render) + "：" + (r == "OK" ? "成功" : r));
            }
            else results.Add("声音输出：未切换");

            if (!string.IsNullOrEmpty(cfg.Capture))
            {
                string r = SetDefaultDevice(cfg.Capture);
                results.Add("声音输入 → " + ResolveDeviceName(1, cfg.Capture) + "：" + (r == "OK" ? "成功" : r));
            }
            else results.Add("声音输入：未切换");

            for (int i = 0; i < cfg.Apps.Count; i++)
            {
                string path = cfg.Apps[i];
                if (string.IsNullOrEmpty(path)) { results.Add("应用" + (i + 1) + "：未选择"); continue; }
                if (!File.Exists(path)) { results.Add("应用" + (i + 1) + "：路径不存在 " + path); continue; }
                string r = LaunchApp(path);
                results.Add("应用" + (i + 1) + " → " + path + "：" + (r == "OK" ? "已启动" : r));
            }

            for (int i = 0; i < cfg.Urls.Count; i++)
            {
                string url = cfg.Urls[i];
                if (string.IsNullOrEmpty(url)) { results.Add("网址" + (i + 1) + "：未填写"); continue; }
                if (url.IndexOf("://", StringComparison.Ordinal) < 0) url = "https://" + url;
                string r = OpenUrl(url);
                results.Add("网址" + (i + 1) + " → " + url + "：" + (r == "OK" ? "已打开" : r));
            }
            return results;
        }

        private static string ResolveInputName(string klid)
        {
            foreach (var im in Enumerator.GetInputMethods())
                if (im.Klid == klid) return im.Name;
            return klid;
        }

        private static string ResolveDeviceName(int dataFlow, string id)
        {
            foreach (var d in Enumerator.GetAudioDevices(dataFlow))
                if (d.Id == id) return d.Name;
            return id;
        }
    }

    // ==================== 配置（多方案） ====================
    internal static class Config
    {
        private const string DefaultProfileName = "默认方案";

        public static string ActivePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt"); }
        }

        public static string ProfilesPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles.txt"); }
        }

        // ---------- 当前激活的方案名（config.txt 里的 lastprofile 标记） ----------
        public static string ActiveName
        {
            get
            {
                try
                {
                    if (File.Exists(ActivePath))
                        foreach (string raw in File.ReadAllLines(ActivePath, Encoding.UTF8))
                        {
                            string line = raw.Trim();
                            int eq = line.IndexOf('=');
                            if (eq > 0 && line.Substring(0, eq).Trim().Equals("lastprofile", StringComparison.OrdinalIgnoreCase))
                            {
                                string v = line.Substring(eq + 1).Trim();
                                if (v.Length > 0) return v;
                            }
                        }
                }
                catch { }
                return DefaultProfileName;
            }
        }

        // ---------- 读取激活方案内容（config.txt；lastprofile 行会被忽略） ----------
        public static ConfigData LoadActive()
        {
            var cfg = new ConfigData();
            try
            {
                if (!File.Exists(ActivePath)) return cfg;
                foreach (string raw in File.ReadAllLines(ActivePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    ApplyLine(cfg, line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                }
            }
            catch { }
            return cfg;
        }

        // 把某方案内容写进 config.txt 并更新激活标记
        public static void SaveAsActive(ConfigData cfg, string name)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# UniversalLauncher 当前激活方案：lastprofile=方案名");
            sb.AppendLine("lastprofile=" + name);
            sb.AppendLine("input=" + cfg.Input);
            sb.AppendLine("capture=" + cfg.Capture);
            sb.AppendLine("render=" + cfg.Render);
            sb.AppendLine("apps=" + string.Join("|", cfg.Apps.ToArray()));
            sb.AppendLine("urls=" + string.Join("|", cfg.Urls.ToArray()));
            try { File.WriteAllText(ActivePath, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }

        // ---------- 方案列表（profiles.txt，[方案名] 分节） ----------
        public static List<KeyValuePair<string, ConfigData>> ReadAllProfiles()
        {
            var list = new List<KeyValuePair<string, ConfigData>>();
            try
            {
                if (!File.Exists(ProfilesPath)) return list;
                string current = null;
                ConfigData cfg = null;
                foreach (string raw in File.ReadAllLines(ProfilesPath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        if (current != null && cfg != null)
                            list.Add(new KeyValuePair<string, ConfigData>(current, cfg));
                        current = line.Substring(1, line.Length - 2).Trim();
                        cfg = new ConfigData();
                        continue;
                    }
                    if (current == null || cfg == null) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    ApplyLine(cfg, line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                }
                if (current != null && cfg != null)
                    list.Add(new KeyValuePair<string, ConfigData>(current, cfg));
            }
            catch { }
            return list;
        }

        public static List<string> GetProfileNames()
        {
            var names = new List<string>();
            foreach (var kv in ReadAllProfiles()) names.Add(kv.Key);
            return names;
        }

        public static ConfigData LoadProfile(string name)
        {
            foreach (var kv in ReadAllProfiles())
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }

        public static void SaveProfile(string name, ConfigData cfg)
        {
            var names = GetProfileNames();
            if (!names.Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) names.Add(name);
            var map = LoadProfileMap();
            map[name] = cfg;
            WriteAllProfiles(names, map);
        }

        public static void RenameProfile(string oldName, string newName)
        {
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) return;
            var names = GetProfileNames();
            var map = LoadProfileMap();
            ConfigData cfg;
            if (!map.TryGetValue(oldName, out cfg)) return;
            map.Remove(oldName);
            map[newName] = cfg;
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], oldName, StringComparison.OrdinalIgnoreCase)) names[i] = newName;
            WriteAllProfiles(names, map);
            if (string.Equals(ActiveName, oldName, StringComparison.OrdinalIgnoreCase))
                SaveAsActive(cfg, newName);
        }

        public static void DeleteProfile(string name)
        {
            var names = GetProfileNames();
            names.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            var map = LoadProfileMap();
            map.Remove(name);
            if (names.Count == 0)
            {
                names.Add(DefaultProfileName);
                map[DefaultProfileName] = new ConfigData();
            }
            WriteAllProfiles(names, map);
            if (string.Equals(ActiveName, name, StringComparison.OrdinalIgnoreCase))
                SaveAsActive(map[names[0]], names[0]);
        }

        // ---------- 启动初始化：首次运行时把旧单配置迁移成第一个方案 ----------
        public static void EnsureProfiles()
        {
            try
            {
                if (!File.Exists(ProfilesPath))
                {
                    ConfigData legacy = LoadActive();
                    SaveProfile(ActiveName, legacy);
                    SaveAsActive(legacy, ActiveName);
                }
                else
                {
                    if (!GetProfileNames().Exists(n => string.Equals(n, ActiveName, StringComparison.OrdinalIgnoreCase)))
                    {
                        SaveProfile(ActiveName, new ConfigData());
                        SaveAsActive(LoadProfile(ActiveName), ActiveName);
                    }
                }
            }
            catch { }
        }

        public static bool IsValidProfileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            name = name.Trim();
            if (name.Length == 0 || name.Length > 40) return false;
            if (name.IndexOfAny(new[] { '[', ']', '=', '\r', '\n', '|' }) >= 0) return false;
            return true;
        }

        // ---------- 私有 ----------
        private static Dictionary<string, ConfigData> LoadProfileMap()
        {
            var map = new Dictionary<string, ConfigData>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in ReadAllProfiles()) map[kv.Key] = kv.Value;
            return map;
        }

        private static void ApplyLine(ConfigData cfg, string key, string val)
        {
            if (key.Equals("input", StringComparison.OrdinalIgnoreCase)) cfg.Input = val;
            else if (key.Equals("capture", StringComparison.OrdinalIgnoreCase)) cfg.Capture = val;
            else if (key.Equals("render", StringComparison.OrdinalIgnoreCase)) cfg.Render = val;
            else if (key.Equals("apps", StringComparison.OrdinalIgnoreCase))
            {
                cfg.Apps.Clear();
                foreach (string p in val.Split('|'))
                {
                    string t = p.Trim();
                    if (t.Length > 0) cfg.Apps.Add(t);
                }
            }
            else if (key.Equals("urls", StringComparison.OrdinalIgnoreCase))
            {
                cfg.Urls.Clear();
                foreach (string p in val.Split('|'))
                {
                    string t = p.Trim();
                    if (t.Length > 0) cfg.Urls.Add(t);
                }
            }
        }

        private static void WriteAllProfiles(List<string> names, Dictionary<string, ConfigData> map)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# UniversalLauncher 方案文件：每个 [方案名] 是一套配置，可手改");
            sb.AppendLine("# input=键盘布局ID（00000409=美式键盘 / 00000804=简体中文）；留空=不切换");
            sb.AppendLine("# capture=声音输入设备ID；render=声音输出设备ID；apps=应用路径；urls=网址，多个用 | 分隔");
            foreach (string name in names)
            {
                ConfigData cfg;
                if (!map.TryGetValue(name, out cfg)) continue;
                sb.AppendLine();
                sb.AppendLine("[" + name + "]");
                sb.AppendLine("input=" + cfg.Input);
                sb.AppendLine("capture=" + cfg.Capture);
                sb.AppendLine("render=" + cfg.Render);
                sb.AppendLine("apps=" + string.Join("|", cfg.Apps.ToArray()));
                sb.AppendLine("urls=" + string.Join("|", cfg.Urls.ToArray()));
            }
            try { File.WriteAllText(ProfilesPath, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
    }

    // ==================== 搜索并选择应用对话框 ====================
    internal sealed class AppPickerDialog : Form
    {
        private readonly List<AppEntry> _all;
        private readonly TextBox txtSearch;
        private readonly ListView lv;
        public string SelectedPath;

        public AppPickerDialog(List<AppEntry> apps)
        {
            _all = apps;
            Text = "搜索并选择应用";
            ClientSize = new Size(580, 470);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9);

            txtSearch = new TextBox { Location = new Point(12, 12), Size = new Size(556, 26) };
            txtSearch.TextChanged += delegate { Filter(); };
            txtSearch.KeyDown += OnSearchKeyDown;
            Controls.Add(txtSearch);

            lv = new ListView
            {
                Location = new Point(12, 46),
                Size = new Size(556, 360),
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false
            };
            lv.Columns.Add("名称", 240);
            lv.Columns.Add("路径", 310);
            lv.DoubleClick += delegate { Accept(); };
            Controls.Add(lv);

            var btnOk = new Button { Text = "确定", Location = new Point(372, 418), Size = new Size(92, 34) };
            btnOk.Click += delegate { Accept(); };
            var btnCancel = new Button { Text = "取消", Location = new Point(476, 418), Size = new Size(92, 34) };
            btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(btnOk);
            Controls.Add(btnCancel);
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Filter();
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                Accept();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Down)
            {
                if (lv.Items.Count > 0) { lv.Focus(); lv.SelectedIndices.Clear(); lv.SelectedIndices.Add(0); }
                e.SuppressKeyPress = true;
            }
        }

        private void Filter()
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            string q = txtSearch.Text.Trim();
            foreach (var a in _all)
            {
                if (q.Length == 0
                    || a.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || a.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var li = new ListViewItem(a.DisplayName);
                    li.SubItems.Add(a.Path);
                    li.Tag = a;
                    lv.Items.Add(li);
                }
            }
            lv.EndUpdate();
            if (lv.Items.Count > 0 && lv.SelectedIndices.Count == 0)
                lv.SelectedIndices.Add(0);
        }

        private void Accept()
        {
            if (lv.SelectedItems.Count > 0)
            {
                var entry = lv.SelectedItems[0].Tag as AppEntry;
                if (entry != null)
                {
                    SelectedPath = entry.Path;
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }
            }
            // 直接在搜索框里输入的完整路径
            string t = txtSearch.Text.Trim();
            if (t.Length > 0 && File.Exists(t))
            {
                SelectedPath = t;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            MessageBox.Show(this, "请先在上方列表中选择一个应用。", "提示");
        }
    }

    // ==================== 方案命名对话框 ====================
    internal sealed class NamePromptDialog : Form
    {
        public string ResultName;
        private readonly TextBox txt;

        public NamePromptDialog(string title, string defaultValue)
        {
            Text = title;
            ClientSize = new Size(340, 112);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9);

            var lbl = new Label { Text = "方案名称：", Location = new Point(14, 15), AutoSize = true };
            txt = new TextBox { Location = new Point(96, 12), Size = new Size(228, 26), Text = defaultValue ?? "" };
            var ok = new Button { Text = "确定", Location = new Point(140, 54), Size = new Size(88, 32) };
            ok.Click += delegate { ResultName = txt.Text.Trim(); DialogResult = DialogResult.OK; Close(); };
            var cancel = new Button { Text = "取消", Location = new Point(236, 54), Size = new Size(88, 32) };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(lbl);
            Controls.Add(txt);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    // ==================== 主界面 ====================
    internal sealed class MainForm : Form
    {
        private const int MaxApps = 8;
        private const int MaxUrls = 8;

        private readonly List<AppEntry> _installedApps;
        private ComboBox[] _appCombos;
        private readonly string[] _appValues = new string[MaxApps];
        private TextBox[] _urlBoxes;
        private readonly string[] _urlValues = new string[MaxUrls];

        private ComboBox cmbInput;
        private ComboBox cmbCapture;
        private ComboBox cmbRender;
        private ComboBox cmbProfile;
        private NumericUpDown numCount;
        private Panel pnlApps;
        private NumericUpDown numUrlCount;
        private Panel pnlUrls;
        private bool _loadingProfile;
        private bool _suppressAppCapture; // 程序加载方案时禁止把旧行内容写回 _appValues
        private bool _suppressUrlCapture; // 程序加载方案时禁止把旧行内容写回 _urlValues

        public MainForm()
        {
            Text = "通用一键启动器";
            ClientSize = new Size(1010, 715);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9);

            Config.EnsureProfiles();

            _installedApps = Enumerator.GetInstalledApps();
            _appCombos = new ComboBox[MaxApps];
            _urlBoxes = new TextBox[MaxUrls];

            BuildUi();
            PopulateInputCombo();
            PopulateAudioCombos();

            numCount.ValueChanged += numCount_ValueChanged;
            numUrlCount.ValueChanged += numUrlCount_ValueChanged;

            LoadProfilesIntoCombo();
            ApplyActiveProfile();
        }

        private void BuildUi()
        {
            var lblTip = new Label
            {
                Text = "选择输入法、声音设备、要启动的应用和网址，可保存成多套方案后一键启动。",
                Location = new Point(14, 10),
                AutoSize = true
            };
            Controls.Add(lblTip);

            // 方案栏
            var grpProfile = new GroupBox { Text = "方案", Location = new Point(12, 38), Size = new Size(986, 62) };
            var lblProfile = new Label { Text = "方案：", Location = new Point(14, 25), AutoSize = true };
            cmbProfile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(58, 21), Size = new Size(160, 26) };
            cmbProfile.SelectedIndexChanged += cmbProfile_SelectedIndexChanged;
            var btnNew = new Button { Text = "新增方案", Location = new Point(240, 20), Size = new Size(72, 26), Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold) };
            btnNew.Click += btnNew_Click;
            var btnSave = new Button { Text = "保存", Location = new Point(318, 20), Size = new Size(58, 26) };
            btnSave.Click += btnSave_Click;
            var btnRename = new Button { Text = "重命名", Location = new Point(382, 20), Size = new Size(64, 26) };
            btnRename.Click += btnRename_Click;
            var btnDelete = new Button { Text = "删除", Location = new Point(452, 20), Size = new Size(58, 26) };
            btnDelete.Click += btnDelete_Click;
            grpProfile.Controls.Add(lblProfile);
            grpProfile.Controls.Add(cmbProfile);
            grpProfile.Controls.Add(btnNew);
            grpProfile.Controls.Add(btnSave);
            grpProfile.Controls.Add(btnRename);
            grpProfile.Controls.Add(btnDelete);
            Controls.Add(grpProfile);

            var grpInput = new GroupBox { Text = "输入法", Location = new Point(12, 108), Size = new Size(986, 62) };
            cmbInput = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(14, 25), Size = new Size(940, 26) };
            grpInput.Controls.Add(cmbInput);
            Controls.Add(grpInput);

            var grpAudio = new GroupBox { Text = "声音", Location = new Point(12, 178), Size = new Size(986, 90) };
            var lblCap = new Label { Text = "声音输入（麦克风）", Location = new Point(14, 24), AutoSize = true };
            cmbCapture = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(150, 20), Size = new Size(804, 26) };
            var lblRen = new Label { Text = "声音输出（扬声器）", Location = new Point(14, 56), AutoSize = true };
            cmbRender = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(150, 52), Size = new Size(804, 26) };
            grpAudio.Controls.Add(lblCap);
            grpAudio.Controls.Add(cmbCapture);
            grpAudio.Controls.Add(lblRen);
            grpAudio.Controls.Add(cmbRender);
            Controls.Add(grpAudio);

            var grpApps = new GroupBox { Text = "要启动的应用", Location = new Point(12, 276), Size = new Size(484, 384) };
            var lblCount = new Label { Text = "一次启动几个应用：", Location = new Point(14, 26), AutoSize = true };
            numCount = new NumericUpDown { Location = new Point(150, 22), Size = new Size(64, 26), Minimum = 0, Maximum = MaxApps };
            pnlApps = new Panel
            {
                Location = new Point(10, 56),
                Size = new Size(464, 320),
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            grpApps.Controls.Add(lblCount);
            grpApps.Controls.Add(numCount);
            grpApps.Controls.Add(pnlApps);
            Controls.Add(grpApps);

            var grpUrls = new GroupBox { Text = "要打开的网址", Location = new Point(506, 276), Size = new Size(484, 384) };
            var lblUrlCount = new Label { Text = "一次打开几个网址：", Location = new Point(14, 26), AutoSize = true };
            numUrlCount = new NumericUpDown { Location = new Point(150, 22), Size = new Size(64, 26), Minimum = 0, Maximum = MaxUrls };
            pnlUrls = new Panel
            {
                Location = new Point(10, 56),
                Size = new Size(464, 320),
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            grpUrls.Controls.Add(lblUrlCount);
            grpUrls.Controls.Add(numUrlCount);
            grpUrls.Controls.Add(pnlUrls);
            Controls.Add(grpUrls);

            var btnLaunch = new Button
            {
                Text = "一键启动",
                Location = new Point(405, 668),
                Size = new Size(200, 42),
                Font = new Font("Microsoft YaHei UI", 13, FontStyle.Bold)
            };
            btnLaunch.Click += btnLaunch_Click;
            Controls.Add(btnLaunch);
        }

        private void PopulateInputCombo()
        {
            cmbInput.Items.Add("（不切换）");
            foreach (var im in Enumerator.GetInputMethods())
                cmbInput.Items.Add(im);
            cmbInput.SelectedIndex = 0;
        }

        private void PopulateAudioCombos()
        {
            cmbCapture.Items.Add("（不切换）");
            foreach (var d in Enumerator.GetAudioDevices(1)) // 输入
                cmbCapture.Items.Add(d);
            cmbCapture.SelectedIndex = 0;

            cmbRender.Items.Add("（不切换）");
            foreach (var d in Enumerator.GetAudioDevices(0)) // 输出
                cmbRender.Items.Add(d);
            cmbRender.SelectedIndex = 0;
        }

        // ---------- 方案：加载 / 切换 / 保存 ----------
        private void LoadProfilesIntoCombo()
        {
            var names = Config.GetProfileNames();
            if (names.Count == 0) names.Add(Config.ActiveName);
            _loadingProfile = true;
            cmbProfile.Items.Clear();
            foreach (string n in names) cmbProfile.Items.Add(n);
            int idx = names.FindIndex(n => string.Equals(n, Config.ActiveName, StringComparison.OrdinalIgnoreCase));
            cmbProfile.SelectedIndex = idx < 0 ? 0 : idx;
            _loadingProfile = false;
        }

        private void ReloadProfilesAndSelect(string selectName)
        {
            var names = Config.GetProfileNames();
            _loadingProfile = true;
            cmbProfile.Items.Clear();
            foreach (string n in names) cmbProfile.Items.Add(n);
            int idx = names.FindIndex(n => string.Equals(n, selectName, StringComparison.OrdinalIgnoreCase));
            cmbProfile.SelectedIndex = idx < 0 ? 0 : idx;
            _loadingProfile = false;
        }

        private void ApplyActiveProfile()
        {
            if (cmbProfile.SelectedIndex < 0) return;
            string name = cmbProfile.SelectedItem as string;
            var cfg = Config.LoadProfile(name);
            if (cfg == null) cfg = new ConfigData();
            ApplyConfigToUi(cfg);
        }

        private void cmbProfile_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_loadingProfile || cmbProfile.SelectedIndex < 0) return;
            string name = cmbProfile.SelectedItem as string;
            if (string.IsNullOrEmpty(name)) return;
            var cfg = Config.LoadProfile(name);
            if (cfg == null) cfg = new ConfigData();
            ApplyConfigToUi(cfg);
            Config.SaveAsActive(cfg, name); // 记住当前激活方案
        }

        private void ApplyConfigToUi(ConfigData cfg)
        {
            SelectInput(cfg.Input);
            SelectDevice(cmbCapture, cfg.Capture);
            SelectDevice(cmbRender, cfg.Render);

            int n = cfg.Apps.Count;
            if (n > MaxApps) n = MaxApps;
            for (int i = 0; i < MaxApps; i++)
                _appValues[i] = i < n ? cfg.Apps[i] : "";
            int u = cfg.Urls.Count;
            if (u > MaxUrls) u = MaxUrls;
            for (int i = 0; i < MaxUrls; i++)
                _urlValues[i] = i < u ? cfg.Urls[i] : "";

            _suppressAppCapture = true;   // 程序加载，勿用旧行覆盖新值
            numCount.Value = n;
            _suppressAppCapture = false;
            RebuildAppRows();             // 数量未变化时 ValueChanged 不触发，这里兜底

            _suppressUrlCapture = true;
            numUrlCount.Value = u;
            _suppressUrlCapture = false;
            RebuildUrlRows();
        }

        private ConfigData CollectFromUi()
        {
            var cfg = new ConfigData();
            var im = cmbInput.SelectedItem as InputMethodItem;
            if (im != null) cfg.Input = im.Klid;
            var ci = cmbCapture.SelectedItem as DeviceItem;
            if (ci != null) cfg.Capture = ci.Id;
            var ro = cmbRender.SelectedItem as DeviceItem;
            if (ro != null) cfg.Render = ro.Id;
            for (int i = 0; i < (int)numCount.Value && i < _appCombos.Length; i++)
            {
                string t = _appCombos[i] == null ? "" : _appCombos[i].Text.Trim();
                if (t.Length > 0) cfg.Apps.Add(t);
            }
            for (int i = 0; i < (int)numUrlCount.Value && i < _urlBoxes.Length; i++)
            {
                string t = _urlBoxes[i] == null ? "" : _urlBoxes[i].Text.Trim();
                if (t.Length > 0) cfg.Urls.Add(t);
            }
            return cfg;
        }

        private string CurrentProfileName()
        {
            if (cmbProfile.SelectedIndex < 0) return null;
            return cmbProfile.SelectedItem as string;
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            string name = CurrentProfileName();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show(this, "没有可保存的方案。", "提示");
                return;
            }
            var cfg = CollectFromUi();
            Config.SaveProfile(name, cfg);
            Config.SaveAsActive(cfg, name);
            MessageBox.Show(this, "已保存到方案「" + name + "」。", "已保存");
        }

        private void btnNew_Click(object sender, EventArgs e)
        {
            // 自动起名：新方案1、新方案2……避免重名
            string baseName = "新方案";
            var names = Config.GetProfileNames();
            string suggested = baseName;
            int k = 1;
            while (names.Exists(n => string.Equals(n, suggested, StringComparison.OrdinalIgnoreCase)))
            {
                k++;
                suggested = baseName + k;
            }
            using (var dlg = new NamePromptDialog("新增方案", suggested))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string name = dlg.ResultName;
                if (!Config.IsValidProfileName(name))
                {
                    MessageBox.Show(this, "方案名称不合法（不能为空/超过40字/含 [] = 等字符）。", "提示");
                    return;
                }
                if (names.Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show(this, "已存在同名方案「" + name + "」。", "提示");
                    return;
                }
                // 以当前界面配置为起点新建方案，并切换过去
                var cfg = CollectFromUi();
                Config.SaveProfile(name, cfg);
                Config.SaveAsActive(cfg, name);
                ReloadProfilesAndSelect(name);
                MessageBox.Show(this, "已新增方案「" + name + "」，可直接调整后点「保存」。", "新增方案");
            }
        }

        private void btnRename_Click(object sender, EventArgs e)
        {
            string old = CurrentProfileName();
            if (string.IsNullOrEmpty(old)) return;
            using (var dlg = new NamePromptDialog("重命名方案", old))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string name = dlg.ResultName;
                if (!Config.IsValidProfileName(name))
                {
                    MessageBox.Show(this, "方案名称不合法（不能为空/超过40字/含 [] = 等字符）。", "提示");
                    return;
                }
                if (string.Equals(old, name, StringComparison.OrdinalIgnoreCase)) return;
                if (Config.GetProfileNames().Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show(this, "已存在同名方案「" + name + "」。", "提示");
                    return;
                }
                Config.RenameProfile(old, name);
                ReloadProfilesAndSelect(name);
            }
        }

        private void btnDelete_Click(object sender, EventArgs e)
        {
            string name = CurrentProfileName();
            if (string.IsNullOrEmpty(name)) return;
            if (Config.GetProfileNames().Count <= 1)
            {
                MessageBox.Show(this, "至少需要保留一个方案。", "提示");
                return;
            }
            var r = MessageBox.Show(this, "确定删除方案「" + name + "」？", "确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
            bool wasActive = string.Equals(Config.ActiveName, name, StringComparison.OrdinalIgnoreCase);
            Config.DeleteProfile(name);
            string target = wasActive ? Config.GetProfileNames()[0] : Config.ActiveName;
            ReloadProfilesAndSelect(target);
            if (wasActive) ApplyActiveProfile();
        }

        private void SelectInput(string klid)
        {
            for (int i = 0; i < cmbInput.Items.Count; i++)
            {
                var im = cmbInput.Items[i] as InputMethodItem;
                if (im != null && im.Klid == klid) { cmbInput.SelectedIndex = i; return; }
            }
        }

        private void SelectDevice(ComboBox cmb, string id)
        {
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                var d = cmb.Items[i] as DeviceItem;
                if (d != null && d.Id == id) { cmb.SelectedIndex = i; return; }
            }
        }

        private void numCount_ValueChanged(object sender, EventArgs e)
        {
            // 用户手动改数量时，先把已填的行内容保留下来再重建
            if (!_suppressAppCapture) CaptureCurrentAppValues();
            RebuildAppRows();
        }

        private void CaptureCurrentAppValues()
        {
            for (int i = 0; i < _appCombos.Length; i++)
                if (_appCombos[i] != null) _appValues[i] = _appCombos[i].Text;
        }

        // 删除某一行应用，后面的行依次上移，数量自动减一
        private void DeleteAppRow(int idx)
        {
            if (idx < 0 || idx >= (int)numCount.Value) return;
            CaptureCurrentAppValues(); // 先把当前所有行的内容同步到 _appValues
            for (int i = idx; i < MaxApps - 1; i++)
                _appValues[i] = _appValues[i + 1];
            _appValues[MaxApps - 1] = "";
            _suppressAppCapture = true; // 数量变化时勿用旧行内容覆盖新值
            numCount.Value--;
            _suppressAppCapture = false;
        }

        private void RebuildAppRows()
        {
            int n = (int)numCount.Value;

            pnlApps.Controls.Clear();
            for (int i = 0; i < _appCombos.Length; i++) _appCombos[i] = null;

            for (int i = 0; i < n; i++)
            {
                ComboBox cmb = new ComboBox();
                cmb.Location = new Point(50, i * 36);
                cmb.Size = new Size(220, 26);
                cmb.DropDownStyle = ComboBoxStyle.DropDown;
                if (!string.IsNullOrEmpty(_appValues[i])) cmb.Text = _appValues[i];
                _appCombos[i] = cmb;

                var lbl = new Label { Text = "应用" + (i + 1), Location = new Point(4, 6 + i * 36), AutoSize = true };
                var btnSearch = new Button { Text = "搜索…", Location = new Point(272, i * 36), Size = new Size(60, 26) };
                btnSearch.Click += delegate { SearchAppForRow(cmb); };
                var btnBrowse = new Button { Text = "浏览…", Location = new Point(334, i * 36), Size = new Size(60, 26) };
                btnBrowse.Click += delegate { BrowseForApp(cmb); };
                int row = i;
                var btnDel = new Button { Text = "删", Location = new Point(396, i * 36), Size = new Size(42, 26) };
                btnDel.Click += delegate { DeleteAppRow(row); };

                pnlApps.Controls.Add(lbl);
                pnlApps.Controls.Add(cmb);
                pnlApps.Controls.Add(btnSearch);
                pnlApps.Controls.Add(btnBrowse);
                pnlApps.Controls.Add(btnDel);
            }
        }

        private void numUrlCount_ValueChanged(object sender, EventArgs e)
        {
            // 用户手动改数量时，先把已填的行内容保留下来再重建
            if (!_suppressUrlCapture) CaptureCurrentUrlValues();
            RebuildUrlRows();
        }

        private void CaptureCurrentUrlValues()
        {
            for (int i = 0; i < _urlBoxes.Length; i++)
                if (_urlBoxes[i] != null) _urlValues[i] = _urlBoxes[i].Text;
        }

        // 删除某一行网址，后面的行依次上移，数量自动减一
        private void DeleteUrlRow(int idx)
        {
            if (idx < 0 || idx >= (int)numUrlCount.Value) return;
            CaptureCurrentUrlValues(); // 先把当前所有行的内容同步到 _urlValues
            for (int i = idx; i < MaxUrls - 1; i++)
                _urlValues[i] = _urlValues[i + 1];
            _urlValues[MaxUrls - 1] = "";
            _suppressUrlCapture = true; // 数量变化时勿用旧行内容覆盖新值
            numUrlCount.Value--;
            _suppressUrlCapture = false;
        }

        private void RebuildUrlRows()
        {
            int n = (int)numUrlCount.Value;

            pnlUrls.Controls.Clear();
            for (int i = 0; i < _urlBoxes.Length; i++) _urlBoxes[i] = null;

            for (int i = 0; i < n; i++)
            {
                TextBox tb = new TextBox();
                tb.Location = new Point(50, i * 36);
                tb.Size = new Size(344, 26);
                if (!string.IsNullOrEmpty(_urlValues[i])) tb.Text = _urlValues[i];
                _urlBoxes[i] = tb;

                var lbl = new Label { Text = "网址" + (i + 1), Location = new Point(4, 6 + i * 36), AutoSize = true };
                int row = i;
                var btnDel = new Button { Text = "删", Location = new Point(396, i * 36), Size = new Size(42, 26) };
                btnDel.Click += delegate { DeleteUrlRow(row); };

                pnlUrls.Controls.Add(lbl);
                pnlUrls.Controls.Add(tb);
                pnlUrls.Controls.Add(btnDel);
            }
        }

        private void BrowseForApp(ComboBox cmb)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "选择要启动的应用程序";
                ofd.Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    cmb.Text = ofd.FileName;
                }
            }
        }

        private void SearchAppForRow(ComboBox cmb)
        {
            using (var dlg = new AppPickerDialog(_installedApps))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                {
                    cmb.Text = dlg.SelectedPath;
                }
            }
        }

        private void btnLaunch_Click(object sender, EventArgs e)
        {
            var cfg = CollectFromUi();
            string name = CurrentProfileName();
            if (string.IsNullOrEmpty(name)) name = Config.ActiveName;
            Config.SaveProfile(name, cfg);
            Config.SaveAsActive(cfg, name);

            var results = Switcher.ExecuteFlow(cfg);
            MessageBox.Show(this, string.Join("\r\n", results.ToArray()), "一键启动结果",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // ==================== 程序入口 ====================
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                string a = args[0].ToLowerInvariant();
                if (a == "--check" || a == "/check") return RunCheck();
                if (a == "--run" || a == "/run") return RunHeadless();
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        private static int RunCheck()
        {
            Native.AllocConsole();
            var sb = new StringBuilder();
            sb.AppendLine("== UniversalLauncher 环境检查 ==");
            sb.AppendLine("--- 方案 ---");
            foreach (var kv in Config.ReadAllProfiles())
                sb.AppendLine("[" + kv.Key + "] 输入=" + kv.Value.Input + " 输入设备=" + kv.Value.Capture + " 输出设备=" + kv.Value.Render + " 应用数=" + kv.Value.Apps.Count + " 网址数=" + kv.Value.Urls.Count);
            sb.AppendLine("激活方案: " + Config.ActiveName);
            sb.AppendLine("--- 输入法 ---");
            foreach (var im in Enumerator.GetInputMethods())
                sb.AppendLine(im.Klid + " => " + im.Name);
            sb.AppendLine("--- 声音输出 ---");
            foreach (var d in Enumerator.GetAudioDevices(0))
                sb.AppendLine(d.Id + " => " + d.Name);
            sb.AppendLine("--- 声音输入 ---");
            foreach (var d in Enumerator.GetAudioDevices(1))
                sb.AppendLine(d.Id + " => " + d.Name);
            sb.AppendLine("--- 已安装应用 ---");
            var apps = Enumerator.GetInstalledApps();
            sb.AppendLine("共 " + apps.Count + " 个：");
            int n = Math.Min(40, apps.Count);
            for (int i = 0; i < n; i++) sb.AppendLine(apps[i].DisplayName + "  =>  " + apps[i].Path);
            sb.AppendLine("== 完成 ==");
            string text = sb.ToString();
            Console.WriteLine(text);
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UniversalLauncher.check.txt"), text, Encoding.UTF8); } catch { }
            return 0;
        }

        private static int RunHeadless()
        {
            Native.AllocConsole();
            var cfg = Config.LoadActive();
            var results = Switcher.ExecuteFlow(cfg);
            string text = string.Join(Environment.NewLine, results.ToArray());
            Console.WriteLine(text);
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UniversalLauncher.run.txt"), text, Encoding.UTF8); } catch { }
            return 0;
        }
    }
}
