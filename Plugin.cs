using System;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.CustomProfleImage;

public sealed class Plugin : IStellarPlugin
{
    public string Name => "CustomProfileImage";

    private const string HarmonyId = "stellar.customprofleimage-plugin";

    private readonly IPluginServices _services;
    private          IWindowControl  _window = null!;
    private readonly IDisposable     _launcherEntry;

    private object?     _luaState;
    private MethodInfo? _luaDoString;
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

    // Re-entrancy guard: ProcessAvatarUpload calls CallLua which fires DoString again.
    [System.ThreadStatic]
    private static bool _inLuaTick;

    public Plugin(IPluginServices services)
    {
        _instance = this;
        _services = services;

        EnsureLuaState();
        InstallLuaReadyTrigger();
        _services.Framework.Update += OnFrameworkUpdate;

        _window = RegisterWindow();

        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            Title:   "Custom Profile Image",
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen:  () => _window.SetVisible(true))
        { Group = LauncherGroup.Plugin });

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
                             : "Choose File…",
                OnClick: ChooseFile,
                Enabled: () => !_dialogOpen,
                Width:   270f),
        };

        elements.AddRange(new HudElement[]
        {
            new SeparatorElement(),
            new TextElement(() => "Avatar", Emphasis: true),
            new TextElement(() => "Recommended: 300×300 px square PNG",
                            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new RowElement(new HudElement[]
            {
                new ButtonElement(
                    Label:   () => "Make Avatar",
                    OnClick: MakeAvatar,
                    Enabled: () => !_dialogOpen && _selectedImagePath.Length > 0,
                    Width:   100f),
                new ButtonElement(
                    Label:   () => "Cancel",
                    OnClick: ClearAvatarHooks,
                    Enabled: () => _avatarHooksActive,
                    Width:   100f),
            }, Gap: 8f),
            new ConditionalElement(
                () => _avatarUploadStatus.Length > 0,
                new TextElement(() => _avatarUploadStatus,
                                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted)),
            new SeparatorElement(),
            new TextElement(() => "Namecard", Emphasis: true),
            new TextElement(() => "Recommended: 468×774 px portrait PNG",
                            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
            new RowElement(new HudElement[]
            {
                new ButtonElement(
                    Label:   () => "Make Namecard",
                    OnClick: MakeNamecard,
                    Enabled: () => !_dialogOpen && _selectedImagePath.Length > 0,
                    Width:   100f),
                new ButtonElement(
                    Label:   () => "Cancel",
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
            elements.Add(new TextElement(() => "Preview", Emphasis: true));
            elements.Add(new TextElement(
                () => "Preview may not reflect the final result. Check the in-game preview for the actual appearance.",
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
                Title:       "Custom Profile Image",
                DefaultRect: new WindowRect(_services.Framework.ScreenWidth - 460f, 20f, 440f, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              // Gameplay tool: upload requires being in-world (checks Z.EntityMgr.PlayerEnt).
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World },
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
        var lsType = FindType("LuaInterface.LuaState");
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
        var umType = FindType("UpdateManager");
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

        if (_instance._luaDoString == null)
            _instance.EnsureLuaState();

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

    // ── Lua state ─────────────────────────────────────────────────────────────

    private void EnsureLuaState()
    {
        if (_luaDoString != null) return;

        var lsType = FindType("LuaInterface.LuaState");
        if (lsType != null)
        {
            _luaState =
                lsType.GetProperty("mainState", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
                ?? lsType.GetField("mainState",  BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);
        }

        if (_luaState is null)
        {
            var clientType = FindType("LuaClient");
            var clientInst = clientType
                ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (clientInst != null)
            {
                var t = clientInst.GetType();
                _luaState =
                    t.GetProperty("luaState", BindingFlags.Instance | BindingFlags.Public)?.GetValue(clientInst)
                    ?? t.GetField("luaState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(clientInst);
            }
        }

        if (_luaState != null)
        {
            foreach (var m in _luaState.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "DoString" || m.IsGenericMethod) continue;
                var ps = m.GetParameters();
                if (ps.Length < 1 || ps[0].ParameterType != typeof(string)) continue;
                if (m.ReturnType == typeof(void) && ps.Length >= 2 && _luaDoString == null)
                    _luaDoString = m;
            }
        }

        _services.Log.Info($"[CustomProfileImage] LuaState={_luaState?.GetType()?.FullName ?? "null"} DoString={(_luaDoString != null ? "ok" : "null")}");
    }

    private string? CallLua(string chunk)
    {
        if (_luaState is null || _luaDoString is null)
        {
            _services.Log.Warning("[CustomProfileImage] CallLua: LuaState not ready");
            return "LuaState not ready";
        }
        try
        {
            _luaDoString.Invoke(_luaState, new object[] { chunk, "customprofleimage" });
            return null;
        }
        catch (Exception ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            _services.Log.Warning($"[CustomProfileImage] CallLua threw: {msg}");
            return msg;
        }
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t is not null) return t;
        }
        return null;
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

    private void ShowDialogAsync(Action<string?> callback)
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
            try { result = ShowOpenPngDialog(); }
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

    private static string? ShowOpenPngDialog()
    {
        const int MaxPath = 520;
        var buf = Marshal.AllocHGlobal(MaxPath * 2);
        try
        {
            Marshal.Copy(new byte[MaxPath * 2], 0, buf, MaxPath * 2);
            var ofn = new OFN
            {
                lStructSize  = Marshal.SizeOf<OFN>(),
                lpstrFilter  = "PNG Images\0*.png\0",
                nFilterIndex = 1,
                lpstrFile    = buf,
                nMaxFile     = MaxPath,
                lpstrTitle   = "Select Portrait Image",
                lpstrDefExt  = "png",
                Flags        = 0x00001000 | 0x00000800,
            };
            if (GetOpenFileNameW(ref ofn))
                return Marshal.PtrToStringUni(buf);
            return null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ── File pick + upload actions ────────────────────────────────────────────

    private void ChooseFile()
    {
        ShowDialogAsync(picked =>
        {
            if (picked != null)
                _pendingSelectedPath = picked;
        });
    }

    private void MakeAvatar()
    {
        if (_namecardHooksActive) { ClearNamecardHooks(); _namecardUploadStatus = ""; }
        ProcessAvatarUpload(_selectedImagePath);
    }

    private void MakeNamecard()
    {
        if (_avatarHooksActive) { ClearAvatarHooks(); _avatarUploadStatus = ""; }
        ProcessNamecardUpload(_selectedImagePath);
    }

    // ── Avatar upload ─────────────────────────────────────────────────────────

    private void ProcessAvatarUpload(string picked)
    {
        if (!System.IO.File.Exists(picked)) { _avatarUploadStatus = "File not found"; _window.MarkDirty(); return; }

        var luaFilePath = picked.Replace(@"\", @"\\");
        var fileUrl     = "file:///" + picked.Replace('\\', '/');

        _avatarUploadStatus = "Starting...";
        _window.MarkDirty();
        EnsureLuaState();

        if (_luaDoString == null) { _avatarUploadStatus = "Lua not ready"; _window.MarkDirty(); return; }

        // Synchronous errors propagate to C# via error() -> CallLua returns the message.
        // Async handlers (server callbacks) use pcall + LuaSetStatus internally.
        var setupErr = CallLua(
            " if not (Z.EntityMgr).PlayerEnt then error('Need world: enter game first') end" +
            " local stub=require('zservice/photograph_ntf_impl')" +
            " local origRet=stub.RetAvatarToken" +
            " local origGet=stub.GetPhotoTokenNtf" +
            " local origUpR=stub.UploadPhotoResultNtf" +
            " local origUpP=stub.UploadPictureResultNtf" +
            " local origRev=stub.ReviewAvatarInfoNtf" +
            " _G.__stlr_av_origRet=origRet _G.__stlr_av_origGet=origGet _G.__stlr_av_origUpR=origUpR _G.__stlr_av_origUpP=origUpP _G.__stlr_av_origRev=origRev" +
            " stub.RetAvatarToken=function(self,call,vReq)" +
            "  stub.RetAvatarToken=origRet stub.GetPhotoTokenNtf=origGet stub.UploadPhotoResultNtf=origUpR stub.UploadPictureResultNtf=origUpP stub.ReviewAvatarInfoNtf=origRev" +
            "  local cbOk,cbErr=pcall(function()" +
            "   if vReq.errCode~=0 then " + LuaSetStatus("'Token err:'..tostring(vReq.errCode)") + " return end" +
            "   local r=vReq.result if not r then " + LuaSetStatus("'No result'") + " return end" +
            "   " + LuaSetStatus("'Uploading...'") +
            "   local up=(Z.UploadParm).New()" +
            "   up.TmpSecretId=r.tmpSecretId up.TmpSecretKey=r.tmpSecretKey" +
            "   up.Region=r.region up.TmpToken=r.tmpToken" +
            "   up.ExpireTime=r.expiredTime up.Bucket=r.bucket up.SaveKey=r.objectKey" +
            "   up.CallBackFunc=function(isOk)" +
            "    if not isOk then " + LuaSetStatus("'COS failed'") + " return end" +
            "    ;(Z.CoroUtil.create_coro_xpcall)(function()" +
            "     local px=require('zproxy.photograph_proxy')" +
            "     local cs=(Z.CancelSource).Rent()" +
            "     local ret=px.UploadPhotoSuccessful({" +
            "      charId=Z.ContainerMgr.CharSerialize.charBase.charId," +
            "      pictureId=r.pictureId," +
            "      funcType=(E.HttpTokenType).HeadProfile," +
            "      data={{pictureUrl=r.objectKey,version=r.version,pictureType=(E.PictureType).EProfileSnapShot}}" +
            "     },cs:CreateToken())" +
            "     if ret and ret.errCode==0 then " + LuaSetStatus("'Done'") +
            "     else " + LuaSetStatus("'Confirm err:'..(ret and tostring(ret.errCode) or '?')") + " end" +
            "    end,function(e)" + LuaSetStatus("'Coro err:'..tostring(e)") + " end)()" +
            "   end" +
            "   local _pf=io.open('" + luaFilePath + "','rb')" +
            "   if not _pf then " + LuaSetStatus("'No file " + luaFilePath + "'") + " return end" +
            "   local _bytes=_pf:read('*a') _pf:close()" +
            "   if not _bytes or #_bytes==0 then " + LuaSetStatus("'Empty file'") + " return end" +
            "   " + LuaSetStatus("'Uploading '..(#_bytes)..'b...'") +
            "   Z.UploadMgr:UploadPictureToCos(up,_bytes)" +
            "  end)" +
            "  if not cbOk then " + LuaSetStatus("'CB err:'..tostring(cbErr)") + " end" +
            " end" +
            " stub.GetPhotoTokenNtf=function(self,call,vReq) stub.GetPhotoTokenNtf=origGet " + LuaSetStatus("'NTF:GetPhotoTokenNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origGet,self,call,vReq) end" +
            " stub.UploadPhotoResultNtf=function(self,call,vReq) stub.UploadPhotoResultNtf=origUpR " + LuaSetStatus("'NTF:UploadPhotoResultNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origUpR,self,call,vReq) end" +
            " stub.UploadPictureResultNtf=function(self,call,vReq) stub.UploadPictureResultNtf=origUpP " + LuaSetStatus("'NTF:UploadPictureResultNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origUpP,self,call,vReq) end" +
            " stub.ReviewAvatarInfoNtf=function(self,call,vReq) stub.ReviewAvatarInfoNtf=origRev " + LuaSetStatus("'NTF:ReviewAvatarInfoNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origRev,self,call,vReq) end" +
            " local popupCls=require('ui.view.photo_personalzone_idcard_popup_view')" +
            " local origSetHead=popupCls.setHeadImg _G.__stlr_av_origSetHead=origSetHead" +
            " popupCls.setHeadImg=function(self)" +
            "  pcall(function()" +
            "   local rimg=((self.uiBinder).binder_head).rimg_portrait" +
            "   rimg:AsyncLoadUrlImage('" + fileUrl + "',function() end)" +
            "  end)" +
            " end" +
            " local cvm=Z.VMMgr.GetVM('camerasys')" +
            " if not cvm then error('No camera VM') end" +
            " local origGetToken=cvm.GetHeadOrBodyPhotoToken _G.__stlr_av_origGetToken=origGetToken" +
            " cvm.GetHeadOrBodyPhotoToken=function(texId,snapType)" +
            "  cvm.GetHeadOrBodyPhotoToken=origGetToken" +
            "  popupCls.setHeadImg=origSetHead" +
            "  " + LuaSetStatus("'Portrait confirm fired texId='..tostring(texId)..' snap='..tostring(snapType)") +
            "  origGetToken(texId,snapType)" +
            " end");

        _services.Log.Info($"[CustomProfileImage] avatar setup: {(setupErr == null ? "ok" : setupErr)}");
        if (setupErr != null)
        {
            var msg = setupErr;
            var ci  = msg.LastIndexOf(": ");
            if (ci >= 0 && ci < msg.Length - 2) msg = msg.Substring(ci + 2);
            _avatarUploadStatus = msg;
            _window.MarkDirty();
            return;
        }

        _avatarUploadStatus = "Ready - Open your inventory and use the Avatar Change card and take any shot and the preview should appear";
        _avatarHooksActive  = true;
        _window.MarkDirty();
    }

    private void ClearAvatarHooks()
    {
        EnsureLuaState();
        CallLua(
            "pcall(function()" +
            " local ok,stub=pcall(require,'zservice/photograph_ntf_impl')" +
            " if ok and stub then" +
            "  if _G.__stlr_av_origRet then stub.RetAvatarToken=_G.__stlr_av_origRet end" +
            "  if _G.__stlr_av_origGet then stub.GetPhotoTokenNtf=_G.__stlr_av_origGet end" +
            "  if _G.__stlr_av_origUpR then stub.UploadPhotoResultNtf=_G.__stlr_av_origUpR end" +
            "  if _G.__stlr_av_origUpP then stub.UploadPictureResultNtf=_G.__stlr_av_origUpP end" +
            "  if _G.__stlr_av_origRev then stub.ReviewAvatarInfoNtf=_G.__stlr_av_origRev end" +
            " end" +
            " local cvm=Z.VMMgr.GetVM('camerasys')" +
            " if cvm and _G.__stlr_av_origGetToken then cvm.GetHeadOrBodyPhotoToken=_G.__stlr_av_origGetToken end" +
            " local popupCls=require('ui.view.photo_personalzone_idcard_popup_view')" +
            " if popupCls and _G.__stlr_av_origSetHead then popupCls.setHeadImg=_G.__stlr_av_origSetHead end" +
            " _G.__stlr_av_origRet=nil _G.__stlr_av_origGet=nil _G.__stlr_av_origUpR=nil" +
            " _G.__stlr_av_origUpP=nil _G.__stlr_av_origRev=nil" +
            " _G.__stlr_av_origGetToken=nil _G.__stlr_av_origSetHead=nil _G.__stlr_av_status=nil" +
            "end)");
        _avatarHooksActive  = false;
        _avatarUploadStatus = "Override cancelled";
        _window.MarkDirty();
    }

    // ── Namecard upload ───────────────────────────────────────────────────────

    private void ProcessNamecardUpload(string picked)
    {
        if (!System.IO.File.Exists(picked)) { _namecardUploadStatus = "File not found"; _window.MarkDirty(); return; }

        var luaFilePath = picked.Replace(@"\", @"\\");
        var fileUrl     = "file:///" + picked.Replace('\\', '/');

        _namecardUploadStatus = "Starting...";
        _window.MarkDirty();
        EnsureLuaState();

        if (_luaDoString == null) { _namecardUploadStatus = "Lua not ready"; _window.MarkDirty(); return; }

        var setupErr = CallLua(
            " if not (Z.EntityMgr).PlayerEnt then error('Need world: enter game first') end" +
            " local stub=require('zservice/photograph_ntf_impl')" +
            " local origRet=stub.RetAvatarToken" +
            " local origGet=stub.GetPhotoTokenNtf" +
            " local origUpR=stub.UploadPhotoResultNtf" +
            " local origUpP=stub.UploadPictureResultNtf" +
            " local origRev=stub.ReviewAvatarInfoNtf" +
            " _G.__stlr_nc_origRet=origRet _G.__stlr_nc_origGet=origGet _G.__stlr_nc_origUpR=origUpR _G.__stlr_nc_origUpP=origUpP _G.__stlr_nc_origRev=origRev" +
            " stub.RetAvatarToken=function(self,call,vReq)" +
            "  stub.RetAvatarToken=origRet stub.GetPhotoTokenNtf=origGet stub.UploadPhotoResultNtf=origUpR stub.UploadPictureResultNtf=origUpP stub.ReviewAvatarInfoNtf=origRev" +
            "  local cbOk,cbErr=pcall(function()" +
            "   if vReq.errCode~=0 then " + LuaSetNcStatus("'Token err:'..tostring(vReq.errCode)") + " return end" +
            "   local r=vReq.result if not r then " + LuaSetNcStatus("'No result'") + " return end" +
            "   " + LuaSetNcStatus("'Uploading...'") +
            "   local up=(Z.UploadParm).New()" +
            "   up.TmpSecretId=r.tmpSecretId up.TmpSecretKey=r.tmpSecretKey" +
            "   up.Region=r.region up.TmpToken=r.tmpToken" +
            "   up.ExpireTime=r.expiredTime up.Bucket=r.bucket up.SaveKey=r.objectKey" +
            "   up.CallBackFunc=function(isOk)" +
            "    if not isOk then " + LuaSetNcStatus("'COS failed'") + " return end" +
            "    ;(Z.CoroUtil.create_coro_xpcall)(function()" +
            "     local px=require('zproxy.photograph_proxy')" +
            "     local cs=(Z.CancelSource).Rent()" +
            "     local ret=px.UploadPhotoSuccessful({" +
            "      charId=Z.ContainerMgr.CharSerialize.charBase.charId," +
            "      pictureId=r.pictureId," +
            "      funcType=(E.HttpTokenType).HeadProfile," +
            "      data={{pictureUrl=r.objectKey,version=r.version,pictureType=(E.PictureType).EProfileHalfBody}}" +
            "     },cs:CreateToken())" +
            "     if ret and ret.errCode==0 then " + LuaSetNcStatus("'Done'") +
            "     else " + LuaSetNcStatus("'Confirm err:'..(ret and tostring(ret.errCode) or '?')") + " end" +
            "    end,function(e)" + LuaSetNcStatus("'Coro err:'..tostring(e)") + " end)()" +
            "   end" +
            "   local _pf=io.open('" + luaFilePath + "','rb')" +
            "   if not _pf then " + LuaSetNcStatus("'No file " + luaFilePath + "'") + " return end" +
            "   local _bytes=_pf:read('*a') _pf:close()" +
            "   if not _bytes or #_bytes==0 then " + LuaSetNcStatus("'Empty file'") + " return end" +
            "   " + LuaSetNcStatus("'Uploading '..(#_bytes)..'b...'") +
            "   Z.UploadMgr:UploadPictureToCos(up,_bytes)" +
            "  end)" +
            "  if not cbOk then " + LuaSetNcStatus("'CB err:'..tostring(cbErr)") + " end" +
            " end" +
            " stub.GetPhotoTokenNtf=function(self,call,vReq) stub.GetPhotoTokenNtf=origGet " + LuaSetNcStatus("'NTF:GetPhotoTokenNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origGet,self,call,vReq) end" +
            " stub.UploadPhotoResultNtf=function(self,call,vReq) stub.UploadPhotoResultNtf=origUpR " + LuaSetNcStatus("'NTF:UploadPhotoResultNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origUpR,self,call,vReq) end" +
            " stub.UploadPictureResultNtf=function(self,call,vReq) stub.UploadPictureResultNtf=origUpP " + LuaSetNcStatus("'NTF:UploadPictureResultNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origUpP,self,call,vReq) end" +
            " stub.ReviewAvatarInfoNtf=function(self,call,vReq) stub.ReviewAvatarInfoNtf=origRev " + LuaSetNcStatus("'NTF:ReviewAvatarInfoNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origRev,self,call,vReq) end" +
            " local idcardCls=require('ui.view.idcard_view')" +
            " local origSetPhoto=idcardCls.setPhotoData _G.__stlr_nc_origSetPhoto=origSetPhoto" +
            " idcardCls.setPhotoData=function(self)" +
            "  pcall(function()" +
            "   local bnd=(self.uiBinder)" +
            "   local rimg=bnd.rimg_idcard_figure" +
            "   bnd.Ref:SetVisible(rimg,true)" +
            "   rimg:AsyncLoadUrlImage('" + fileUrl + "',function() end)" +
            "  end)" +
            " end" +
            " local cvm=Z.VMMgr.GetVM('camerasys')" +
            " if not cvm then error('No camera VM') end" +
            " local origGetToken=cvm.GetHeadOrBodyPhotoToken _G.__stlr_nc_origGetToken=origGetToken" +
            " cvm.GetHeadOrBodyPhotoToken=function(texId,snapType)" +
            "  cvm.GetHeadOrBodyPhotoToken=origGetToken" +
            "  idcardCls.setPhotoData=origSetPhoto" +
            "  " + LuaSetNcStatus("'Namecard confirm fired texId='..tostring(texId)..' snap='..tostring(snapType)") +
            "  origGetToken(texId,snapType)" +
            " end");

        _services.Log.Info($"[CustomProfileImage] namecard setup: {(setupErr == null ? "ok" : setupErr)}");
        if (setupErr != null)
        {
            var msg = setupErr;
            var ci  = msg.LastIndexOf(": ");
            if (ci >= 0 && ci < msg.Length - 2) msg = msg.Substring(ci + 2);
            _namecardUploadStatus = msg;
            _window.MarkDirty();
            return;
        }

        _namecardUploadStatus = "Ready - Open your inventory and use the Namecard Change card and take any shot and the preview should appear";
        _namecardHooksActive  = true;
        _window.MarkDirty();
    }

    private void ClearNamecardHooks()
    {
        EnsureLuaState();
        CallLua(
            "pcall(function()" +
            " local ok,stub=pcall(require,'zservice/photograph_ntf_impl')" +
            " if ok and stub then" +
            "  if _G.__stlr_nc_origRet then stub.RetAvatarToken=_G.__stlr_nc_origRet end" +
            "  if _G.__stlr_nc_origGet then stub.GetPhotoTokenNtf=_G.__stlr_nc_origGet end" +
            "  if _G.__stlr_nc_origUpR then stub.UploadPhotoResultNtf=_G.__stlr_nc_origUpR end" +
            "  if _G.__stlr_nc_origUpP then stub.UploadPictureResultNtf=_G.__stlr_nc_origUpP end" +
            "  if _G.__stlr_nc_origRev then stub.ReviewAvatarInfoNtf=_G.__stlr_nc_origRev end" +
            " end" +
            " local cvm=Z.VMMgr.GetVM('camerasys')" +
            " if cvm and _G.__stlr_nc_origGetToken then cvm.GetHeadOrBodyPhotoToken=_G.__stlr_nc_origGetToken end" +
            " local ok2,idcardCls=pcall(require,'ui.view.idcard_view')" +
            " if ok2 and idcardCls and _G.__stlr_nc_origSetPhoto then idcardCls.setPhotoData=_G.__stlr_nc_origSetPhoto end" +
            " _G.__stlr_nc_origRet=nil _G.__stlr_nc_origGet=nil _G.__stlr_nc_origUpR=nil" +
            " _G.__stlr_nc_origUpP=nil _G.__stlr_nc_origRev=nil" +
            " _G.__stlr_nc_origGetToken=nil _G.__stlr_nc_origSetPhoto=nil _G.__stlr_nc_status=nil" +
            "end)");
        _namecardHooksActive  = false;
        _namecardUploadStatus = "Override cancelled";
        _window.MarkDirty();
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
