namespace RynthCore.PluginSdk;

// ─── Plugin windows: the engine's look (engine API v89) ─────────────────────
// RynthCoreHost.GetUiColor / GetUiFont / SetPanelOpen, for the windows a plugin
// declares in its sections manifest ("windows") and draws itself through
// RynthPluginRenderEmbed (RynthCore docs/PLUGIN_WINDOWS.md). Check
// RynthCoreHost.HasUiTheme / HasSetPanelOpen first: older engines have neither.
// The numbers match the engine's UiColorToken / UiFontToken and only grow.

/// <summary>A colour of the player's theme (RynthCoreHost.GetUiColor).</summary>
public enum RynthUiColor
{
    Text = 0, TextMuted = 1,
    /// <summary>The theme's accent (teal by default): titles, links, the current item.</summary>
    Accent = 2,
    /// <summary>The highlight (amber by default): warnings, things to notice.</summary>
    Highlight = 3,
    Button = 4, Border = 5, Field = 6, Selected = 7, RowAlt = 8, Background = 9, PanelGlass = 10,
    AccentFill = 11, BoxBg = 12, FrameBg = 13, FrameHover = 14, FrameActive = 15,
    /// <summary>Fixed, whatever the theme: good / bad.</summary>
    Green = 16, Red = 17,
    TitleBar = 18, WindowBorder = 19,
    /// <summary>v90: the theme's own muted text (<see cref="TextMuted"/> is a lighter mix of it and the text).</summary>
    Muted = 20,
    /// <summary>v90, the instrument look (worked out from the theme's eight colours): a key's face, a step up from the background.</summary>
    KeyFace = 21,
    /// <summary>v90: hairlines and key borders.</summary>
    Hairline = 22,
    /// <summary>v90: inset wells (an activity line, bar tracks), darker than the background.</summary>
    Inset = 23,
    /// <summary>v90: faint text (units, secondary numbers).</summary>
    Faint = 24,
}

/// <summary>A font the engine bakes (RynthCoreHost.GetUiFont): sizes in pixels at 100% text size.</summary>
public enum RynthUiFont
{
    Ui9 = 0, Ui10 = 1,
    /// <summary>The default for plugin windows.</summary>
    Ui11 = 2,
    Ui14 = 3, Ui18 = 4, UiBold11 = 5, UiBold14 = 6,
    Mono9 = 7, Mono10 = 8, Mono12 = 9, Chat10 = 10, Chat12 = 11, Chat14 = 12,
    /// <summary>v90: Cascadia Mono (Consolas without it): numbers that line up.</summary>
    Dash9 = 13, Dash10 = 14, Dash11 = 15, Dash14 = 16,
    /// <summary>v90: Barlow Semi Condensed SemiBold, narrow labels.</summary>
    Narrow9 = 17, Narrow10 = 18, Narrow11 = 19,
    /// <summary>v90: Barlow Semi Condensed Bold.</summary>
    NarrowBold11 = 20, NarrowBold13 = 21,
}

/// <summary>What RynthCoreHost.SetPanelOpen does.</summary>
public enum RynthPanelOpen
{
    Ask = -1, Close = 0, Open = 1, Toggle = 2,
}

/// <summary>What RynthCoreHost.SetPanelPopOut does (v90).</summary>
public enum RynthPanelPopOut
{
    Ask = -1, Dock = 0, PopOut = 1, Toggle = 2,
}

/// <summary>What RynthCoreHost.ShortcutAction does to an AC shortcut slot (v90).</summary>
public enum RynthShortcutOp
{
    Use = 0, Set = 1, Clear = 2,
}

/// <summary>What RynthCoreHost.GetIconTexture draws (v90).</summary>
public enum RynthIconKind
{
    /// <summary>An icon (0x06 texture) id.</summary>
    Icon = 0,
    /// <summary>An object id: the item's icon with its underlay and overlay.</summary>
    Object = 1,
    /// <summary>A spell id: the spell's icon.</summary>
    Spell = 2,
    /// <summary>
    /// A retail UI picture (any 0x06 texture up to 512 x 512: window backgrounds, banners, frame
    /// pieces), at its own size. Engines from 2026-11-07; older ones answer IntPtr.Zero for it, so
    /// draw a stand-in while it stays zero.
    /// </summary>
    UiSprite = 3,
    /// <summary>
    /// A retail font's glyph sheet (the Font file's foreground or background surface, an A8 0x06
    /// texture up to 2048 wide) as white with the sheet's level as alpha: draw glyphs from it with a
    /// tint, from the Font file's glyph table. Engines from 2026-11-07; older ones answer IntPtr.Zero.
    /// </summary>
    FontSheet = 4,
}

/// <summary>Where RynthCoreHost.PollKeyCapture is (v90).</summary>
public enum RynthKeyCapture
{
    /// <summary>Still waiting for a key (the text, if any, says why the last one couldn't be taken).</summary>
    Waiting = 0,
    /// <summary>A key was pressed: its name ("Ctrl+F5") is the text.</summary>
    Pressed = 1,
    /// <summary>Esc, the time ran out, or it was stopped.</summary>
    Cancelled = 2,
}

/// <summary>
/// AC's own panels for RynthCoreHost.AcPanel (v90): the ids AC's panel notice uses
/// (CM_UI::SendNotice_SetPanelVisibility), read from the client 2026-10-22.
/// </summary>
public enum RynthAcPanel
{
    /// <summary>Book (gmBookUI).</summary>
    Book = 2,
    /// <summary>Character information: titles, birth, deaths (gmCharacterInfoUI).</summary>
    CharacterInfo = 3,
    /// <summary>Beneficial spells in effect.</summary>
    HelpfulSpells = 4,
    /// <summary>Harmful spells in effect.</summary>
    HarmfulSpells = 5,
    /// <summary>The pack: paper doll and backpack (gmInventoryUI).</summary>
    Inventory = 7,
    /// <summary>Link status.</summary>
    LinkStatus = 8,
    /// <summary>Options: gameplay and configuration (gmGameplayOptionsUI, gmConfigUI).</summary>
    Options = 10,
    /// <summary>Attributes and skills (gmAttributeUI, gmSkillUI).</summary>
    Character = 11,
    /// <summary>Allegiance, fellowship, friends and squelch, one tabbed panel.</summary>
    Social = 12,
    /// <summary>The spellbook and spell components (gmSpellbookUI).</summary>
    Spellbook = 13,
    /// <summary>Urgent assistance (help).</summary>
    Help = 14,
    /// <summary>Vitae.</summary>
    Vitae = 15,
    /// <summary>The map (gmMapUI).</summary>
    Map = 16,
    /// <summary>Contracts, journal and page list (quests).</summary>
    Quests = 25,
}

/// <summary>What RynthCoreHost.AcPanel does (v90).</summary>
public enum RynthAcPanelOp
{
    Toggle = 1, Open = 2, Close = 3,
}

/// <summary>What RynthCoreHost.CheckActionKey found (v90).</summary>
public enum RynthKeyCheck
{
    /// <summary>The key can't be bound to it (the message says why).</summary>
    Invalid = -1,
    /// <summary>Nothing else uses the key.</summary>
    Free = 0,
    /// <summary>Binding it takes it from what the message names.</summary>
    Taken = 1,
}
