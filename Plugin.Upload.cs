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
        if (!System.IO.File.Exists(picked)) { _uploadStatus = _loc.T("cpi.status.fileNotFound"); _window.MarkDirty(); return; }

        // Stage the file under our ASCII plugin-cache dir so Lua's io.open (ANSI fopen) can read it
        // even when the user's chosen path contains non-ASCII characters (see Plugin.StagingDir).
        string staged;
        try
        {
            staged = System.IO.Path.Combine(StagingDir(), "stlr_upload.png");
            System.IO.File.Copy(picked, staged, overwrite: true);
        }
        catch (System.Exception ex)
        {
            _uploadStatus = _loc.T("cpi.status.stageCopyFailed") + ex.Message;
            _window.MarkDirty();
            return;
        }

        var luaFilePath = staged.Replace(@"\", @"\\");
        var fileUrl     = "file:///" + staged.Replace('\\', '/');

        _uploadStatus = _loc.T("cpi.status.starting");
        _window.MarkDirty();

        if (!_services.Lua.Ready) { _uploadStatus = _loc.T("cpi.status.luaNotReady"); _window.MarkDirty(); return; }

        // Setup runs under pcall; a synchronous error() is parked in a status global and read back
        // via ReadGlobalString (ILua.DoString is fire-and-forget). setupErr == null means success.
        // Async handlers (server callbacks) use pcall + LuaSetStatus internally.
        _services.Lua.DoString(
            "_G.__stlr_up_setup=nil local _stlrOk,_stlrErr=pcall(function()" +
            " if not (Z.EntityMgr).PlayerEnt then error('Need world: enter game first') end" +
            " local stub=require('zservice/photograph_ntf_impl')" +
            " local origRet=stub.RetAvatarToken" +
            " local origGet=stub.GetPhotoTokenNtf" +
            " local origUpR=stub.UploadPhotoResultNtf" +
            " local origUpP=stub.UploadPictureResultNtf" +
            " local origRev=stub.ReviewAvatarInfoNtf" +
            " _G.__stlr_up_origRet=origRet _G.__stlr_up_origGet=origGet _G.__stlr_up_origUpR=origUpR _G.__stlr_up_origUpP=origUpP _G.__stlr_up_origRev=origRev" +
            // Avatar preview hook (setHeadImg fires on popup open — armed from Apply, stays armed).
            " local popupCls=require('ui.view.photo_personalzone_idcard_popup_view')" +
            " local origSetHead=popupCls.setHeadImg _G.__stlr_up_origSetHead=origSetHead" +
            " popupCls.setHeadImg=function(self)" +
            "  pcall(function()" +
            "   local rimg=((self.uiBinder).binder_head).rimg_portrait" +
            "   rimg:AsyncLoadUrlImage('" + fileUrl + "',function() end)" +
            "  end)" +
            " end" +
            // Namecard preview hook (setPhotoData — different view class, no conflict with setHeadImg).
            " local idcardCls=require('ui.view.idcard_view')" +
            " local origSetPhoto=idcardCls.setPhotoData _G.__stlr_up_origSetPhoto=origSetPhoto" +
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
            " local origGetToken=cvm.GetHeadOrBodyPhotoToken _G.__stlr_up_origGetToken=origGetToken" +
            " cvm.GetHeadOrBodyPhotoToken=function(texId,snapType)" +
            // Confirm fired: install the uploader NOW, scoped to this confirm's token. pictureType is
            // snapType (head→EProfileSnapShot / halfbody→EProfileHalfBody) — auto-detects card type.
            "  stub.RetAvatarToken=function(self,call,vReq)" +
            "   local cbOk,cbErr=pcall(function()" +
            "    if vReq.errCode~=0 then " + LuaSetStatus("'Token err:'..tostring(vReq.errCode)") + " stub.RetAvatarToken=origRet return end" +
            "    local r=vReq.result if not r then " + LuaSetStatus("'No result'") + " stub.RetAvatarToken=origRet return end" +
            "    " + LuaSetStatus("'Uploading...'") +
            "    local up=(Z.UploadParm).New()" +
            "    up.TmpSecretId=r.tmpSecretId up.TmpSecretKey=r.tmpSecretKey" +
            "    up.Region=r.region up.TmpToken=r.tmpToken" +
            "    up.ExpireTime=r.expiredTime up.Bucket=r.bucket up.SaveKey=r.objectKey" +
            "    up.CallBackFunc=function(isOk)" +
            "     if not isOk then " + LuaSetStatus("'COS failed'") + " stub.RetAvatarToken=origRet return end" +
            "     ;(Z.CoroUtil.create_coro_xpcall)(function()" +
            "      local px=require('zproxy.photograph_proxy')" +
            "      local cs=(Z.CancelSource).Rent()" +
            "      local ret=px.UploadPhotoSuccessful({" +
            "       charId=Z.ContainerMgr.CharSerialize.charBase.charId," +
            "       pictureId=r.pictureId," +
            "       funcType=(E.HttpTokenType).HeadProfile," +
            "       data={{pictureUrl=r.objectKey,version=r.version,pictureType=snapType}}" +
            "      },cs:CreateToken())" +
            "      if ret and ret.errCode==0 then " + LuaSetStatus("'Done'") +
            // Success: fully disarm every hook and nil the origin globals (Cancel becomes a no-op).
            "       stub.RetAvatarToken=origRet stub.GetPhotoTokenNtf=origGet stub.UploadPhotoResultNtf=origUpR stub.UploadPictureResultNtf=origUpP stub.ReviewAvatarInfoNtf=origRev" +
            "       popupCls.setHeadImg=origSetHead idcardCls.setPhotoData=origSetPhoto cvm.GetHeadOrBodyPhotoToken=origGetToken" +
            "       _G.__stlr_up_origRet=nil _G.__stlr_up_origGet=nil _G.__stlr_up_origUpR=nil _G.__stlr_up_origUpP=nil _G.__stlr_up_origRev=nil _G.__stlr_up_origGetToken=nil _G.__stlr_up_origSetHead=nil _G.__stlr_up_origSetPhoto=nil" +
            "      else " + LuaSetStatus("'Confirm err:'..(ret and tostring(ret.errCode) or '?')") + " stub.RetAvatarToken=origRet end" +
            "     end,function(e)" + LuaSetStatus("'Coro err:'..tostring(e)") + " end)()" +
            "    end" +
            "    local _pf=io.open('" + luaFilePath + "','rb')" +
            "    if not _pf then " + LuaSetStatus("'No file " + luaFilePath + "'") + " stub.RetAvatarToken=origRet return end" +
            "    local _bytes=_pf:read('*a') _pf:close()" +
            "    if not _bytes or #_bytes==0 then " + LuaSetStatus("'Empty file'") + " stub.RetAvatarToken=origRet return end" +
            "    " + LuaSetStatus("'Uploading '..(#_bytes)..'b...'") +
            "    Z.UploadMgr:UploadPictureToCos(up,_bytes)" +
            "   end)" +
            "   if not cbOk then " + LuaSetStatus("'CB err:'..tostring(cbErr)") + " stub.RetAvatarToken=origRet end" +
            "  end" +
            // Diagnostic NTF taps (self-restore on fire, only park status strings).
            "  stub.GetPhotoTokenNtf=function(self,call,vReq) stub.GetPhotoTokenNtf=origGet " + LuaSetStatus("'NTF:GetPhotoTokenNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origGet,self,call,vReq) end" +
            "  stub.UploadPhotoResultNtf=function(self,call,vReq) stub.UploadPhotoResultNtf=origUpR " + LuaSetStatus("'NTF:UploadPhotoResultNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origUpR,self,call,vReq) end" +
            "  stub.UploadPictureResultNtf=function(self,call,vReq) stub.UploadPictureResultNtf=origUpP " + LuaSetStatus("'NTF:UploadPictureResultNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origUpP,self,call,vReq) end" +
            "  stub.ReviewAvatarInfoNtf=function(self,call,vReq) stub.ReviewAvatarInfoNtf=origRev " + LuaSetStatus("'NTF:ReviewAvatarInfoNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origRev,self,call,vReq) end" +
            "  " + LuaSetStatus("'Confirm fired texId='..tostring(texId)..' snap='..tostring(snapType)") +
            // Do NOT restore previews/GetToken here — keep them armed for retry.
            "  origGetToken(texId,snapType)" +
            " end" +
            " end)" +
            " if not _stlrOk then _G.__stlr_up_setup=tostring(_stlrErr) end");
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
            " _G.__stlr_up_origRet=nil _G.__stlr_up_origGet=nil _G.__stlr_up_origUpR=nil" +
            " _G.__stlr_up_origUpP=nil _G.__stlr_up_origRev=nil" +
            " _G.__stlr_up_origGetToken=nil _G.__stlr_up_origSetHead=nil _G.__stlr_up_origSetPhoto=nil _G.__stlr_up_status=nil" +
            "end)");
        _hooksActive  = false;
        _uploadStatus = _loc.T("cpi.status.cancelled");
        _window.MarkDirty();
    }
}
