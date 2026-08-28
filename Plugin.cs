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
    private byte[]?          _previewPngBytes     = null;
    private int              _previewHeight       = 200;
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

        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            Title:   _loc.T("cpi.title"),
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen:  () => _window.SetVisible(true))
        { Group = LauncherGroup.Plugin,
          // Re-localize the tile title live on a language change (Title alone is a captured string).
          TitleProvider = () => _loc.T("cpi.title"),
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World });

        _services.Log.Info("[CustomProfileImage] constructed");
    }

    private IWindowControl RegisterWindow()
    {
        var capturedBytes  = _previewPngBytes;
        var capturedHeight = _previewHeight;

        var elements = new System.Collections.Generic.List<HudElement>
        {
            new SeparatorElement(),
            new ButtonElement(
                Label:   () => _selectedImagePath.Length > 0
                             ? System.IO.Path.GetFileName(_selectedImagePath)
                             : _loc.T("cpi.chooseFile"),
                OnClick: ChooseFile,
                Enabled: () => !_dialogOpen,
                Width:   270f),
        };

        elements.AddRange(new HudElement[]
        {
            new SeparatorElement(),
            // Recommended-size reference — one image serves both, so show both target sizes.
            new TextElement(() => _loc.T("cpi.recommendedSize"), Emphasis: true),
            new TextElement(() => _loc.T("cpi.avatar.hint"),
                            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new TextElement(() => _loc.T("cpi.namecard.hint"),
                            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new SeparatorElement(),
            // How-to instruction block.
            new TextElement(() => _loc.T("cpi.howto.title"), Emphasis: true),
            new TextElement(() => _loc.T("cpi.howto.step1"),
                            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new TextElement(() => _loc.T("cpi.howto.step2"),
                            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new TextElement(() => _loc.T("cpi.howto.step3"),
                            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new TextElement(() => _loc.T("cpi.howto.step4"),
                            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new TextElement(() => _loc.T("cpi.howto.step5"),
                            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new TextElement(() => _loc.T("cpi.howto.step6"),
                            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new SeparatorElement(),
            new RowElement(new HudElement[]
            {
                new ButtonElement(
                    Label:   () => _loc.T("cpi.apply"),
                    OnClick: ApplyUpload,
                    Enabled: () => !_dialogOpen && _selectedImagePath.Length > 0,
                    // Wide enough for the longest localized label on ONE line:
                    // CJK ja "アバター／ネームカードを作成" (14 full-width glyphs) is the constraint.
                    Width:   280f),
                new ButtonElement(
                    Label:   () => _loc.T("cpi.cancel"),
                    OnClick: ClearHooks,
                    Enabled: () => _hooksActive,
                    Width:   95f),
            }, Gap: 8f),
            new ConditionalElement(
                () => _uploadStatus.Length > 0,
                new TextElement(() => _uploadStatus,
                                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted)),
        });

        if (capturedBytes != null)
        {
            elements.Add(new SeparatorElement());
            elements.Add(new TextElement(() => _loc.T("cpi.preview"), Emphasis: true));
            elements.Add(new TextElement(
                () => _loc.T("cpi.preview.hint"),
                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted));
            elements.Add(new RowElement(new HudElement[]
            {
                new SpacerElement(Width: 58f),
                new ImageElement(
                    Png:    () => capturedBytes,
                    Width:  300,
                    Height: capturedHeight),
                new SpacerElement(Width: 58f),
            }));
        }

        return _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "customprofleimage.main",
                Title:       _loc.T("cpi.title"),
                DefaultRect: new WindowRect(_services.Framework.ScreenWidth - 520f, 20f, 500f, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              // Gameplay tool: upload requires being in-world (checks Z.EntityMgr.PlayerEnt).
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root:    new ColumnElement(elements.ToArray(), Gap: 8f),
            OnClose: () => _window.SetVisible(false)));
    }

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
            if (_hooksActive) { ClearHooks(); _uploadStatus = ""; }
            _selectedImagePath = pending;
            LoadPreview(pending);
            RebuildWindow();
        }

        // Mirror the Lua-side upload status into the window + log while hooks are armed. The uploader
        // parks its progress/error strings in _G.__stlr_up_status (via LuaSetStatus) but nothing reads
        // them back, so confirm/upload failures were invisible. One ReadGlobalString per frame, only
        // while active. Null/absent means "no new status" — leave the C#-set Ready/error text alone.
        if (_hooksActive)
        {
            var luaStatus = _services.Lua.ReadGlobalString("__stlr_up_status");
            if (luaStatus != null && luaStatus != _lastLuaStatus)
            {
                _lastLuaStatus = luaStatus;
                _uploadStatus  = luaStatus;
                _window.MarkDirty();
                _services.Log.Info($"[CustomProfileImage] lua-status: {luaStatus}");
            }
        }
    }

    private void LoadPreview(string path)
    {
        try
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == (byte)'P' &&
                bytes[2] == (byte)'N'  && bytes[3] == (byte)'G')
            {
                int pw = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                int ph = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                _previewHeight = (pw > 0) ? Math.Max(1, (int)(300f * ph / pw)) : 300;
            }
            else _previewHeight = 200;
            _previewPngBytes = bytes;
            _services.Log.Info($"[CustomProfileImage] LoadPreview: {bytes.Length} bytes, 200x{_previewHeight}");
        }
        catch (Exception ex)
        {
            _previewPngBytes = null;
            _previewHeight   = 200;
            _services.Log.Warning($"[CustomProfileImage] LoadPreview failed: {ex.Message}");
        }
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
                if (_instance._hooksActive) { _instance.ClearHooks(); _instance._uploadStatus = ""; }
                _instance._selectedImagePath = pending;
                _instance.LoadPreview(pending);
                _instance.RebuildWindow();
            }
        }
        finally { _inLuaTick = false; }
    }

    // ── Lua global helpers ────────────────────────────────────────────────────

    private static string LuaSetStatus(string luaValueExpr) =>
        $"_G.__stlr_up_status=({luaValueExpr})";

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
