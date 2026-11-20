using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CustomProfleImage;

/// <summary>
/// The main window in the house style (approved prototype option A, 2026-11-20 —
/// <c>_scratch\prototypes\customprofileimage-redesign-APPROVED.md</c>):
///   ToolHeader  [40 px accent tile | bold title over ONE live, coloured status line]
///   PREVIEW     fixed recessed frame (image aspect-FIT) · "This image: W × H px" · Avatar / Namecard size chips
///   IMAGE       file-name tile + Neutral "Choose…" pill          HOW TO  4 steps that double as progress
///   footer      Primary "Apply" · Caution "Cancel" · coloured status text
/// Every line is pinned (struts, fixed cells, fixed-width pills / chips, a fixed-height step viewport), so the window
/// keeps ONE size in every state. No per-frame work of its own: the framework's scheduled refresh polls the Funcs, which
/// only compare fields and hand back cached strings (Plugin-Performance-Rules §2/§5). State is READ from the upload flow
/// (<c>Plugin.Upload.cs</c>), never written here.
/// </summary>
public sealed partial class Plugin
{
    // Geometry (canvas units). 560 window − 2 × 12 GlassMenu body padding = 536 inside.
    private const float WindowWidth   = 560f;
    private const float BodyWidth     = 536f;
    private const float LeftColW      = 196f;                          // PREVIEW panel
    private const float ColGap        = 8f;
    private const float RightColW     = BodyWidth - LeftColW - ColGap; // 332: IMAGE + HOW TO
    private const float PanelPadX     = 10f;
    private const float FrameW        = LeftColW - 2f * PanelPadX;     // 176
    private const float FrameH        = 250f;
    private const float FitMargin     = 4f;                            // image inset inside the frame → 168 × 242 fit box
    private const float ChooseW       = 118f;
    private const float FileTileW     = RightColW - 2f * PanelPadX - ColGap - ChooseW;   // 186
    // The HOW TO steps sit in a FIXED-height viewport: a Row centres its children vertically and the toolkit has no
    // stretch, so the right column must come out exactly as tall as the left one (else it floats mid-height). Left =
    // 2×8 pad + caption + 6 + 250 frame + 6 + 18 size line + 6 + 24 chip + 6 + 24 chip = 356 + caption; right = IMAGE
    // (2×8 + caption + 6 + 28) + 8 + HOW TO (2×8 + caption + 6 + viewport) = 80 + 2×caption + viewport → 276 − caption.
    // The viewport also absorbs locale-to-locale wrap differences (a long translation scrolls, the window never grows).
    private const float StepsViewH    = 260f;
    private const float ScrollInset   = 9f;                            // the scroll viewport's scrollbar inset
    private const float StepPadX      = 6f;
    private const float StepBadge     = 22f;
    private const float StepTextW     = RightColW - 2f * PanelPadX - ScrollInset - 2f * StepPadX - StepBadge - 8f - 3f;   // 258
    private const float SizeLineH     = 18f;
    private const float ApplyW        = 150f;
    private const float CancelW       = 100f;
    private const float HeaderStatusW = BodyWidth - ToolIconSize - 10f;   // 486

    // ── UI state, derived from the upload flow ────────────────────────────────────────────────────────────────────
    private enum Stage { Empty, Picked, Armed, Uploading, Done, Failed, Error }

    // Field compares only (no allocation): the hooks + the mirrored Lua code drive the armed stages; otherwise any status
    // text that isn't one of the flow's neutral messages is an error from the C# side (file not found, Lua not ready,
    // "Need world…" from the setup, …).
    private Stage CurrentStage()
    {
        if (_hooksActive)
            return _lastLuaStatus switch
            {
                "uploading" => Stage.Uploading,
                "done"      => Stage.Done,
                "error"     => Stage.Failed,
                _           => Stage.Armed,
            };
        if (IsErrorStatus()) return Stage.Error;
        return _selectedImagePath.Length == 0 ? Stage.Empty : Stage.Picked;
    }

    private bool IsErrorStatus()
    {
        var s = _uploadStatus;
        return s.Length > 0 && s != _loc.T("cpi.status.cancelled") && s != _loc.T("cpi.status.starting")
                            && s != _loc.T("cpi.status.ready");
    }

    // The HOW TO step that is "current" (0-based); 4 = every step done.
    private int CurrentStep() => CurrentStage() switch
    {
        Stage.Empty     => 0,
        Stage.Picked    => 1,
        Stage.Armed     => 2,
        Stage.Uploading => 3,
        Stage.Done      => 4,
        _               => _selectedImagePath.Length == 0 ? 0 : 1,   // Failed / Error → back to "Press Apply"
    };

    private string HeaderStatus() => CurrentStage() switch
    {
        Stage.Empty     => _loc.T("cpi.header.empty"),
        Stage.Picked    => PickedLine(),
        Stage.Armed     => _loc.T("cpi.header.ready"),
        Stage.Uploading => _loc.T("cpi.status.uploading"),
        Stage.Done      => _loc.T("cpi.status.done"),
        Stage.Failed    => _loc.T("cpi.header.failed"),
        _               => _uploadStatus,
    };

