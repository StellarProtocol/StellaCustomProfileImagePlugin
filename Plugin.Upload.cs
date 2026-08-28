namespace Stellar.CustomProfleImage;

public sealed partial class Plugin
{
    private void ApplyUpload() => ProcessUpload(_selectedImagePath);

    // ── Unified avatar / namecard upload ───────────────────────────────────────
    //
    // Avatar and namecard share the SAME upload chain (camerasys_vm.GetHeadOrBodyPhotoToken →
    // world_proxy.GetAvatarToken → RetAvatarToken → snapshot_vm.UpLoad). Only two things differ:
    //   (a) the preview hook — avatar hooks photo_personalzone_idcard_popup_view.setHeadImg
    //       (loads into binder_head.rimg_portrait); namecard hooks idcard_view.setPhotoData
    //       (loads into rimg_idcard_figure, needs Ref:SetVisible(rimg,true)), and
    //   (b) the finalize pictureType — avatar EProfileSnapShot (4), namecard EProfileHalfBody (5).
    // The two preview hooks live on different view classes, so we arm BOTH at once with no conflict.
    // Crucially, the snapType arg passed into the GetHeadOrBodyPhotoToken override IS that same
    // E.PictureType value (head→EProfileSnapShot, halfbody→EProfileHalfBody), so the uploader sets
    // pictureType=snapType instead of hardcoding it — auto-detecting avatar vs namecard from whichever
    // card the player actually uses at confirm time. One selected image serves both.
    //
    // Hook lifecycle (token-race fix preserved — see Custom-Profile-Image-Upload.md):
    //   Setup (Apply)  installs ONLY the two preview hooks and the cvm.GetHeadOrBodyPhotoToken
    //                  override. Neither self-restores, so a failed/early-returning confirm can be
    //                  retried. The RetAvatarToken uploader is NOT armed here — that avoids a stray
    //                  earlier server token consuming a global one-shot.
    //   Confirm        installs the RetAvatarToken uploader closure fresh (capturing snapType),
    //                  scoped to exactly this confirm's token, then calls the saved original GetToken.
    //   Token reply    uploads the user's file with pictureType=snapType. On success it FULLY disarms
    //                  (restores both previews, GetToken, RetAvatarToken, nils the origin globals). On
    //                  any error it restores only RetAvatarToken and leaves previews + GetToken armed.
    private void ProcessUpload(string picked)
    {
        // Fresh upload — forget any status mirrored from a prior confirm so the per-frame mirror re-fires
        // on the first new value even if it repeats the last one.
        _lastLuaStatus = null;

        if (!System.IO.File.Exists(picked)) { _uploadStatus = _loc.T("cpi.status.fileNotFound"); _window.MarkDirty(); return; }

        // Read the user's PNG bytes in .NET (Unicode-safe) rather than letting Lua's io.open touch the
        // path at all. io.open goes through the ANSI CRT fopen, which fails on any non-ASCII path — and
        // staging to an ASCII cache copy still failed when the GAME itself is installed under a non-ASCII
        // path. We push the raw bytes straight into the Lua VM as a byte-exact string global (no file, no
        // base64, no chunking, no decoder), so the upload depends on NO path at all.
        byte[] bytes;
        try
        {
            bytes = System.IO.File.ReadAllBytes(picked);
        }
        catch (System.Exception ex)
        {
            _uploadStatus = _loc.T("cpi.status.fileNotFound") + ": " + ex.Message;
            _window.MarkDirty();
            return;
        }
        if (bytes.Length == 0) { _uploadStatus = _loc.T("cpi.status.emptyFile"); _window.MarkDirty(); return; }

        // Preview URL still points at the ORIGINAL file — Unity's AsyncLoadUrlImage is UTF-8-aware and
        // handled non-ASCII user paths fine (that was never the broken part). Escape ' for the single-quoted
        // Lua literal (a path may contain an apostrophe; backslashes are already turned to forward slashes).
        var fileUrl = ("file:///" + picked.Replace('\\', '/')).Replace("'", @"\'");

        _uploadStatus = _loc.T("cpi.status.starting");
        _window.MarkDirty();

        if (!_services.Lua.Ready) { _uploadStatus = _loc.T("cpi.status.luaNotReady"); _window.MarkDirty(); return; }

        // Push the raw image bytes straight into the Lua VM as a byte-exact string global via tolua#'s
        // native lua_pushlstring (Lua strings are byte arrays — no encoding, byte-perfect). The uploader
        // reads _G.__stlr_up_bytes and hands it to UploadPictureToCos, whose byte[] param already takes a
        // Lua string as-is. Must run BEFORE the setup DoString so the global exists when confirm reads it.
        if (!PushBytesToLuaGlobal(bytes, "__stlr_up_bytes"))
        {
            _uploadStatus = _loc.T("cpi.status.uploadFailed");
            _window.MarkDirty();
            return;
        }

        // Reliable log-channel confirmation that the exact byte length landed (should equal the file size).
        // tolua# print() is a no-op, but the game's global logError reaches the Unity/BepInEx log.
        _services.Lua.DoString("logError('[STLR] bytes global len='..tostring(_G.__stlr_up_bytes and #_G.__stlr_up_bytes or -1))");

        // Setup runs under pcall; a synchronous error() is parked in a status global and read back
        // via ReadGlobalString (ILua.DoString is fire-and-forget). setupErr == null means success.
        // Async handlers (server callbacks) use pcall + LuaSetStatus internally.
        _services.Lua.DoString(
            "rawset(_G,'__stlr_up_setup',nil) local _stlrOk,_stlrErr=pcall(function()" +
            " if not (Z.EntityMgr).PlayerEnt then error('Need world: enter game first') end" +
            " local stub=require('zservice/photograph_ntf_impl')" +
            " local origRet=stub.RetAvatarToken" +
            " local origGet=stub.GetPhotoTokenNtf" +
            " local origUpR=stub.UploadPhotoResultNtf" +
            " local origUpP=stub.UploadPictureResultNtf" +
            " local origRev=stub.ReviewAvatarInfoNtf" +
            " rawset(_G,'__stlr_up_origRet',origRet) rawset(_G,'__stlr_up_origGet',origGet) rawset(_G,'__stlr_up_origUpR',origUpR) rawset(_G,'__stlr_up_origUpP',origUpP) rawset(_G,'__stlr_up_origRev',origRev)" +
            // Avatar preview hook (setHeadImg fires on popup open — armed from Apply, stays armed).
            " local popupCls=require('ui.view.photo_personalzone_idcard_popup_view')" +
            " local origSetHead=popupCls.setHeadImg rawset(_G,'__stlr_up_origSetHead',origSetHead)" +
            " popupCls.setHeadImg=function(self)" +
            "  pcall(function()" +
            "   local rimg=((self.uiBinder).binder_head).rimg_portrait" +
            "   rimg:AsyncLoadUrlImage('" + fileUrl + "',function() end)" +
            "  end)" +
            " end" +
            // Namecard preview hook (setPhotoData — different view class, no conflict with setHeadImg).
            " local idcardCls=require('ui.view.idcard_view')" +
            " local origSetPhoto=idcardCls.setPhotoData rawset(_G,'__stlr_up_origSetPhoto',origSetPhoto)" +
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
            " local origGetToken=cvm.GetHeadOrBodyPhotoToken rawset(_G,'__stlr_up_origGetToken',origGetToken)" +
            " cvm.GetHeadOrBodyPhotoToken=function(texId,snapType)" +
            // Confirm fired: install the uploader NOW, scoped to this confirm's token. pictureType is
            // snapType (head→EProfileSnapShot / halfbody→EProfileHalfBody) — auto-detects card type.
            "  stub.RetAvatarToken=function(self,call,vReq)" +
            "   local cbOk,cbErr=pcall(function()" +
            "    logError('[STLR] uploader fired bytes='..tostring(_G.__stlr_up_bytes and #_G.__stlr_up_bytes or 'nil'))" +
            "    if vReq.errCode~=0 then " + LuaSetStatus("'error'") + " logError('[STLR] token err '..tostring(vReq.errCode)) stub.RetAvatarToken=origRet return end" +
            "    local r=vReq.result if not r then " + LuaSetStatus("'error'") + " logError('[STLR] no result') stub.RetAvatarToken=origRet return end" +
            "    " + LuaSetStatus("'uploading'") +
            "    local up=(Z.UploadParm).New()" +
            "    up.TmpSecretId=r.tmpSecretId up.TmpSecretKey=r.tmpSecretKey" +
            "    up.Region=r.region up.TmpToken=r.tmpToken" +
            "    up.ExpireTime=r.expiredTime up.Bucket=r.bucket up.SaveKey=r.objectKey" +
            "    up.CallBackFunc=function(isOk)" +
            "     logError('[STLR] cos isOk='..tostring(isOk))" +
            "     if not isOk then " + LuaSetStatus("'error'") + " logError('[STLR] cos failed') stub.RetAvatarToken=origRet return end" +
            "     ;(Z.CoroUtil.create_coro_xpcall)(function()" +
            "      local px=require('zproxy.photograph_proxy')" +
            "      local cs=(Z.CancelSource).Rent()" +
            "      local ret=px.UploadPhotoSuccessful({" +
            "       charId=Z.ContainerMgr.CharSerialize.charBase.charId," +
            "       pictureId=r.pictureId," +
            "       funcType=(E.HttpTokenType).HeadProfile," +
            "       data={{pictureUrl=r.objectKey,version=r.version,pictureType=snapType}}" +
            "      },cs:CreateToken())" +
            "      if ret and ret.errCode==0 then " + LuaSetStatus("'done'") + " logError('[STLR] upload done')" +
            // Success: fully disarm every hook and nil the origin globals (Cancel becomes a no-op).
            "       stub.RetAvatarToken=origRet stub.GetPhotoTokenNtf=origGet stub.UploadPhotoResultNtf=origUpR stub.UploadPictureResultNtf=origUpP stub.ReviewAvatarInfoNtf=origRev" +
            "       popupCls.setHeadImg=origSetHead idcardCls.setPhotoData=origSetPhoto cvm.GetHeadOrBodyPhotoToken=origGetToken" +
            "       rawset(_G,'__stlr_up_origRet',nil) rawset(_G,'__stlr_up_origGet',nil) rawset(_G,'__stlr_up_origUpR',nil) rawset(_G,'__stlr_up_origUpP',nil) rawset(_G,'__stlr_up_origRev',nil) rawset(_G,'__stlr_up_origGetToken',nil) rawset(_G,'__stlr_up_origSetHead',nil) rawset(_G,'__stlr_up_origSetPhoto',nil) rawset(_G,'__stlr_up_bytes',nil)" +
            "      else " + LuaSetStatus("'error'") + " logError('[STLR] confirm err '..(ret and tostring(ret.errCode) or '?'))" + " stub.RetAvatarToken=origRet end" +
            "     end,function(e)" + LuaSetStatus("'error'") + " logError('[STLR] coro err '..tostring(e)) end)()" +
            "    end" +
            "    local _bytes=_G.__stlr_up_bytes" +
            "    if not _bytes or #_bytes==0 then " + LuaSetStatus("'error'") + " logError('[STLR] no image bytes') stub.RetAvatarToken=origRet return end" +
            "    " + LuaSetStatus("'uploading'") +
            "    logError('[STLR] calling UploadPictureToCos '..(#_bytes)..'b')" +
            "    Z.UploadMgr:UploadPictureToCos(up,_bytes)" +
            "   end)" +
            "   if not cbOk then " + LuaSetStatus("'error'") + " logError('[STLR] cb err '..tostring(cbErr)) stub.RetAvatarToken=origRet end" +
            "  end" +
            // Diagnostic NTF taps (self-restore on fire, only park status strings).
            "  stub.GetPhotoTokenNtf=function(self,call,vReq) stub.GetPhotoTokenNtf=origGet logError('[STLR] ntf GetPhotoTokenNtf ec='..tostring(vReq and vReq.errCode)) pcall(origGet,self,call,vReq) end" +
            "  stub.UploadPhotoResultNtf=function(self,call,vReq) stub.UploadPhotoResultNtf=origUpR logError('[STLR] ntf UploadPhotoResultNtf ec='..tostring(vReq and vReq.errCode)) pcall(origUpR,self,call,vReq) end" +
            "  stub.UploadPictureResultNtf=function(self,call,vReq) stub.UploadPictureResultNtf=origUpP logError('[STLR] ntf UploadPictureResultNtf ec='..tostring(vReq and vReq.errCode)) pcall(origUpP,self,call,vReq) end" +
            "  stub.ReviewAvatarInfoNtf=function(self,call,vReq) stub.ReviewAvatarInfoNtf=origRev logError('[STLR] ntf ReviewAvatarInfoNtf ec='..tostring(vReq and vReq.errCode)) pcall(origRev,self,call,vReq) end" +
            "  " + LuaSetStatus("'uploading'") +
            "  logError('[STLR] confirm fired snap='..tostring(snapType))" +
            // Do NOT restore previews/GetToken here — keep them armed for retry.
            "  origGetToken(texId,snapType)" +
            " end" +
            " end)" +
            " if not _stlrOk then rawset(_G,'__stlr_up_setup',tostring(_stlrErr)) end");
        var setupErr = _services.Lua.ReadGlobalString("__stlr_up_setup");

        _services.Log.Info($"[CustomProfileImage] upload setup: {(setupErr == null ? "ok" : setupErr)}");
        if (setupErr != null)
        {
            var msg = setupErr;
            var ci  = msg.LastIndexOf(": ");
            if (ci >= 0 && ci < msg.Length - 2) msg = msg.Substring(ci + 2);
            _uploadStatus = msg;
            _window.MarkDirty();
            return;
        }

        _uploadStatus = _loc.T("cpi.status.ready");
        _hooksActive  = true;
        _window.MarkDirty();
    }

