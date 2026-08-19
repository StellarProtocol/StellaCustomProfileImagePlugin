namespace Stellar.CustomProfleImage;

public sealed partial class Plugin
{
    private void MakeNamecard()
    {
        if (_avatarHooksActive) { ClearAvatarHooks(); _avatarUploadStatus = ""; }
        ProcessNamecardUpload(_selectedImagePath);
    }

    // ── Namecard upload ───────────────────────────────────────────────────────
    //
    // Same hook lifecycle as the avatar path (see Plugin.Avatar.cs): Make installs only the
    // preview hook (setPhotoData) and the cvm.GetHeadOrBodyPhotoToken override; the confirm
    // installs the RetAvatarToken uploader scoped to that confirm's token; success fully
    // disarms and any error leaves preview + GetToken armed for retry. Namecards use the
    // idcard_view preview and pictureType EProfileHalfBody.
    private void ProcessNamecardUpload(string picked)
    {
        if (!System.IO.File.Exists(picked)) { _namecardUploadStatus = _loc.T("cpi.status.fileNotFound"); _window.MarkDirty(); return; }

        var luaFilePath = picked.Replace(@"\", @"\\");
        var fileUrl     = "file:///" + picked.Replace('\\', '/');

        _namecardUploadStatus = _loc.T("cpi.status.starting");
        _window.MarkDirty();

        if (!_services.Lua.Ready) { _namecardUploadStatus = _loc.T("cpi.status.luaNotReady"); _window.MarkDirty(); return; }

        // Setup runs under pcall; a synchronous error() is parked in a status global and read back
        // via ReadGlobalString (ILua.DoString is fire-and-forget). setupErr == null means success.
        _services.Lua.DoString(
            "_G.__stlr_nc_setup=nil local _stlrOk,_stlrErr=pcall(function()" +
            " if not (Z.EntityMgr).PlayerEnt then error('Need world: enter game first') end" +
            " local stub=require('zservice/photograph_ntf_impl')" +
            " local origRet=stub.RetAvatarToken" +
            " local origGet=stub.GetPhotoTokenNtf" +
            " local origUpR=stub.UploadPhotoResultNtf" +
            " local origUpP=stub.UploadPictureResultNtf" +
            " local origRev=stub.ReviewAvatarInfoNtf" +
            " _G.__stlr_nc_origRet=origRet _G.__stlr_nc_origGet=origGet _G.__stlr_nc_origUpR=origUpR _G.__stlr_nc_origUpP=origUpP _G.__stlr_nc_origRev=origRev" +
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
            // Confirm fired: install the uploader NOW, scoped to this confirm's token.
            "  stub.RetAvatarToken=function(self,call,vReq)" +
            "   local cbOk,cbErr=pcall(function()" +
            "    if vReq.errCode~=0 then " + LuaSetNcStatus("'Token err:'..tostring(vReq.errCode)") + " stub.RetAvatarToken=origRet return end" +
            "    local r=vReq.result if not r then " + LuaSetNcStatus("'No result'") + " stub.RetAvatarToken=origRet return end" +
            "    " + LuaSetNcStatus("'Uploading...'") +
            "    local up=(Z.UploadParm).New()" +
            "    up.TmpSecretId=r.tmpSecretId up.TmpSecretKey=r.tmpSecretKey" +
            "    up.Region=r.region up.TmpToken=r.tmpToken" +
            "    up.ExpireTime=r.expiredTime up.Bucket=r.bucket up.SaveKey=r.objectKey" +
            "    up.CallBackFunc=function(isOk)" +
            "     if not isOk then " + LuaSetNcStatus("'COS failed'") + " stub.RetAvatarToken=origRet return end" +
            "     ;(Z.CoroUtil.create_coro_xpcall)(function()" +
            "      local px=require('zproxy.photograph_proxy')" +
            "      local cs=(Z.CancelSource).Rent()" +
            "      local ret=px.UploadPhotoSuccessful({" +
            "       charId=Z.ContainerMgr.CharSerialize.charBase.charId," +
            "       pictureId=r.pictureId," +
            "       funcType=(E.HttpTokenType).HeadProfile," +
            "       data={{pictureUrl=r.objectKey,version=r.version,pictureType=(E.PictureType).EProfileHalfBody}}" +
            "      },cs:CreateToken())" +
            "      if ret and ret.errCode==0 then " + LuaSetNcStatus("'Done'") +
            // Success: fully disarm every hook and nil the origin globals (Cancel becomes a no-op).
            "       stub.RetAvatarToken=origRet stub.GetPhotoTokenNtf=origGet stub.UploadPhotoResultNtf=origUpR stub.UploadPictureResultNtf=origUpP stub.ReviewAvatarInfoNtf=origRev" +
            "       idcardCls.setPhotoData=origSetPhoto cvm.GetHeadOrBodyPhotoToken=origGetToken" +
            "       _G.__stlr_nc_origRet=nil _G.__stlr_nc_origGet=nil _G.__stlr_nc_origUpR=nil _G.__stlr_nc_origUpP=nil _G.__stlr_nc_origRev=nil _G.__stlr_nc_origGetToken=nil _G.__stlr_nc_origSetPhoto=nil" +
            "      else " + LuaSetNcStatus("'Confirm err:'..(ret and tostring(ret.errCode) or '?')") + " stub.RetAvatarToken=origRet end" +
            "     end,function(e)" + LuaSetNcStatus("'Coro err:'..tostring(e)") + " end)()" +
            "    end" +
            "    local _pf=io.open('" + luaFilePath + "','rb')" +
            "    if not _pf then " + LuaSetNcStatus("'No file " + luaFilePath + "'") + " stub.RetAvatarToken=origRet return end" +
            "    local _bytes=_pf:read('*a') _pf:close()" +
            "    if not _bytes or #_bytes==0 then " + LuaSetNcStatus("'Empty file'") + " stub.RetAvatarToken=origRet return end" +
            "    " + LuaSetNcStatus("'Uploading '..(#_bytes)..'b...'") +
            "    Z.UploadMgr:UploadPictureToCos(up,_bytes)" +
            "   end)" +
            "   if not cbOk then " + LuaSetNcStatus("'CB err:'..tostring(cbErr)") + " stub.RetAvatarToken=origRet end" +
            "  end" +
            // Diagnostic NTF taps (self-restore on fire, only park status strings).
            "  stub.GetPhotoTokenNtf=function(self,call,vReq) stub.GetPhotoTokenNtf=origGet " + LuaSetNcStatus("'NTF:GetPhotoTokenNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origGet,self,call,vReq) end" +
            "  stub.UploadPhotoResultNtf=function(self,call,vReq) stub.UploadPhotoResultNtf=origUpR " + LuaSetNcStatus("'NTF:UploadPhotoResultNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origUpR,self,call,vReq) end" +
            "  stub.UploadPictureResultNtf=function(self,call,vReq) stub.UploadPictureResultNtf=origUpP " + LuaSetNcStatus("'NTF:UploadPictureResultNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origUpP,self,call,vReq) end" +
            "  stub.ReviewAvatarInfoNtf=function(self,call,vReq) stub.ReviewAvatarInfoNtf=origRev " + LuaSetNcStatus("'NTF:ReviewAvatarInfoNtf ec='..tostring(vReq and vReq.errCode)") + " pcall(origRev,self,call,vReq) end" +
            "  " + LuaSetNcStatus("'Namecard confirm fired texId='..tostring(texId)..' snap='..tostring(snapType)") +
            // Do NOT restore preview/GetToken here — keep them armed for retry.
            "  origGetToken(texId,snapType)" +
            " end" +
            " end)" +
            " if not _stlrOk then _G.__stlr_nc_setup=tostring(_stlrErr) end");
        var setupErr = _services.Lua.ReadGlobalString("__stlr_nc_setup");

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

        _namecardUploadStatus = _loc.T("cpi.status.namecardReady");
        _namecardHooksActive  = true;
        _window.MarkDirty();
    }

    private void ClearNamecardHooks()
    {
        _services.Lua.DoString(
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
        _namecardUploadStatus = _loc.T("cpi.status.cancelled");
        _window.MarkDirty();
    }
}
