using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CustomProfleImage;

// More house-kit building blocks (Stellar Design Language §4; ported from LoadoutSwitcher / Maestro
// Plugin.Style.Blocks.cs): embedded art + the per-size header glyph, the accent icon tile, the ToolHeader, the 24 px
// status chip and the per-language caption cache.
public sealed partial class Plugin
{
    // ── embedded PNGs ─────────────────────────────────────────────────────────────────────────────────────────────
    // Read once per resource (csproj EmbeddedResource LogicalName "Stellar.CustomProfleImage.<file>") and kept:
    // ImageElement Funcs are polled. A missing resource caches null (the element then draws nothing) instead of
    // re-reading every poll.
    private static readonly Dictionary<string, byte[]?> _pngCache = new();

    private static byte[]? CachedPng(string file)
    {
        if (_pngCache.TryGetValue(file, out var png)) return png;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.CustomProfleImage." + file);
            if (s != null) { using var ms = new System.IO.MemoryStream(); s.CopyTo(ms); png = ms.ToArray(); }
        }
        catch { png = null; }
        _pngCache[file] = png;
        return png;
    }

    // Glyphs ship one pixel-hinted bitmap per size (house icon rule: never a master scaled down — resources/icons, drawn
    // by tools/icons/gen-header-icon.py and gen-check-icon.py), so each element asks for the PIXEL size it draws at.
    // The launcher tile keeps its own 64 px icon (Plugin.cs LoadIconPng).
    private sealed class SizedIcon
    {
        public readonly string Prefix;
        public readonly int[]  Sizes;          // ascending; keep in sync with the generator's SIZES
        public float   Scale = -1f;
        public byte[]? Picked;
        public SizedIcon(string prefix, int[] sizes) { Prefix = prefix; Sizes = sizes; }
    }

    private readonly SizedIcon _headerIcon = new("customprofleimage-", new[] { 16, 18, 24, 32, 36, 48 });
    // The check mark that replaced "✓" (U+2713 has no ink in the bold TMP atlas — a quad, nothing visible; bridge
    // ui.inspect 2026-11-20): HOW TO done badges + the matching size chip, both 12 canvas units.
    private readonly SizedIcon _checkIcon  = new("check-", new[] { 6, 8, 9, 12, 15, 18, 24 });
    private const int CheckGlyph = 12;

    // `units` canvas units × the live canvas→screen scale (1.0 at 1440p, 0.75 at 1080p): an exact size (±0.05 px) draws
    // 1:1, else the smallest LARGER drawing, else the largest. Cached per icon until the scale changes, so the per-refresh
    // Func is two property reads + a float compare and hands back the SAME array (ImageElement.Live compares by
    // reference). Every element of one icon uses the same `units`.
    private byte[]? IconPng(SizedIcon icon, int units)
    {
        var fw = _services.Framework;
        float scale = fw.CanvasWidth > 1 && fw.ScreenWidth > 0 ? (float)fw.ScreenWidth / fw.CanvasWidth : 1f;
        if (scale == icon.Scale && icon.Picked != null) return icon.Picked;
        icon.Scale = scale;
        float want = units * scale;
        int px = icon.Sizes[icon.Sizes.Length - 1];
        for (int i = 0; i < icon.Sizes.Length; i++)
            if (icon.Sizes[i] >= want - 0.05f) { px = icon.Sizes[i]; break; }
        return icon.Picked = CachedPng(icon.Prefix + px + ".png") ?? icon.Picked;
    }

    private HudElement CheckImage(Func<ColorRgba?>? tint = null)
        => new ImageElement(() => IconPng(_checkIcon, CheckGlyph), CheckGlyph, CheckGlyph) { Live = true, Tint = tint };

    // ── accent icon tile + ToolHeader ─────────────────────────────────────────────────────────────────────────────
    // A rounded tile in the LIVE theme accent with the flat white glyph centred (art can't follow the theme, so the colour
    // lives in the tile). 40 px tile → 24 px glyph, 8 px all round; radius 9 (≈ 22 % of the side). The fixed-width Cell
    // pins the column (Cell / Selectable force-expand a child's width — the icon-squish gotcha); the Row keeps it square.
    private const float ToolIconSize  = 40f;
    private const int   ToolGlyphSize = 24;

    private HudElement IconBox(Func<byte[]?> png, float size, int glyph)
    {
        float pad = (size - glyph) / 2f;
        return new CellElement(new BubbleElement(
            // Live: re-pull per refresh so a resolution / UI-scale change swaps in the matching pre-drawn size.
            new RowElement(new HudElement[] { new ImageElement(png, glyph, glyph) { Live = true } }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(size * 0.22f), PaddingX = pad, PaddingY = pad,
        }, Width: size);
    }

    // Window title block: 40 px icon + bold title over ONE live status line. `statusWidth` pins the line (NoWrap +
    // Ellipsize), so a long error never widens the window or wraps into a second row.
    private HudElement ToolHeader(Func<string> title, Func<string> status, Func<ColorRgba> statusColor, float statusWidth)
        => new RowElement(new HudElement[]
        {
            IconBox(() => IconPng(_headerIcon, ToolGlyphSize), ToolIconSize, ToolGlyphSize),
            new ColumnElement(new HudElement[]
            {
                new TextElement(title, Color: () => TextColor(), Emphasis: true, NoWrap: true),
                new TextElement(status, Color: () => statusColor(), Width: statusWidth, NoWrap: true) { Ellipsize = true },
            }, Gap: 2f),
        }, Gap: 10f);

    // ── 24 px status chip (the PREVIEW size check) ────────────────────────────────────────────────────────────────
    // Not clickable. Three COMPLETE chips swapped by ConditionalElements wrapping the whole chip (bold can't toggle live;
    // never a Conditional inside a bubble's measured child), all in the SAME fixed-width cell with the same 24 px strut,
    // so a state change never moves anything (rule 14). Bubble.MaxWidth pins the width (a MaxWidth-0 bubble under a
    // Conditional caps itself at the 100 px default — MinimalNameplate, KB Stellar-Design-Language).
    //   off  (no image)      = muted regular text on the tile fill
    //   match    = a check icon + bold label, both in the status green, on a STRONGER 32 % green wash (reads clearly
    //              green — user 2026-11-20); the icon sits at the chip's left padding and the label centres in the rest
    //              (no text-measure API, so the pair can't be centred as a group; the chip itself never changes size)
    //   mismatch = bold amber text on a 22 % wash
    private const float ChipHeight   = 24f;
    private const float ChipPadX     = 8f;
    private const float ChipIconGap  = 6f;
    private const float OkChipWash   = 0.32f;

    private HudElement ChipShell(HudElement[] content, Func<ColorRgba> fill, float width)
    {
        var items = new HudElement[content.Length + 1];
        items[0] = new SpacerElement(0f, ChipHeight);   // height strut = the side (house rule 4: no stacked spacers)
        Array.Copy(content, 0, items, 1, content.Length);
        return new BubbleElement(new RowElement(items, Gap: 0.01f, Justify: RowJustify.Center))
        {
        FillColorFn = () => fill(),
            CornerRadius = RoundRadius(ChipHeight), PaddingX = 0f, PaddingY = 0f, MaxWidth = width,
        };
    }

    private HudElement SizeChip(Func<bool> hasImage, Func<bool> match,
        Func<string> offLabel, Func<string> okLabel, Func<string> badLabel, float width)
    {
        var okText  = new ContrastCache();
        var badText = new ContrastCache();
        float labelW = width - 2f * ChipPadX;
        ColorRgba OkFill()  => Over(Menu.Success with { A = OkChipWash }, PanelColor());
        ColorRgba BadFill() => Over(Menu.Caution with { A = ChipWashAlpha }, PanelColor());
        ColorRgba OkInk()   => okText.Get(Menu.Success, OkFill(), PillMinContrast);

        HudElement Bold(Func<string> text, Func<ColorRgba> color, float w) => new TextElement(text, Color: () => color(),
            Width: w, Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = true, Ellipsize = true, CenterCaps = true };

        // Icon + gap + label = labelW, so the ok chip lays out exactly like the text-only ones.
        var ok  = ChipShell(new HudElement[]
                            {
                                CheckImage(() => OkInk()),
                                new SpacerElement(ChipIconGap - 1f, 1f),   // a SpacerElement(w) takes w + 1
                                Bold(okLabel, OkInk, labelW - CheckGlyph - ChipIconGap),
                            },
                            () => Fade(OkFill()), width);
        var bad = ChipShell(new[] { Bold(badLabel, () => badText.Get(Menu.Caution, BadFill(), PillMinContrast), labelW) },
                            () => Fade(BadFill()), width);
        var off = ChipShell(new HudElement[] { new TextElement(offLabel, Color: () => MutedColor(), Width: labelW,
                                Align: TextAlign.Center, NoWrap: true) { Ellipsize = true } },
                            () => Fade(TileColor()), width);
        return new CellElement(new ConditionalElement(hasImage, new ConditionalElement(match, ok, bad), off), Width: width);
    }

    // ── per-language strings ──────────────────────────────────────────────────────────────────────────────────────
    // Section captions are UPPERCASE: upper-cased once per language (keyed by the localized source string), so the
    // polled Func hands back a cached string instead of allocating ToUpperInvariant() every refresh.
    private readonly Dictionary<string, (string Src, string Up)> _upperCache = new();

    private Func<string> UpperLoc(string key) => () =>
    {
        var src = _loc.T(key);
        if (_upperCache.TryGetValue(key, out var e) && string.Equals(e.Src, src, StringComparison.Ordinal)) return e.Up;
        var up = src.ToUpperInvariant();
        _upperCache[key] = (src, up);
        return up;
    };
}