    // Restore both preview hooks + GetToken + RetAvatarToken + the 4 NTF taps from the globals.
    private void ClearHooks()
    {
        _services.Lua.DoString(
            "pcall(function()" +
            " local ok,stub=pcall(require,'zservice/photograph_ntf_impl')" +
            " if ok and stub then" +
            "  if _G.__stlr_up_origRet then stub.RetAvatarToken=_G.__stlr_up_origRet end" +
            "  if _G.__stlr_up_origGet then stub.GetPhotoTokenNtf=_G.__stlr_up_origGet end" +
            "  if _G.__stlr_up_origUpR then stub.UploadPhotoResultNtf=_G.__stlr_up_origUpR end" +
            "  if _G.__stlr_up_origUpP then stub.UploadPictureResultNtf=_G.__stlr_up_origUpP end" +
            "  if _G.__stlr_up_origRev then stub.ReviewAvatarInfoNtf=_G.__stlr_up_origRev end" +
            " end" +
            " local cvm=Z.VMMgr.GetVM('camerasys')" +
            " if cvm and _G.__stlr_up_origGetToken then cvm.GetHeadOrBodyPhotoToken=_G.__stlr_up_origGetToken end" +
            " local ok1,popupCls=pcall(require,'ui.view.photo_personalzone_idcard_popup_view')" +
            " if ok1 and popupCls and _G.__stlr_up_origSetHead then popupCls.setHeadImg=_G.__stlr_up_origSetHead end" +
            " local ok2,idcardCls=pcall(require,'ui.view.idcard_view')" +
            " if ok2 and idcardCls and _G.__stlr_up_origSetPhoto then idcardCls.setPhotoData=_G.__stlr_up_origSetPhoto end" +
            " rawset(_G,'__stlr_up_origRet',nil) rawset(_G,'__stlr_up_origGet',nil) rawset(_G,'__stlr_up_origUpR',nil)" +
            " rawset(_G,'__stlr_up_origUpP',nil) rawset(_G,'__stlr_up_origRev',nil)" +
            " rawset(_G,'__stlr_up_origGetToken',nil) rawset(_G,'__stlr_up_origSetHead',nil) rawset(_G,'__stlr_up_origSetPhoto',nil) rawset(_G,'__stlr_up_status',nil)" +
            " rawset(_G,'__stlr_up_bytes',nil)" +
            "end)");
        _hooksActive   = false;
        _lastLuaStatus = null;
        _uploadStatus  = _loc.T("cpi.status.cancelled");
        _window.MarkDirty();
    }

