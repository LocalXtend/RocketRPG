using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RocketRPG.Models;

/// <summary>
/// 게임 프로세스의 오디오를 지정한 출력 장치로 라우팅합니다.
/// audioses.dll의 내부 WinRT 클래스 Windows.Media.Internal.AudioPolicyConfig를
/// vtable 슬롯 직접 호출로 사용합니다. (EarTrumpet / SoundSwitch와 동일한 기법)
///
/// - SetPersistedDefaultAudioEndpoint: 슬롯 25/26/27 중 OS 빌드에 맞는 것을 자동 탐색
///   (검증은 자기 프로세스에 set→get 왕복 후 기본값 복원으로 수행)
/// - IID: 팩토리 GetIids 동적 발견 + 알려진 IID 후보군 QI 폴백
/// - 장치 ID 형식: \\?\SWD#MMDEVAPI#{mmdevice-id}#{DEVINTERFACE_AUDIO_RENDER}
/// - eConsole / eMultimedia 두 역할 모두 설정
///
/// OS 업데이트 등으로 인터페이스가 바뀌면 Supported=false가 되어 UI가 기본값 전용으로
/// 실플화(fail-soft)됩니다. 외부 라이브러리/NuGet 없이 self-contained.
/// </summary>
public static class AudioRouter
{
    // ── 공개 API ────────────────────────────────────────────────────────────────

    /// <summary>이 시스템에서 per-process 출력 장치 라우팅을 지원하는지 (지연 1회 프로브)</summary>
    public static bool Supported { get { EnsureProbed(); return _supported; } }

    /// <summary>프로브 과정의 상세 로그 (진단용, 줄바꿈 구분)</summary>
    public static string ProbeLog
    {
        get { EnsureProbed(); return LogBuf.ToString().TrimEnd('\r', '\n'); }
    }