    private string FooterStatus() => CurrentStage() switch
    {
        Stage.Armed     => _loc.T("cpi.foot.ready"),
        Stage.Uploading => _loc.T("cpi.status.uploading"),
        Stage.Done      => _loc.T("cpi.status.done"),
        Stage.Failed    => _loc.T("cpi.foot.failed"),
        Stage.Error     => _uploadStatus,
        // Empty / Picked: only the "cancelled" confirmation of a Cancel click, else nothing.
        _               => _uploadStatus,
    };

    // Raw (un-contrasted) status colour for a stage; `idle` = the colour of the Empty / Picked line.
    private ColorRgba StageColor(Stage s, ColorRgba idle) => s switch
    {
        Stage.Armed or Stage.Uploading => Menu.MenuAccent,
        Stage.Done                     => Menu.Success,
        Stage.Failed or Stage.Error    => Menu.Danger,
        _                              => idle,
    };

    // "<file> · press Apply" / "This image: W × H px": formatted once per (value, language), not per refresh.
    private string? _pickedTpl, _pickedName, _pickedLine;
    private string PickedLine()
    {
        var tpl = _loc.T("cpi.header.picked");
        if (_pickedLine == null || !string.Equals(tpl, _pickedTpl, StringComparison.Ordinal)
                                || !string.Equals(_selectedFileName, _pickedName, StringComparison.Ordinal))
        {
            _pickedTpl = tpl; _pickedName = _selectedFileName;
            _pickedLine = string.Format(tpl, _selectedFileName);
        }
        return _pickedLine;
    }

    private string? _sizeTpl, _sizeLine;
    private int     _sizeW, _sizeH;
    private string SizeLine()
    {
        if (_previewPxW <= 0) return "";
        var tpl = _loc.T("cpi.preview.size");
        if (_sizeLine == null || !string.Equals(tpl, _sizeTpl, StringComparison.Ordinal)
                              || _sizeW != _previewPxW || _sizeH != _previewPxH)
        {
            _sizeTpl = tpl; _sizeW = _previewPxW; _sizeH = _previewPxH;
            _sizeLine = string.Format(tpl, _previewPxW, _previewPxH);
        }
        return _sizeLine;
    }

