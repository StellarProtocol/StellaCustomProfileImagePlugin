using System;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.CustomProfleImage;

public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "CustomProfileImage";

    private const string HarmonyId = "stellar.customprofleimage-plugin";

    private readonly IPluginServices _services;
    private readonly ILocalization   _loc;
    private          IWindowControl  _window = null!;
    private readonly IDisposable     _launcherEntry;

    private Harmony?    _luaReadyHarmony;

    // The live main LuaState captured off the DoString patch's `__instance`. LuaInterface.LuaClient is
    // absent from the interop assembly, so LuaClient.GetMainState() can't reach the running state — but the
    // `this` of every LuaState.DoString call IS that main state. The game calls DoString every frame, so this
    // is populated long before any upload. Read by PushBytesToLuaGlobal as its preferred instance source.
    private static object? _capturedLuaState;

    private string _uploadStatus = "";
    private bool   _hooksActive  = false;
    // Last value mirrored out of the Lua status global (_G.__stlr_up_status). The uploader parks its
    // diagnostic strings there; we surface changes to the window + log so a failed confirm isn't invisible.
    private string? _lastLuaStatus = null;

    private string           _selectedImagePath   = "";
    private string           _selectedFileName    = "";   // Path.GetFileName, resolved once per pick (polled by the UI)
    private byte[]?          _previewPngBytes     = null;
    // PNG IHDR size of the picked file (0 = unknown / not a PNG) + the size-check verdicts, computed once per pick.
    private int              _previewPxW          = 0;
    private int              _previewPxH          = 0;
    private bool             _avatarMatch         = false;
    private bool             _namecardMatch       = false;
    private static Plugin?   _instance;
    private volatile string? _pendingSelectedPath = null;
    private volatile bool    _dialogOpen          = false;

    // Re-entrancy guard: ProcessUpload calls _services.Lua.DoString, which re-enters the
    // LuaState.DoString patch and fires OnLuaDoStringStatic again.
    [System.ThreadStatic]
    private static bool _inLuaTick;

    public Plugin(IPluginServices services)
    {
        _instance = this;
        _services = services;
        _loc      = services.Localization;

        InstallLuaReadyTrigger();
        _services.Framework.Update += OnFrameworkUpdate;

        _window = RegisterWindow();

        // Title stays the fixed literal "Custom Profile Image" — the stable pin-identity key
        // (ILauncher.cs:49-50) — so a pinned tile survives a language change; TitleProvider carries the
        // live-localized display.
        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            Title:   "Custom Profile Image",
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen:  () => _window.SetVisible(true))
        { Group = LauncherGroup.Plugin,
          // Re-localize the tile DISPLAY on a language change; Title above never changes (pin identity).
          TitleProvider = () => _loc.T("cpi.title"),
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World });

        _services.Log.Info("[CustomProfileImage] constructed");
    }

    // A new pick re-registers the window so the PREVIEW ImageElement (fixed size at build time) gets the new picture's
    // aspect-fit size. Only the picture inside the fixed frame changes: the window keeps its width (DefaultRect) and
    // height (fixed content), and SetRect on a non-resizable window restores the position only.
    private void RebuildWindow()
    {
        var rect    = _window.Rect;
        var visible = _window.IsShown;
        _window.Remove();
        _window = RegisterWindow();
        if (rect.Width > 0f) _window.SetRect(rect);
        _window.SetVisible(visible);
    }

    public void Dispose()
    {
        _services.Framework.Update -= OnFrameworkUpdate;
        _instance = null;
        _luaReadyHarmony?.UnpatchSelf();
        _luaReadyHarmony = null;
        _launcherEntry.Dispose();
        _window.Remove();
    }

    // ── Lua ready trigger ──────────────────────────────────────────────────────

    private void InstallLuaReadyTrigger()
    {
        var lsType = StellarInterop.FindType("LuaInterface.LuaState");
        if (lsType == null) { _services.Log.Info("[CustomProfileImage] LuaReadyTrigger: LuaState not found"); return; }

        MethodInfo? target = null;
        foreach (var m in lsType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "DoString" || m.IsGenericMethod) continue;
            var ps = m.GetParameters();
            if (ps.Length >= 1 && ps[0].ParameterType == typeof(string) && m.ReturnType == typeof(void))
            { target = m; break; }
        }
        if (target == null) { _services.Log.Info("[CustomProfileImage] LuaReadyTrigger: DoString not found"); return; }

        _luaReadyHarmony = new Harmony(HarmonyId + ".luaready");
        _luaReadyHarmony.Patch(target, postfix: new HarmonyMethod(typeof(Plugin), nameof(OnLuaDoStringStatic)));
        // Second postfix on the SAME DoString target: capture the live main LuaState from `__instance`.
        // Attached ONLY here (not to UpdateManager.Update below, whose instance is the wrong type).
        _luaReadyHarmony.Patch(target, postfix: new HarmonyMethod(typeof(Plugin), nameof(CaptureLuaStatePostfix)));
        _services.Log.Info("[CustomProfileImage] LuaReadyTrigger installed");

        // Hook UpdateManager.Update — AOT-compiled, fires every Unity frame,
        // gives us a reliable per-frame tick independent of DoString frequency.
        var umType = StellarInterop.FindType("UpdateManager");
        if (umType != null)
        {
            var umUpdate = umType.GetMethod("Update",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (umUpdate != null)
            {
                _luaReadyHarmony.Patch(umUpdate,
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(OnLuaDoStringStatic)));
                _services.Log.Info("[CustomProfileImage] UpdateManager.Update hook installed");
            }
            else _services.Log.Info("[CustomProfileImage] UpdateManager.Update not found");
        }
        else _services.Log.Info("[CustomProfileImage] UpdateManager not found");
    }

    private void OnFrameworkUpdate(float _)
    {
        var pending = _pendingSelectedPath;
        if (pending != null)
        {
            _pendingSelectedPath = null;
            if (_hooksActive) ClearHooks();
            // A new file starts clean: no "cancelled" / error text left over from the previous one.
            _uploadStatus      = "";
            _selectedImagePath = pending;
            LoadPreview(pending);
            RebuildWindow();
        }

        // Mirror the Lua-side upload status into the window + log while hooks are armed. The uploader
        // parks a stable CODE in _G.__stlr_up_status (via LuaSetStatus) — Lua can't call _loc.T, so it
        // parks 'uploading'/'done'/'error' and we map the code to a localized string here. The specific
        // technical cause stays in the Lua logError traces (Player.log), out of the user's window. One
        // ReadGlobalString per frame, only while active. Change-detection runs on the RAW code so a
        // repeated code doesn't re-fire. Null/absent means "no new status" — leave the C#-set text alone.
        if (_hooksActive)
        {
            var luaStatus = _services.Lua.ReadGlobalString("__stlr_up_status");
            if (luaStatus != null && luaStatus != _lastLuaStatus)
            {
                _lastLuaStatus = luaStatus;
                _uploadStatus  = MapLuaStatus(luaStatus);
                _window.MarkDirty();
                _services.Log.Info($"[CustomProfileImage] lua-status: {luaStatus}");
            }
        }
    }

    // Map the stable status CODE the Lua uploader parks to a localized, user-friendly string. Unknown or
    // legacy values fall through to the raw string so nothing is silently swallowed.
    private string MapLuaStatus(string code) => code switch
    {
        "uploading" => _loc.T("cpi.status.uploading"),
        "done"      => _loc.T("cpi.status.done"),
        "error"     => _loc.T("cpi.status.uploadFailed"),
        _           => code,
    };

    // Target shapes for the size check (one image serves both): avatar 1:1, namecard 468×774. A chip shows a match when
    // the picked image's aspect ratio is within SizeTolerance of the target — the game crops/scales, so only the SHAPE
    // matters, not the exact pixel size.
    private const float AvatarAspect   = 1f;
    private const float NamecardAspect = 468f / 774f;
    private const float SizeTolerance  = 0.05f;

    private static bool AspectMatches(int w, int h, float target)
        => w > 0 && h > 0 && MathF.Abs((float)w / h / target - 1f) <= SizeTolerance;

    private void LoadPreview(string path)
    {
        _selectedFileName = System.IO.Path.GetFileName(path);
        _previewPxW = _previewPxH = 0;
        try
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == (byte)'P' &&
                bytes[2] == (byte)'N'  && bytes[3] == (byte)'G')
            {
                _previewPxW = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                _previewPxH = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                if (_previewPxW <= 0 || _previewPxH <= 0) _previewPxW = _previewPxH = 0;
            }
            _previewPngBytes = bytes;
            _services.Log.Info($"[CustomProfileImage] LoadPreview: {bytes.Length} bytes, {_previewPxW}x{_previewPxH}");
        }
        catch (Exception ex)
        {
            _previewPngBytes = null;
            _services.Log.Warning($"[CustomProfileImage] LoadPreview failed: {ex.Message}");
        }
        _avatarMatch   = AspectMatches(_previewPxW, _previewPxH, AvatarAspect);
        _namecardMatch = AspectMatches(_previewPxW, _previewPxH, NamecardAspect);
    }

    // Capture the running main LuaState the first time DoString is called. `__instance` is the LuaState the
    // game invoked DoString on — the one whose `L` handle PushBytesToLuaGlobal needs. Only ever attached to
    // the LuaState.DoString target, so __instance is always a LuaState here.
    private static void CaptureLuaStatePostfix(object __instance)
    {
        if (_capturedLuaState == null && __instance != null) _capturedLuaState = __instance;
    }

    private static void OnLuaDoStringStatic()
    {
        if (_instance == null || _inLuaTick) return;

        _inLuaTick = true;
        try
        {
            var pending = _instance._pendingSelectedPath;
            if (pending != null)
            {
                _instance._pendingSelectedPath = null;
                if (_instance._hooksActive) _instance.ClearHooks();
                _instance._uploadStatus      = "";
                _instance._selectedImagePath = pending;
                _instance.LoadPreview(pending);
                _instance.RebuildWindow();
            }
        }
        finally { _inLuaTick = false; }
    }

    // ── Lua global helpers ────────────────────────────────────────────────────

    // rawset bypasses the game's strict-global __newindex guard, which rejects undeclared-global writes
    // (a plain `_G.__stlr_up_status=...` is silently blocked, leaving the value nil).
    private static string LuaSetStatus(string luaValueExpr) =>
        $"rawset(_G,'__stlr_up_status',({luaValueExpr}))";

    // ── Windows dialog + focus helpers ────────────────────────────────────────

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetOpenFileNameW(ref OFN ofn);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OFN
    {
        public int    lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int    nMaxCustFilter;
        public int    nFilterIndex;
        public IntPtr lpstrFile;
        public int    nMaxFile;
        public IntPtr lpstrFileTitle;
        public int    nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int    Flags;
        public short  nFileOffset;
        public short  nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int    dwReserved;
        public int    FlagsEx;
    }

    private void ShowDialogAsync(string title, string filter, Action<string?> callback)
    {
        // No hwndOwner: keeps the game window enabled so Unity's Lua tick keeps running.
        // MTA (default): avoids STA cross-apartment deadlock with the game's main thread.
        var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        _dialogOpen = true;
        _window.MarkDirty();
        _services.Log.Info("[CustomProfileImage] ShowDialogAsync: bg thread starting");
        var t = new System.Threading.Thread(() =>
        {
            string? result = null;
            try { result = ShowOpenPngDialog(title, filter); }
            catch (Exception ex) { _services.Log.Warning($"[CustomProfileImage] ShowOpenPngDialog threw: {ex.Message}"); }
            _services.Log.Info($"[CustomProfileImage] dialog result: {result ?? "null (cancelled or error)"}");
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
            _dialogOpen = false;
            _window.MarkDirty();
            callback(result);
        });
        t.IsBackground = true;
        t.Start();
    }

    private static string? ShowOpenPngDialog(string title, string filter)
    {
        const int MaxPath = 520;
        var buf = Marshal.AllocHGlobal(MaxPath * 2);
        try
        {
            Marshal.Copy(new byte[MaxPath * 2], 0, buf, MaxPath * 2);
            var ofn = new OFN
            {
                lStructSize  = Marshal.SizeOf<OFN>(),
                lpstrFilter  = filter,
                nFilterIndex = 1,
                lpstrFile    = buf,
                nMaxFile     = MaxPath,
                lpstrTitle   = title,
                lpstrDefExt  = "png",
                Flags        = 0x00001000 | 0x00000800,
            };
            if (GetOpenFileNameW(ref ofn))
                return Marshal.PtrToStringUni(buf);
            return null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ── File pick ──────────────────────────────────────────────────────────────

    private void ChooseFile()
    {
        var title  = _loc.T("cpi.dialog.title");
        var filter = _loc.T("cpi.dialog.filter") + "\0*.png\0";
        ShowDialogAsync(title, filter, picked =>
        {
            if (picked != null)
                _pendingSelectedPath = picked;
        });
    }

    // ── Icon ──────────────────────────────────────────────────────────────────

    private static byte[]? LoadIconPng()
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.CustomProfleImage.customprofleimage-icon.png");
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
