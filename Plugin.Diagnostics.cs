namespace Stellar.CustomProfleImage;

public sealed partial class Plugin
{
    // ── File-read diagnostic ──────────────────────────────────────────────────
    //
    // Lets a user confirm the root cause of the "preview right, upload = normal photo" bug on
    // their own machine: the game's Lua io.open (ANSI CRT fopen) fails on non-ASCII paths. Users
    // whose own path is pure ASCII can't reproduce it, so this synthesises CJK-named probe files
    // under the plugin cache and reports which ones Lua could actually read. The composed result
    // string stays English/technical — it's a debug readout, only the button + guard are localized.
    private void TestFileRead()
    {
        if (!_services.Lua.Ready) { _diagStatus = _loc.T("cpi.status.luaNotReady"); _window.MarkDirty(); return; }

        var diagDir = System.IO.Path.Combine(StagingDir(), "diag");
        try
        {
            System.IO.Directory.CreateDirectory(diagDir);
            var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

            // Each probe drives its WriteAllBytes and its ProbeLuaRead off the SAME path string,
            // so the read is self-consistent with what was actually written.
            var asciiPath = System.IO.Path.Combine(diagDir, "ascii_test.png");
            System.IO.File.WriteAllBytes(asciiPath, payload);
            var asciiRes = ProbeLuaRead(asciiPath);

            var cjkNamePath = System.IO.Path.Combine(diagDir, "测试_头像.png");
            System.IO.File.WriteAllBytes(cjkNamePath, payload);
            var cjkNameRes = ProbeLuaRead(cjkNamePath);

            var cjkDirPath = System.IO.Path.Combine(diagDir, "测试目录", "pic.png");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cjkDirPath)!);
            System.IO.File.WriteAllBytes(cjkDirPath, payload);
            var cjkDirRes = ProbeLuaRead(cjkDirPath);

            var result = $"ascii: {asciiRes} | CJK-name: {cjkNameRes} | CJK-dir: {cjkDirRes}";

            if (_selectedImagePath.Length > 0)
                result += " | selected: " + ProbeLuaRead(_selectedImagePath);

            _diagStatus = result;
            _services.Log.Info("[CustomProfileImage] " + result);
        }
        catch (System.Exception ex)
        {
            _diagStatus = "Diag error: " + ex.Message;
            _services.Log.Warning("[CustomProfileImage] TestFileRead failed: " + ex.Message);
        }
        finally
        {
            // Recursive cleanup — leave nothing behind under the plugin cache.
            try { if (System.IO.Directory.Exists(diagDir)) System.IO.Directory.Delete(diagDir, recursive: true); } catch { }
            _window.MarkDirty();
        }
    }

    // Attempt the read the exact way the uploader does — Lua io.open(path,'rb') — and read the
    // parked byte count (or 'FAILED') back directly. Mirrors the __stlr_av_setup readback in
    // Plugin.Avatar.cs: DoString parks into _G, then ReadGlobalString pulls the value by bare name.
    private string ProbeLuaRead(string path)
    {
        var esc = path.Replace(@"\", @"\\").Replace("'", @"\'");
        _services.Lua.DoString(
            "_G.__stlr_diag=(function() local f=io.open('" + esc + "','rb')" +
            " if not f then return 'FAILED' end" +
            " local b=f:read('*a') f:close() return tostring(#b)..'b' end)()");
        return _services.Lua.ReadGlobalString("__stlr_diag") ?? "nil";
    }
}
