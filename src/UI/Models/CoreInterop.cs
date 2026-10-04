using System;
using System.Runtime.InteropServices;

namespace RocketRPG.Models;

public static class CoreInterop
{
    const string Dll = "RocketRPGCore.dll";
    public const int EngineUnknown = 0, Engine2000 = 1, Engine2003 = 2, EngineXp = 3,
        EngineVx = 4, EngineAce = 5, EngineMv = 6, EngineMz = 7;

    [StructLayout(LayoutKind.Explicit, Size = 2832, CharSet = CharSet.Unicode)]
    public unsafe struct GameInfo
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public fixed char DirRaw[1024];
        [FieldOffset(2052)] public fixed byte TitleUtf8Raw[256];
        [FieldOffset(2308)] public fixed byte TitleAsciiRaw[256];
        [FieldOffset(2564)] public int Engine;
        [FieldOffset(2568)] public int Layout;
        [FieldOffset(2572)] public fixed byte RuntimeDllRaw[128];
        [FieldOffset(2700)] public fixed byte ExeNameRaw[128];
        [FieldOffset(2828)] public byte HasRtp;
        [FieldOffset(2829)] public byte FullPackage;

        public string Dir
        {
            get { fixed (char* p = DirRaw) return new string(p); }
            set
            {
                fixed (char* p = DirRaw)
                {
                    int i = 0;
                    if (value != null)
                    {
                        for (; i < Math.Min(value.Length, 1023); i++) p[i] = value[i];
                    }
                    p[i] = '\0';
                }
            }
        }

        public string TitleUtf8
        {
            get
            {
                fixed (byte* p = TitleUtf8Raw)
                {
                    int len = 0;
                    while (len < 256 && p[len] != 0) len++;
                    if (len == 0) return "";
                    byte[] raw = new byte[len];
                    Marshal.Copy((IntPtr)p, raw, 0, len);
                    return LcfReader.DecodeLcfString(raw);
                }
            }
        }

        public string TitleAscii
        {
            get
            {
                fixed (byte* p = TitleAsciiRaw)
                {
                    int len = 0;
                    while (len < 256 && p[len] != 0) len++;
                    return System.Text.Encoding.ASCII.GetString(p, len);
                }
            }
        }

        public string RuntimeDll
        {
            get
            {
                fixed (byte* p = RuntimeDllRaw)
                {
                    int len = 0;
                    while (len < 128 && p[len] != 0) len++;
                    return System.Text.Encoding.ASCII.GetString(p, len);
                }
            }
        }

        public string ExeName
        {
            get
            {
                fixed (byte* p = ExeNameRaw)
                {
                    int len = 0;
                    while (len < 128 && p[len] != 0) len++;
                    return System.Text.Encoding.ASCII.GetString(p, len);
                }
            }
        }