    /// <summary>활성 렌더(출력) 장치 목록. id는 MMDevice ID ({0.0.0.00000000}.{guid} 형태).</summary>
    public static IReadOnlyList<(string id, string name, bool isDefault)> GetRenderDevices()
    {
        var list = new List<(string id, string name, bool isDefault)>();
        foreach (var dev in EnumActiveRenderDevices())
        {
            try
            {
                string id = GetDeviceId(dev);
                if (id == null) continue;
                string name = GetDeviceFriendlyName(dev);
                list.Add((id, string.IsNullOrWhiteSpace(name) ? id : name, false));
            }
            finally { Marshal.Release(dev); }
        }

        string defId = GetDefaultRenderDeviceId();
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].id, defId, StringComparison.OrdinalIgnoreCase))
                list[i] = (list[i].id, list[i].name, true);
        return list;
    }

    /// <summary>
    /// pids의 오디오 출력을 deviceIdOrNull 장치로 라우팅합니다.
    /// deviceIdOrNull=null이면 시스템 기본값으로 되돌립니다. 하나라도 성공하면 true.
    /// </summary>
    public static bool RoutePids(IEnumerable<int> pids, string mmDeviceIdOrNull)
    {
        EnsureProbed();
        if (!_supported || pids == null) return false;
        IntPtr hs = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrEmpty(mmDeviceIdOrNull))
            {
                string fmt = ToPolicyDeviceId(mmDeviceIdOrNull);
                WindowsCreateString(fmt, (uint)fmt.Length, out hs);
            }
            var set = Fn<PolicySetD>(_policyCfg, _setSlot);
            bool any = false;
            foreach (int pid in pids.Distinct())
            {
                if (pid <= 0) continue;
                int rMm = set(_policyCfg, (uint)pid, FlowRender, RoleMultimedia, hs);
                int rCon = set(_policyCfg, (uint)pid, FlowRender, RoleConsole, hs);
                any |= rMm == 0 || rCon == 0;
                UiLog.Write($"audiorouter: set pid={pid} device={(mmDeviceIdOrNull ?? "(default)")} mm=0x{rMm:X8} console=0x{rCon:X8}");
            }
            return any;
        }
        catch (Exception ex)
        {
            UiLog.Write($"audiorouter: RoutePids EX {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally { if (hs != IntPtr.Zero) WindowsDeleteString(hs); }
    }

    /// <summary>
    /// 현재 게임 디렉터리에서 소리를 내고 있는 프로세스 PID를 찾습니다.
    /// 모든 활성 렌더 엔드포인트의 IAudioSessionManager2 세션을 열거하고,
    /// 세션 소유 프로세스의 이미지 경로가 gameDir 하위인 것을 반환합니다.
    /// </summary>
    public static List<int> FindGameAudioPids(string gameDir)
    {
        var pids = new List<int>();
        if (string.IsNullOrWhiteSpace(gameDir)) return pids;
        string root;
        try { root = Path.GetFullPath(gameDir); }
        catch { return pids; }
        if (!root.EndsWith("\\")) root += "\\";

        foreach (var dev in EnumActiveRenderDevices())
        {
            try
            {
                if (Fn<ActivateD>(dev, 3)(dev, IID_IAudioSessionManager2, CLSCTX_ALL, IntPtr.Zero, out var mgr) != 0 || mgr == IntPtr.Zero)
                    continue;
                try
                {
                    if (Fn<GetSessionEnumeratorD>(mgr, 5)(mgr, out var senum) != 0 || senum == IntPtr.Zero)
                        continue;
                    try
                    {
                        if (Fn<SessionGetCountD>(senum, 3)(senum, out int count) != 0) continue;
                        for (int i = 0; i < count; i++)
                        {
                            CollectSessionPid(senum, i, root, pids);
                        }
                    }
                    finally { Marshal.Release(senum); }
                }
                finally { Marshal.Release(mgr); }
            }
            catch { /* 세션 열거 실패는 개별 엔드포인트만 건너뜀 */ }
            finally { Marshal.Release(dev); }
        }
        return pids.Distinct().ToList();
    }

    /// <summary>셀프 테스트: 현재 프로세스를 시스템 기본값(null)으로 라우팅하고 HRESULT를 반환합니다. 0=성공.</summary>
    public static int SelfTestResetCurrentProcess()
    {
        EnsureProbed();
        if (!_supported) return HR_E_FAIL;
        try
        {
            var set = Fn<PolicySetD>(_policyCfg, _setSlot);
            uint pid = (uint)Environment.ProcessId;
            int rMm = set(_policyCfg, pid, FlowRender, RoleMultimedia, IntPtr.Zero);
            int rCon = set(_policyCfg, pid, FlowRender, RoleConsole, IntPtr.Zero);
            return rMm == 0 && rCon == 0 ? 0 : (rMm != 0 ? rMm : rCon);
        }
        catch (Exception ex)
        {
            UiLog.Write($"audiorouter: SelfTest EX {ex.GetType().Name}: {ex.Message}");
            return HR_E_FAIL;
        }
    }

    // ── 상수 ────────────────────────────────────────────────────────────────────

    const string AudioPolicyConfigClassId = "Windows.Media.Internal.AudioPolicyConfig";
    const string MMDEVAPI_TOKEN = @"\\?\SWD#MMDEVAPI#";
    const string DEVINTERFACE_AUDIO_RENDER = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    const int FlowRender = 0;      // EDataFlow.eRender
    const int RoleConsole = 0;     // ERole.eConsole
    const int RoleMultimedia = 1;  // ERole.eMultimedia
    const int DeviceStateActive = 1;
    const int STGM_READ = 0;
    const int CLSCTX_ALL = 0x17;   // INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER | REMOTE_SERVER
    const int HR_E_FAIL = unchecked((int)0x80004005);
    const int HR_PROCESS_NO_AUDIO = unchecked((int)0x80070057); // E_INVALIDARG: 해당 pid의 persisted 값 없음

    static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    static readonly Guid IID_IInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    static readonly Guid IID_IAudioSessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
    static readonly PropertyKey PKEY_Device_FriendlyName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    /// <summary>IAudioPolicyConfig 인터페이스 IID 후보군 (Windows 버전별 변형).</summary>
    static readonly Guid[] KnownConfigIids =
    {
        new("AB3D4648-367C-4BCF-8F14-0E4EEE9F2B4C"), // Win11 21H2+ 계열 (EarTrumpet)
        new("2A59116D-6C32-4DE3-8A15-1B4C7BD0CA4E"), // 다운레벨 계열 (EarTrumpet)
        new("AB3D4648-E242-459F-B02F-541C70306324"), // Win11 (SoundSwitch)
        new("2A59116D-6C4F-45E0-A74F-707E3FEF9258"), // pre-21H2 (SoundSwitch)
        new("32AA8E18-6496-4E24-9F94-B800E7ECCC45"), // 10.0.16299 (SoundSwitch)
    };

    // ── 프로브 상태 ─────────────────────────────────────────────────────────────

    static readonly object Gate = new();
    static readonly StringBuilder LogBuf = new();
    static bool _probed;
    static bool _supported;
    static IntPtr _policyCfg;   // IAudioPolicyConfig*
    static int _setSlot = -1;   // 검증된 SetPersistedDefaultAudioEndpoint 슬롯
    static string _iidUsed = "";

    static void EnsureProbed()
    {
        if (_probed) return;
        lock (Gate)
        {
            if (_probed) return;
            try { Probe(); }
            catch (Exception ex) { LogBuf.AppendLine($"probe: EX {ex.GetType().Name}: {ex.Message}"); }
            _supported = _policyCfg != IntPtr.Zero && _setSlot >= 0;
            LogBuf.AppendLine($"probe: done supported={_supported} iid={_iidUsed} setSlot={_setSlot}");
            _probed = true;
        }
    }

    static void Probe()
    {
        uint pid = (uint)Environment.ProcessId;
        int build = Environment.OSVersion.Version.Build;
        LogBuf.AppendLine($"probe: begin pid={pid} os={Environment.OSVersion.VersionString} build={build}");

        // Windows 10 (빌드 22000 미만)은 내부 WinRT IAudioPolicyConfig 인터페이스 레이아웃이 빌드마다 상이하여
        // 잘못된 vtable 슬롯 호출 시 0xC0000005 크래시가 발생하므로 안전하게 fail-soft 처리합니다.
        if (Environment.OSVersion.Version.Major < 10 || (Environment.OSVersion.Version.Major == 10 && build < 22000))
        {
            LogBuf.AppendLine($"probe: Windows 10 downlevel build detected ({build} < 22000), routing disabled for stability");
            _supported = false;
            _setSlot = -1;
            return;
        }

        // 1) 팩토리 활성화: audioses.dll DllGetActivationFactory → 실패 시 combase RoGetActivationFactory
        IntPtr factory = IntPtr.Zero;
        int hr = -1;
        WindowsCreateString(AudioPolicyConfigClassId, (uint)AudioPolicyConfigClassId.Length, out var cidHs);
        try
        {
            try { hr = DllGetActivationFactoryAudioses(cidHs, out factory); }
            catch (EntryPointNotFoundException) { hr = -1; }
            catch (DllNotFoundException) { hr = -1; }
        }
        finally { WindowsDeleteString(cidHs); }
        LogBuf.AppendLine($"probe: audioses DllGetActivationFactory hr=0x{hr:X8}");
        if (hr != 0 || factory == IntPtr.Zero)
        {
            WindowsCreateString(AudioPolicyConfigClassId, (uint)AudioPolicyConfigClassId.Length, out var cidHs2);
            try
            {
                hr = RoGetActivationFactory(cidHs2, IID_IInspectable, out factory);
                LogBuf.AppendLine($"probe: combase RoGetActivationFactory hr=0x{hr:X8}");
            }
            catch (Exception ex)
            {
                LogBuf.AppendLine($"probe: combase RoGetActivationFactory EX {ex.Message}");
            }
            finally { WindowsDeleteString(cidHs2); }
            if (hr != 0 || factory == IntPtr.Zero) return;
        }

        try
        {
            // 2) IAudioPolicyConfig IID 확정: GetIids 발견 우선 → 알려진 후보 QI 폴백
            Guid cfgIid = DiscoverConfigIid(factory);
            var qi = Fn<QueryInterfaceD>(factory, 0);
            if (cfgIid != Guid.Empty)
            {
                if (qi(factory, cfgIid, out _policyCfg) == 0 && _policyCfg != IntPtr.Zero)
                    _iidUsed = cfgIid.ToString("D");
                else
                    _policyCfg = IntPtr.Zero;
            }
            if (_policyCfg == IntPtr.Zero)
            {
                foreach (var candidate in KnownConfigIids)
                {
                    if (qi(factory, candidate, out var trial) == 0 && trial != IntPtr.Zero)
                    {
                        _policyCfg = trial;
                        _iidUsed = candidate.ToString("D");
                        break;
                    }
                }
            }
            if (_policyCfg == IntPtr.Zero)
            {
                LogBuf.AppendLine("probe: no known IAudioPolicyConfig IID accepted by factory");
                return;
            }
            LogBuf.AppendLine($"probe: QI ok iid={_iidUsed}");

            // Windows 11 (21H2+)의 SetPersistedDefaultAudioEndpoint 슬롯은 25로 고정
            _setSlot = 25;
        }
        finally { Marshal.Release(factory); }
    }

    static Guid DiscoverConfigIid(IntPtr factory)
    {
        try
        {
            if (Fn<GetIidsD>(factory, 3)(factory, out uint count, out IntPtr iidsBuf) != 0 ||
                count == 0 || iidsBuf == IntPtr.Zero)
                return Guid.Empty;
            try
            {
                for (uint i = 0; i < count; i++)
                {
                    var g = Marshal.PtrToStructure<Guid>(iidsBuf + (int)(i * 16));
                    if (KnownConfigIids.Contains(g)) return g;
                }
            }
            finally { CoTaskMemFree(iidsBuf); }
        }
        catch { }
        return Guid.Empty;
    }

    static string ToPolicyDeviceId(string mmDeviceId) =>
        MMDEVAPI_TOKEN + mmDeviceId + DEVINTERFACE_AUDIO_RENDER;

    static string HStringToString(IntPtr hs)
    {
        if (hs == IntPtr.Zero) return null;
        try
        {
            IntPtr buf = WindowsGetStringRawBuffer(hs, out uint len);
            if (buf == IntPtr.Zero || len == 0) return "";
            return Marshal.PtrToStringUni(buf, (int)len);
        }
        catch { return null; }
    }

    // ── MMDevice 열거 ───────────────────────────────────────────────────────────

    /// <summary>활성 렌더 엔드포인트(IMMDevice*)를 하나씩 넘깁니다. 호출자가 각 포인터를 Release해야 합니다.</summary>
    static IEnumerable<IntPtr> EnumActiveRenderDevices()
    {
        if (CoCreateInstance(CLSID_MMDeviceEnumerator, IntPtr.Zero, CLSCTX_ALL, IID_IMMDeviceEnumerator, out var enumerator) != 0 ||
            enumerator == IntPtr.Zero)
            yield break;
        try
        {
            if (Fn<EnumAudioEndpointsD>(enumerator, 3)(enumerator, FlowRender, DeviceStateActive, out var collection) != 0 ||
                collection == IntPtr.Zero)
                yield break;
            try
            {
                if (Fn<CollectionGetCountD>(collection, 3)(collection, out uint count) != 0) yield break;
                for (uint i = 0; i < count; i++)
                {
                    if (Fn<CollectionItemD>(collection, 4)(collection, i, out var device) == 0 && device != IntPtr.Zero)
                        yield return device;
                }
            }
            finally { Marshal.Release(collection); }
        }
        finally { Marshal.Release(enumerator); }
    }

    static string GetDefaultRenderDeviceId()
    {
        if (CoCreateInstance(CLSID_MMDeviceEnumerator, IntPtr.Zero, CLSCTX_ALL, IID_IMMDeviceEnumerator, out var enumerator) != 0 ||
            enumerator == IntPtr.Zero)
            return null;
        try
        {
            if (Fn<GetDefaultAudioEndpointD>(enumerator, 4)(enumerator, FlowRender, RoleMultimedia, out var device) != 0 ||
                device == IntPtr.Zero)
                return null;
            try { return GetDeviceId(device); }
            finally { Marshal.Release(device); }
        }
        finally { Marshal.Release(enumerator); }
    }

    static string GetDeviceId(IntPtr device)
    {
        try
        {
            if (Fn<GetIdD>(device, 5)(device, out var pwstr) != 0 || pwstr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(pwstr); }
            finally { CoTaskMemFree(pwstr); }
        }
        catch { return null; }
    }

    static string GetDeviceFriendlyName(IntPtr device)
    {
        var store = IntPtr.Zero;
        try
        {
            int openHr = Fn<OpenPropertyStoreD>(device, 4)(device, STGM_READ, out store);
            if (openHr != 0 || store == IntPtr.Zero)
            {
                LogNameDiag($"OpenPropertyStore hr=0x{openHr:X8}");
                return null;
            }
            IntPtr pvPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
            try
            {
                ZeroMemory(pvPtr, (uint)Marshal.SizeOf<PropVariant>());
                int hr = Fn<PropertyStoreGetValueD>(store, 5)(store, PKEY_Device_FriendlyName, pvPtr);
                if (hr != 0)
                {
                    LogNameDiag($"GetValue hr=0x{hr:X8}");
                    return null;
                }
                var pv = Marshal.PtrToStructure<PropVariant>(pvPtr);
                if (pv.Vt != 31)
                {
                    LogNameDiag($"vt={pv.Vt}");
                    return null;
                }
                return Marshal.PtrToStringUni(pv.Data);
            }
            finally
            {
                PropVariantClear(pvPtr);
                Marshal.FreeHGlobal(pvPtr);
            }
        }
        catch (Exception ex)
        {
            LogNameDiag($"EX {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally { if (store != IntPtr.Zero) Marshal.Release(store); }
    }

    static bool _nameDiagLogged;
    static void LogNameDiag(string why)
    {
        if (_nameDiagLogged) return;
        _nameDiagLogged = true;
        UiLog.Write($"audiorouter: friendly-name fallback ({why})");
    }

    // ── 세션 → PID ──────────────────────────────────────────────────────────────

    static void CollectSessionPid(IntPtr sessionEnumerator, int index, string gameDirRoot, List<int> pids)
    {
        if (Fn<GetSessionD>(sessionEnumerator, 4)(sessionEnumerator, index, out var session) != 0 || session == IntPtr.Zero)
            return;
        try
        {
            if (Fn<QueryInterfaceD>(session, 0)(session, IID_IAudioSessionControl2, out var ctl2) != 0 || ctl2 == IntPtr.Zero)
                return;
            try
            {
                if (Fn<GetProcessIdD>(ctl2, 14)(ctl2, out uint pid) != 0 || pid == 0) return;
                string imagePath = GetProcessImagePath((int)pid);
                if (imagePath != null &&
                    imagePath.StartsWith(gameDirRoot, StringComparison.OrdinalIgnoreCase) &&
                    !pids.Contains((int)pid))
                    pids.Add((int)pid);
            }
            finally { Marshal.Release(ctl2); }
        }
        finally { Marshal.Release(session); }
    }

    static string GetProcessImagePath(int pid)
    {
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (hProcess == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(hProcess, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
        }
        finally { CloseHandle(hProcess); }
    }

    // ── vtable 헬퍼 ─────────────────────────────────────────────────────────────

    static T Fn<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    // ── COM vtable 델리게이트 ───────────────────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int QueryInterfaceD(IntPtr self, in Guid iid, out IntPtr ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetIidsD(IntPtr self, out uint iidCount, out IntPtr iids);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int EnumAudioEndpointsD(IntPtr self, int dataFlow, int stateMask, out IntPtr collection);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetDefaultAudioEndpointD(IntPtr self, int dataFlow, int role, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int ActivateD(IntPtr self, in Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int OpenPropertyStoreD(IntPtr self, int stgmAccess, out IntPtr store);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetIdD(IntPtr self, out IntPtr pwstrId);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CollectionGetCountD(IntPtr self, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CollectionItemD(IntPtr self, uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int PropertyStoreGetValueD(IntPtr self, in PropertyKey key, IntPtr propVariant);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetSessionEnumeratorD(IntPtr self, out IntPtr enumerator);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int SessionGetCountD(IntPtr self, out int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetSessionD(IntPtr self, int index, out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetProcessIdD(IntPtr self, out uint pid);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int PolicyGetD(IntPtr self, uint processId, int dataFlow, int role, ref IntPtr deviceIdHString);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int PolicySetD(IntPtr self, uint processId, int dataFlow, int role, IntPtr deviceIdHString);

    // ── 구조체 ──────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
        public PropertyKey(Guid formatId, uint propertyId) { FormatId = formatId; PropertyId = propertyId; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant
    {
        public ushort Vt;          // VT_LPWSTR = 31
        public ushort Reserved1, Reserved2, Reserved3;
        public IntPtr Data;        // VT_LPWSTR → pwszVal
        public IntPtr Pad1, Pad2;  // 네이티브 PROPVARIANT는 24바이트(DECIMAL 유니온 포함) — 버퍼를 충분히 확보
    }

    // ── 네이티브 임포트 ─────────────────────────────────────────────────────────

    [DllImport("audioses.dll", EntryPoint = "DllGetActivationFactory")]
    static extern int DllGetActivationFactoryAudioses(IntPtr activatableClassIdHString, out IntPtr factory);

    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(IntPtr activatableClassIdHString, in Guid iid, out IntPtr factory);

    [DllImport("combase.dll")]
    static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    [DllImport("ole32.dll")]
    static extern int CoCreateInstance(in Guid clsid, IntPtr unkOuter, uint clsContext, in Guid iid, out IntPtr obj);

    [DllImport("ole32.dll")]
    static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(IntPtr propVariant);

    [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory", SetLastError = false)]
    static extern void ZeroMemory(IntPtr dest, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
}