    // ── window ────────────────────────────────────────────────────────────────────────────────────────────────────
    private IWindowControl RegisterWindow()
    {
        var headerColor = new ContrastCache();
        var footColor   = new ContrastCache();

        var root = new ColumnElement(new HudElement[]
        {
            ToolHeader(() => _loc.T("cpi.title"), HeaderStatus,
                () => headerColor.Get(StageColor(CurrentStage(), _selectedImagePath.Length == 0 ? MutedColor() : TextColor()),
                                      WindowBg(), PillMinContrast),
                HeaderStatusW),
            new RowElement(new HudElement[]
            {
                new CellElement(PreviewPanel(), Width: LeftColW),
                new CellElement(new ColumnElement(new HudElement[] { ImagePanel(), HowToPanel() }, Gap: ColGap),
                                Width: RightColW),
            }, Gap: ColGap),
            new RowElement(new HudElement[]
            {
                PrimaryButton(() => _loc.T("cpi.apply"), ApplyUpload, ApplyW,
                    () => !_dialogOpen && _selectedImagePath.Length > 0 && CurrentStage() != Stage.Uploading),
                CautionButton(() => _loc.T("cpi.cancel"), ClearHooks, CancelW,
                    () => CurrentStage() is Stage.Armed or Stage.Failed),
                new CellElement(new TextElement(FooterStatus,
                        Color: () => footColor.Get(StageColor(CurrentStage(), TextColor()), WindowBg(), PillMinContrast),
                        NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true },
                    Weight: 1f),
            }, Gap: 8f),
        }, Gap: 8f);

        return _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "customprofleimage.main",
                Title:       _loc.T("cpi.title"),
                // Constant default (never derived from ScreenWidth — "reset all HUD" reads it as 0): 20 px in from the
                // top-right corner. 0 height = content-sized; the content itself never changes height.
                DefaultRect: new WindowRect(20f, 20f, WindowWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false, Anchor = WindowAnchor.TopRight,
              // Gameplay tool: upload requires being in-world (checks Z.EntityMgr.PlayerEnt).
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root:    root,
            OnClose: () => _window.SetVisible(false)));
    }

    // ── PREVIEW ───────────────────────────────────────────────────────────────────────────────────────────────────
    // ImageElement sizes are fixed at build time, so the picture is aspect-FIT into the fixed frame here and the window
    // is rebuilt when a new file is picked (RebuildWindow) — the frame, and so the window, keep one size.
    private HudElement PreviewPanel()
    {
        var bytes = _previewPngBytes;
        HudElement content;
        if (bytes != null && _previewPxW > 0 && _previewPxH > 0)
        {
            float boxW = FrameW - 2f * FitMargin, boxH = FrameH - 2f * FitMargin;
            float scale = MathF.Min(boxW / _previewPxW, boxH / _previewPxH);
            int iw = Math.Max(1, (int)MathF.Round(_previewPxW * scale));
            int ih = Math.Max(1, (int)MathF.Round(_previewPxH * scale));
            content = new ImageElement(() => bytes, iw, ih);
        }
        else
        {
            content = new TextElement(() => _loc.T("cpi.preview.empty"), Color: () => MutedColor(),
                Width: FrameW - 2f * PanelPadX, Align: TextAlign.Center);
        }

        var frame = new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, FrameH),   // height strut: the frame is FrameH in every state
                content,
            }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            // The recessed fill (prototype --rec) is translucent black, so it fades with the window by itself.
            FillColorFn = () => DisabledShade, CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
        };

        Func<bool> hasImage = () => _previewPxW > 0;
        return SectionPanel("cpi.preview", null, new HudElement[]
        {
            frame,
            FixedLine(SizeLineH, new TextElement(SizeLine, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }),
            SizeChip(hasImage, () => _avatarMatch,
                () => _loc.T("cpi.chip.avatar"), () => _loc.T("cpi.chip.avatarOk"), () => _loc.T("cpi.chip.avatarBad"), FrameW),
            SizeChip(hasImage, () => _namecardMatch,
                () => _loc.T("cpi.chip.namecard"), () => _loc.T("cpi.chip.namecardOk"), () => _loc.T("cpi.chip.namecardBad"), FrameW),
        });
    }

    // ── IMAGE ─────────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement ImagePanel()
    {
        var fileTile = new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(10f, PillHeight),
                new TextElement(() => _selectedImagePath.Length > 0 ? _selectedFileName : _loc.T("cpi.noFile"),
                    Color: () => _selectedImagePath.Length > 0 ? TextColor() : MutedColor(),
                    Width: FileTileW - 20f, NoWrap: true) { Ellipsize = true },
                new SpacerElement(10f, PillHeight),
            }, Gap: 0.01f))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = FileTileW,
        };
        return SectionPanel("cpi.image", "cpi.image.hint", new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                fileTile,
                NeutralButton(() => _loc.T("cpi.chooseFile"), ChooseFile, ChooseW, PanelColor, () => !_dialogOpen),
            }, Gap: ColGap),
        });
    }

    // ── HOW TO ────────────────────────────────────────────────────────────────────────────────────────────────────
    // Four steps that double as progress: done = green ✓ badge + muted text; current = accent badge on an accent-tinted
    // row; pending = a plain number badge. Labels and colours are Funcs over CurrentStep(), so nothing is rebuilt.
    private static readonly string[] StepKeys = { "cpi.howto.step1", "cpi.howto.step2", "cpi.howto.step3", "cpi.howto.step4" };
    private static readonly string[] StepNumbers = { "1", "2", "3", "4" };

    private HudElement HowToPanel()
    {
        var steps = new HudElement[StepKeys.Length];
        for (int i = 0; i < steps.Length; i++) steps[i] = StepRow(i);
        return SectionPanel("cpi.howto.title", "cpi.howto.hint", new HudElement[]
        {
            new ScrollElement(new ColumnElement(steps, Gap: 4f), StepsViewH) { FixedHeight = true },
        });
    }

    private HudElement StepRow(int i)
    {
        var badgeText = new ContrastCache();
        var badgeSolid = new SolidLabelCache();
        bool Done() => CurrentStep() > i;
        bool Cur()  => CurrentStep() == i;
        ColorRgba RowBg() => Cur() ? Over(Menu.MenuAccent with { A = CurrentStepMix }, PanelColor()) : PanelColor();
        ColorRgba DoneFill() => Over(Menu.Success with { A = ChipWashAlpha }, RowBg());

        var badge = new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, StepBadge),
                new TextElement(() => Done() ? "✓" : StepNumbers[i],
                    Color: () => Done() ? badgeText.Get(Menu.Success, DoneFill(), PillMinContrast)
                               : Cur()  ? badgeSolid.Get(AccentColor())
                                        : MutedColor(),
                    Width: StepBadge, Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = true, CenterCaps = true },
            }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(Done() ? DoneFill() : Cur() ? AccentColor() : TileColor()),
            CornerRadius = RoundRadius(StepBadge), PaddingX = 0f, PaddingY = 0f, MaxWidth = StepBadge,
        };

        var key = StepKeys[i];
        return new BubbleElement(new RowElement(new HudElement[]
            {
                badge,
                new TextElement(() => _loc.T(key), Color: () => Done() ? MutedColor() : TextColor(), Width: StepTextW),
            }, Gap: 8f))
        {
            FillColorFn = () => Cur() ? Fade(RowBg()) : Transparent,
            CornerRadius = TileRadius, PaddingX = StepPadX, PaddingY = 4f,
        };
    }
}
