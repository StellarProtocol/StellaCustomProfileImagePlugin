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

    private string _avatarUploadStatus   = "";
    private bool   _avatarHooksActive    = false;

    private string _namecardUploadStatus = "";
    private bool   _namecardHooksActive  = false;

    private string           _selectedImagePath   = "";
    private byte[]?          _previewPngBytes     = null;
    private int              _previewHeight       = 200;
    private static Plugin?   _instance;
    private volatile string? _pendingSelectedPath = null;
    private volatile bool    _dialogOpen          = false;

    // Re-entrancy guard: ProcessAvatarUpload calls _services.Lua.DoString, which re-enters the
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
            new TextElement(() => _loc.T("cpi.avatar"), Emphasis: true),
            new TextElement(() => _loc.T("cpi.avatar.hint"),
                            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new RowElement(new HudElement[]
            {
                new ButtonElement(
                    Label:   () => _loc.T("cpi.avatar.make"),
                    OnClick: MakeAvatar,
                    Enabled: () => !_dialogOpen && _selectedImagePath.Length > 0,
                    Width:   100f),
                new ButtonElement(
                    Label:   () => _loc.T("cpi.cancel"),
                    OnClick: ClearAvatarHooks,
                    Enabled: () => _avatarHooksActive,
                    Width:   100f),
            }, Gap: 8f),
            new ConditionalElement(
                () => _avatarUploadStatus.Length > 0,
                new TextElement(() => _avatarUploadStatus,
                                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted)),
            new SeparatorElement(),
            new TextElement(() => _loc.T("cpi.namecard"), Emphasis: true),
            new TextElement(() => _loc.T("cpi.namecard.hint"),
                            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new RowElement(new HudElement[]
            {
                new ButtonElement(
                    Label:   () => _loc.T("cpi.namecard.make"),
                    OnClick: MakeNamecard,
                    Enabled: () => !_dialogOpen && _selectedImagePath.Length > 0,
                    Width:   100f),
                new ButtonElement(
                    Label:   () => _loc.T("cpi.cancel"),
                    OnClick: ClearNamecardHooks,
                    Enabled: () => _namecardHooksActive,
                    Width:   100f),
            }, Gap: 8f),
            new ConditionalElement(
                () => _namecardUploadStatus.Length > 0,
                new TextElement(() => _namecardUploadStatus,
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
                DefaultRect: new WindowRect(_services.Framework.ScreenWidth - 460f, 20f, 440f, 0f),
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
            if (_avatarHooksActive)   { ClearAvatarHooks();   _avatarUploadStatus   = ""; }
            if (_namecardHooksActive) { ClearNamecardHooks(); _namecardUploadStatus = ""; }
            _selectedImagePath = pending;
            LoadPreview(pending);
            RebuildWindow();
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
                if (_instance._avatarHooksActive)   { _instance.ClearAvatarHooks();   _instance._avatarUploadStatus   = ""; }
                if (_instance._namecardHooksActive) { _instance.ClearNamecardHooks(); _instance._namecardUploadStatus = ""; }
                _instance._selectedImagePath = pending;
                _instance.LoadPreview(pending);
                _instance.RebuildWindow();
            }
        }
        finally { _inLuaTick = false; }
    }

    // ── Lua global helpers ────────────────────────────────────────────────────

    private static string LuaSetStatus(string luaValueExpr) =>
        $"_G.__stlr_av_status=({luaValueExpr})";

    private static string LuaSetNcStatus(string luaValueExpr) =>
        $"_G.__stlr_nc_status=({luaValueExpr})";

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