    // ── Native byte push into the Lua VM ──────────────────────────────────────
    //
    // Push a managed byte[] into the game's tolua# VM as a byte-EXACT Lua string global. Lua strings are
    // raw byte arrays (no encoding), so this is the lossless way to hand image bytes to Lua without a file,
    // base64, or chunking. The uploader reads _G.<globalName> and passes it straight to
    // Z.UploadMgr:UploadPictureToCos, whose byte[] param accepts a Lua string as-is.
    //
    // All interop goes through plain System.Reflection over the Il2Cpp-interop types (resolved via
    // StellarInterop.FindType). StellarInterop.FindMethod is count-only (can't disambiguate the several
    // *_pushlstring overloads), so lua_pushlstring is matched by name + param shape by hand. Called on the
    // main (button) thread from ProcessUpload — no dispatch needed.
    private bool PushBytesToLuaGlobal(byte[] bytes, string globalName)
    {
        try
        {
            // LuaInterface.LuaClient is NOT present in the interop assembly, so LuaClient.GetMainState() is
            // unreachable — that was the old "Lua interop types not found" failure. LuaState + LuaDLL ARE
            // present; we get the instance off the captured DoString `__instance` (or a reflected mainState),
            // and drive lua_pushlstring / lua_setglobal off LuaDLL directly.
            var luaStateType = Stellar.Abstractions.Services.StellarInterop.FindType("LuaInterface.LuaState");
            var luaDllType   = Stellar.Abstractions.Services.StellarInterop.FindType("LuaInterface.LuaDLL");
            if (luaStateType == null || luaDllType == null)
            {
                _services.Log.Warning("[CustomProfileImage] PushBytesToLuaGlobal: LuaState/LuaDLL interop types not found");
                return false;
            }

            // Instance source, in order: (1) the LuaState captured off the DoString patch (preferred — it's
            // the actual running main state); (2) a reflected static `mainState` on LuaState, which
            // Il2CppInterop may expose as a PROPERTY or a FIELD, so try both.
            object? mainState = _capturedLuaState;
            string  instSource = "captured";
            if (mainState == null)
            {
                mainState = luaStateType.GetProperty("mainState",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);
                if (mainState != null) instSource = "mainState-prop";
            }
            if (mainState == null)
            {
                mainState = luaStateType.GetField("mainState",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);
                if (mainState != null) instSource = "mainState-field";
            }
            if (mainState == null)
            {
                _services.Log.Warning("[CustomProfileImage] PushBytesToLuaGlobal: no LuaState instance (captured/mainState both null)");
                return false;
            }

            // `L` (the raw lua_State*) lives on LuaStatePtr, inherited by LuaState. In this project's
            // Il2CppInterop the il2cpp instance FIELD is commonly surfaced as a PROPERTY, so try property
            // first (derived then base), then field (derived w/ FlattenHierarchy, then base).
            var luaStatePtrType = Stellar.Abstractions.Services.StellarInterop.FindType("LuaInterface.LuaStatePtr");
            object? lRaw = null;
            string  lSource = "";
            var lProp = luaStateType.GetProperty("L",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?? luaStatePtrType?.GetProperty("L",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (lProp != null) { lRaw = lProp.GetValue(mainState); lSource = "property"; }
            if (lRaw == null)
            {
                var lField = luaStateType.GetField("L",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy)
                    ?? luaStatePtrType?.GetField("L",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (lField != null) { lRaw = lField.GetValue(mainState); lSource = "field"; }
            }
            if (lRaw == null)
            {
                _services.Log.Warning("[CustomProfileImage] PushBytesToLuaGlobal: LuaState.L not found (property nor field)");
                return false;
            }
            var L = (System.IntPtr)lRaw;
            if (L == System.IntPtr.Zero)
            {
                _services.Log.Warning($"[CustomProfileImage] PushBytesToLuaGlobal: L == 0 (inst={instSource}, via {lSource})");
                return false;
            }
            _services.Log.Info($"[CustomProfileImage] LuaState L resolved = 0x{((long)L):X} via {lSource} (inst={instSource})");

            // Match lua_pushlstring(IntPtr, <byte array>, int) by name + shape — NOT the tolua_pushlstring
            // variants, nor the 2-arg ReadOnlySpan overload. The array param may surface as managed byte[]
            // or as Il2CppStructArray<byte> depending on how interop generated the signature, so match by
            // "3 params, [0]=IntPtr, [2]=int, [1] is not string" rather than an exact type array.
            System.Reflection.MethodInfo? pushMethod = null;
            foreach (var m in luaDllType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (m.Name != "lua_pushlstring") continue;
                var ps = m.GetParameters();
                if (ps.Length == 3 && ps[0].ParameterType == typeof(System.IntPtr)
                    && ps[2].ParameterType == typeof(int) && ps[1].ParameterType != typeof(string))
                { pushMethod = m; break; }
            }
            if (pushMethod == null)
            {
                _services.Log.Warning("[CustomProfileImage] PushBytesToLuaGlobal: lua_pushlstring(IntPtr,byte[],int) not found");
                return false;
            }

            // The game protects _G with a strict-global metatable (__newindex rejects any UNDECLARED
            // global write with "Attempt to write to undeclared global variable: ..."), so lua_setglobal
            // is silently blocked and the value stays nil. rawset(_G, key, value) bypasses __newindex —
            // so instead of pushing the value + lua_setglobal, we call rawset via the C API:
            //   getglobal 'rawset'; getglobal '_G'; pushstring key; pushlstring bytes; pcall(3,0,0).
            // string marshals reliably, so exact-signature GetMethod is fine for these three.
            var getGlobal = luaDllType.GetMethod("lua_getglobal",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null,
                new[] { typeof(System.IntPtr), typeof(string) }, null);
            var pushString = luaDllType.GetMethod("lua_pushstring",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null,
                new[] { typeof(System.IntPtr), typeof(string) }, null);
            var pcall = luaDllType.GetMethod("lua_pcall",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null,
                new[] { typeof(System.IntPtr), typeof(int), typeof(int), typeof(int) }, null);
            if (getGlobal == null || pushString == null || pcall == null)
            {
                _services.Log.Warning($"[CustomProfileImage] PushBytesToLuaGlobal: rawset C-API methods not found " +
                    $"(getglobal={getGlobal != null}, pushstring={pushString != null}, pcall={pcall != null})");
                return false;
            }
            // Optional — only for a richer error message on pcall failure; not required for the push.
            var toString = luaDllType.GetMethod("lua_tostring",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null,
                new[] { typeof(System.IntPtr), typeof(int) }, null);

            // Coerce the managed byte[] to whatever the array param actually is. If it already accepts a
            // managed byte[], pass it straight through; otherwise build the interop array (Il2CppStructArray
            // <byte> exposes a byte[] ctor; length-ctor + indexer is the last-resort copy path).
            var arrParamType = pushMethod.GetParameters()[1].ParameterType;
            object byteArg;
            if (arrParamType.IsInstanceOfType(bytes))
                byteArg = bytes;
            else
            {
                var ctorFromArr = arrParamType.GetConstructor(new[] { typeof(byte[]) });
                if (ctorFromArr != null)
                    byteArg = ctorFromArr.Invoke(new object[] { bytes });
                else
                {
                    var lenCtor = arrParamType.GetConstructor(new[] { typeof(long) })
                               ?? arrParamType.GetConstructor(new[] { typeof(int) });
                    if (lenCtor == null)
                    {
                        _services.Log.Warning($"[CustomProfileImage] PushBytesToLuaGlobal: no usable ctor on {arrParamType.FullName}");
                        return false;
                    }
                    var lenArg = lenCtor.GetParameters()[0].ParameterType == typeof(long)
                        ? (object)(long)bytes.Length : (object)bytes.Length;
                    byteArg = lenCtor.Invoke(new[] { lenArg });
                    var indexer = arrParamType.GetProperty("Item");
                    if (indexer == null)
                    {
                        _services.Log.Warning($"[CustomProfileImage] PushBytesToLuaGlobal: no indexer on {arrParamType.FullName}");
                        return false;
                    }
                    for (int i = 0; i < bytes.Length; i++)
                        indexer.SetValue(byteArg, bytes[i], new object[] { i });
                }
            }

            // rawset(_G, globalName, bytes) via the C API — bypasses the strict-global __newindex guard.
            getGlobal.Invoke(null, new object[] { L, "rawset" });       // push the rawset function
            getGlobal.Invoke(null, new object[] { L, "_G" });          // push the globals table
            pushString.Invoke(null, new object[] { L, globalName });   // push key
            pushMethod.Invoke(null, new object[] { L, byteArg, bytes.Length }); // push value (exact bytes)
            int rc = (int)pcall.Invoke(null, new object[] { L, 3, 0, 0 })!;
            if (rc != 0)
            {
                var err = toString?.Invoke(null, new object[] { L, -1 }) as string;
                _services.Log.Warning($"[CustomProfileImage] PushBytesToLuaGlobal: rawset pcall failed rc={rc}" +
                    (err != null ? $" ({err})" : ""));
                return false;
            }

            _services.Log.Info($"[CustomProfileImage] pushed {bytes.Length} bytes to _G.{globalName} (rawset rc=0)");
            return true;
        }
        catch (System.Exception ex)
        {
            _services.Log.Warning($"[CustomProfileImage] PushBytesToLuaGlobal failed: {ex}");
            return false;
        }
    }
}