        public static GameInfo Create() => new() { Size = (uint)Marshal.SizeOf<GameInfo>() };
    }

    [DllImport(Dll)] public static extern int rpg_core_init([MarshalAs(UnmanagedType.LPWStr)] string root);
    [DllImport(Dll)] public static extern void rpg_core_shutdown();
    [DllImport(Dll)] public static extern IntPtr rpg_last_error();
    [DllImport(Dll)] public static extern IntPtr rpg_version();
    [DllImport(Dll)] public static extern int rpg_install_crash_handler([MarshalAs(UnmanagedType.LPWStr)] string crashDir);

    [DllImport(Dll)] public static extern int rpg_scan_games([MarshalAs(UnmanagedType.LPWStr)] string root, out IntPtr dirs, out int count);
    [DllImport(Dll)] public static extern void rpg_free_strings(IntPtr arr, int count);
    [DllImport(Dll)] public static extern int rpg_detect_game([MarshalAs(UnmanagedType.LPWStr)] string dir, ref GameInfo info);
    [DllImport(Dll)] public static extern int rpg_validate_game([MarshalAs(UnmanagedType.LPWStr)] string dir, [Out] byte[] report, int cap);


    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);


    [DllImport(Dll)] public static extern int rpg_set_volume_for_pid(uint pid, double percent);

    // ---------- RocketRenderMKXP Data-Oriented Interop ----------
    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    public unsafe struct MkxpConfig
    {
        public uint Size;
        public int RgssVersion;
        public uint ScreenWidth;
        public uint ScreenHeight;
        public uint TargetFps;
        public byte Fullscreen;
        public byte FixedAspectRatio;
        public byte SmoothScaling;
        public byte Vsync;
        public byte WinResizable;
        public byte PreloadBridge;
        public byte UseSharedSurface;
        public fixed byte Reserved[5];
        public fixed char GameDirRaw[1024];
        public fixed char CustomExeRaw[1024];
        public fixed char RtpPathRaw[1024];

        public string GameDir
        {
            get { fixed (char* p = GameDirRaw) return new string(p); }
            set { fixed (char* p = GameDirRaw) CopyStringToFixed(value, p, 1024); }
        }

        public string CustomExe
        {
            get { fixed (char* p = CustomExeRaw) return new string(p); }
            set { fixed (char* p = CustomExeRaw) CopyStringToFixed(value, p, 1024); }
        }

        public string RtpPath
        {
            get { fixed (char* p = RtpPathRaw) return new string(p); }
            set { fixed (char* p = RtpPathRaw) CopyStringToFixed(value, p, 1024); }
        }

        private static void CopyStringToFixed(string val, char* target, int maxLen)
        {
            int i = 0;
            if (val != null)
            {
                for (; i < Math.Min(val.Length, maxLen - 1); i++) target[i] = val[i];
            }
            target[i] = '\0';
        }

        public static MkxpConfig Create() => new() { Size = (uint)Marshal.SizeOf<MkxpConfig>() };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct MkxpFrame
    {
        public uint Size;
        public uint Width;
        public uint Height;
        public uint StrideBytes;
        public ulong FrameIndex;
        public double Fps;
        public IntPtr SharedNtHandle;
        public IntPtr PixelData;
        public uint PixelDataBytes;
        public uint IsNativeSurface;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct MkxpStats
    {
        public uint Size;
        public uint Pid;
        public IntPtr Hwnd;
        public ulong TotalRenderedFrames;
        public double CurrentFps;
        public int IsRunning;
        public int IsHosted;
    }

    [DllImport(Dll)] public static extern int rpg_mkxp_is_available();
    [DllImport(Dll)] public static extern int rpg_mkxp_is_available_game([MarshalAs(UnmanagedType.LPWStr)] string gameDir);
    [DllImport(Dll)] public static extern int rpg_mkxp_generate_config(ref MkxpConfig cfg, [Out] byte[] outJson, int cap);
    [DllImport(Dll)] public static extern int rpg_mkxp_create(ref MkxpConfig cfg, out IntPtr instance);
    [DllImport(Dll)] public static extern int rpg_mkxp_start(IntPtr instance);
    [DllImport(Dll)] public static extern int rpg_mkxp_stop(IntPtr instance);
    [DllImport(Dll)] public static extern int rpg_mkxp_is_running(IntPtr instance, out int running);
    [DllImport(Dll)] public static extern int rpg_mkxp_get_frame(IntPtr instance, ref MkxpFrame outFrame);
    [DllImport(Dll)] public static extern int rpg_mkxp_read_pixels(IntPtr instance, IntPtr dstBgra8, uint capBytes);
    [DllImport(Dll)] public static extern int rpg_mkxp_get_hwnd(IntPtr instance, out IntPtr outHwnd);
    [DllImport(Dll)] public static extern int rpg_mkxp_get_pid(IntPtr instance, out uint outPid);
    [DllImport(Dll)] public static extern int rpg_mkxp_host_window(IntPtr instance, IntPtr parentHwnd, int x, int y, int w, int h);
    [DllImport(Dll)] public static extern int rpg_mkxp_resize(IntPtr instance, int w, int h);
    [DllImport(Dll)] public static extern int rpg_mkxp_send_input(IntPtr instance, uint msg, IntPtr wparam, IntPtr lparam);
    [DllImport(Dll)] public static extern int rpg_mkxp_get_stats(IntPtr instance, ref MkxpStats outStats);
    [DllImport(Dll)] public static extern void rpg_mkxp_destroy(IntPtr instance);

    // ---------- RocketRenderEasyRPG Data-Oriented Interop ----------
    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    public unsafe struct EasyRpgConfig
    {
        public uint Size;
        public int EngineType; // 1 = 2000, 2 = 2003
        public uint ScreenWidth;
        public uint ScreenHeight;
        public uint TargetFps;
        public byte Fullscreen;
        public byte FixedAspectRatio;
        public byte SmoothScaling;
        public byte Vsync;
        public byte WinResizable;
        public byte PreloadBridge;
        public byte UseSharedSurface;
        public fixed byte Reserved[5];
        public fixed char GameDirRaw[1024];
        public fixed char CustomExeRaw[1024];
        public fixed char RtpPathRaw[1024];

        public string GameDir
        {
            get { fixed (char* p = GameDirRaw) return new string(p); }
            set { fixed (char* p = GameDirRaw) CopyStringToFixed(value, p, 1024); }
        }

        public string CustomExe
        {
            get { fixed (char* p = CustomExeRaw) return new string(p); }
            set { fixed (char* p = CustomExeRaw) CopyStringToFixed(value, p, 1024); }
        }

        public string RtpPath
        {
            get { fixed (char* p = RtpPathRaw) return new string(p); }
            set { fixed (char* p = RtpPathRaw) CopyStringToFixed(value, p, 1024); }
        }

        private static void CopyStringToFixed(string val, char* target, int maxLen)
        {
            int i = 0;
            if (val != null)
            {
                for (; i < Math.Min(val.Length, maxLen - 1); i++) target[i] = val[i];
            }
            target[i] = '\0';
        }

        public static EasyRpgConfig Create() => new() { Size = (uint)Marshal.SizeOf<EasyRpgConfig>() };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct EasyRpgFrame
    {
        public uint Size;
        public uint Width;
        public uint Height;
        public uint StrideBytes;
        public ulong FrameIndex;
        public double Fps;
        public IntPtr SharedNtHandle;
        public IntPtr PixelData;
        public uint PixelDataBytes;
        public uint IsNativeSurface;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct EasyRpgStats
    {
        public uint Size;
        public uint Pid;
        public IntPtr Hwnd;
        public ulong TotalRenderedFrames;
        public double CurrentFps;
        public int IsRunning;
        public int IsHosted;
    }

    [DllImport(Dll)] public static extern int rpg_easyrpg_is_available();
    [DllImport(Dll)] public static extern int rpg_easyrpg_is_available_game([MarshalAs(UnmanagedType.LPWStr)] string gameDir);
    [DllImport(Dll)] public static extern int rpg_easyrpg_create(ref EasyRpgConfig cfg, out IntPtr instance);
    [DllImport(Dll)] public static extern int rpg_easyrpg_start(IntPtr instance);
    [DllImport(Dll)] public static extern int rpg_easyrpg_stop(IntPtr instance);
    [DllImport(Dll)] public static extern int rpg_easyrpg_is_running(IntPtr instance, out int running);
    [DllImport(Dll)] public static extern int rpg_easyrpg_get_frame(IntPtr instance, ref EasyRpgFrame outFrame);
    [DllImport(Dll)] public static extern int rpg_easyrpg_read_pixels(IntPtr instance, IntPtr dstBgra8, uint capBytes);
    [DllImport(Dll)] public static extern int rpg_easyrpg_get_hwnd(IntPtr instance, out IntPtr outHwnd);
    [DllImport(Dll)] public static extern int rpg_easyrpg_get_pid(IntPtr instance, out uint outPid);
    [DllImport(Dll)] public static extern int rpg_easyrpg_host_window(IntPtr instance, IntPtr parentHwnd, int x, int y, int w, int h);
    [DllImport(Dll)] public static extern int rpg_easyrpg_resize(IntPtr instance, int w, int h);
    [DllImport(Dll)] public static extern int rpg_easyrpg_send_input(IntPtr instance, uint msg, IntPtr wparam, IntPtr lparam);
    [DllImport(Dll)] public static extern int rpg_easyrpg_get_stats(IntPtr instance, ref EasyRpgStats outStats);
    [DllImport(Dll)] public static extern void rpg_easyrpg_destroy(IntPtr instance);


    public static string LastError() => Marshal.PtrToStringUTF8(rpg_last_error()) ?? "";
    public static string Version() => Marshal.PtrToStringUTF8(rpg_version()) ?? "0.1.0";
}
