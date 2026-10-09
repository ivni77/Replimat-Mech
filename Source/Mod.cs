using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ReplimatMech;

[DefOf]
public static class RMDefOf
{
    public static ThingDef RM_Replicator, RM_ReplicatorWall, RM_Feeder, RM_Tank, RM_TankLarge, RM_Hopper, RM_Computer, RM_IsolinearModule;
    public static DesignationDef RM_Scan;
    public static JobDef RM_ScanItem, RM_UseComputer;
    public static SoundDef RM_Replicate, RM_Dematerialize;
    public static ResearchProjectDef RM_Replication;
    public static StatDef MechFormingSpeed;

    static RMDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RMDefOf));
}

public class Settings : ModSettings
{
    public static readonly string[] PowerDefs = { "RM_Replicator", "RM_ReplicatorWall", "RM_Feeder", "RM_Tank", "RM_TankLarge", "RM_Computer", "RM_Hopper" };
    /// Колонки мощности в окне настроек: подпись и сколько подряд идущих PowerDefs в колонке.
    public static readonly (string key, int count)[] PowerGroups = { ("RM_PowerPrint", 3), ("RM_PowerStore", 2), ("RM_PowerRecycle", 2) };
    public static readonly string[] Groups = { "RM_GroupChips", "RM_GroupArcho", "RM_GroupGenes" };
    /// Поменялись умолчания — сохранённые настройки сбрасываются на новые.
    const int Version = 6;
    public static readonly Color DefaultAccent = new(0.20f, 0.45f, 1.00f);

    /// Свечение и свет — цвет реплимата, чуть разбавленный белым, чтобы горело ярко.
    public static Color GlowColor => Color.Lerp(ReplimatMechMod.S.accent, Color.white, 0.25f);

    int version = Version;
    public int massReturn = 100, valueReturn = 50, tankCapacity = 1000;
    public Color accent = DefaultAccent;
    public QualityCategory qualityCap = QualityCategory.Excellent;
    public bool needMechanitor = true, moveTanks = true;
    public List<bool> groupAllowed = new() { true, true, true };
    public List<int> groupMarkup = new() { 2, 2, 2 };
    public List<int> power = new() { 200, 200, 200, 50, 200, 300, 200 };

    public override void ExposeData()
    {
        var d = new Settings();
        Scribe_Values.Look(ref version, "version");
        Scribe_Values.Look(ref massReturn, "massReturn", d.massReturn);
        Scribe_Values.Look(ref valueReturn, "valueReturn", d.valueReturn);
        Scribe_Values.Look(ref accent, "accent", d.accent);
        Scribe_Values.Look(ref tankCapacity, "tankCapacity", d.tankCapacity);
        Scribe_Values.Look(ref qualityCap, "qualityCap", d.qualityCap);
        Scribe_Values.Look(ref needMechanitor, "needMechanitor", d.needMechanitor);
        Scribe_Values.Look(ref moveTanks, "moveTanks", d.moveTanks);
        Scribe_Collections.Look(ref groupAllowed, "groupAllowed", LookMode.Value);
        Scribe_Collections.Look(ref groupMarkup, "groupMarkup", LookMode.Value);
        Scribe_Collections.Look(ref power, "power", LookMode.Value);
        if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
        if (version != Version || groupAllowed?.Count != d.groupAllowed.Count || groupMarkup?.Count != d.groupMarkup.Count || power?.Count != d.power.Count) Reset();
        version = Version;
    }

    public void Reset()
    {
        var d = new Settings();
        foreach (FieldInfo f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            f.SetValue(this, f.GetValue(d));
    }

    /// Мощность зданий и переносимость баков — в def, уже стоящим зданиям — пересчёт; иконки меню — в цвет реплимата.
    public void Apply()
    {
        AccentIcons.Paint();
        FieldInfo baseField = AccessTools.Field(typeof(CompProperties_Power), "basePowerConsumption");
        for (int i = 0; i < PowerDefs.Length; i++)
            if (DefDatabase<ThingDef>.GetNamedSilentFail(PowerDefs[i])?.GetCompProperties<CompProperties_Power>() is { } props)
                baseField.SetValue(props, (float)power[i]);
        RMDefOf.RM_Tank.minifiedDef = RMDefOf.RM_TankLarge.minifiedDef = moveTanks ? ThingDefOf.MinifiedThing : null;
        if (Current.Game == null) return;
        foreach (Map map in Find.Maps)
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
                if (PowerDefs.Contains(b.def.defName)) b.GetComp<CompPowerTrader>()?.SetUpPowerVars();
    }
}

/// Настройки: ползунки с шагом, числа — полями ввода. Возврат стоимости до 50% + ползунки заказа ±40% (константа) —
/// вместе меньше 100%, «напечатал со скидкой — разобрал» в плюс не уходит. Скорости печати нет: время — валюта ползунков.
public class ReplimatMechMod : Mod
{
    public static Settings S;
    Vector2 scroll;
    float height = 700f;
    string[] buf = new string[Settings.Groups.Length + Settings.PowerDefs.Length + 1];

    public static ModContentPack Pack;

    public ReplimatMechMod(ModContentPack content) : base(content)
    {
        Pack = content;
        S = GetSettings<Settings>();
        new Harmony("ivni77.ReplimatMech").PatchAll();
    }

    public override string SettingsCategory() => "Replimat Mech";

    public override void WriteSettings()
    {
        base.WriteSettings();
        S.Apply();
    }

    static int Slider(Listing_Standard l, string key, int val, int min, int max, int step) =>
        Mathf.Clamp(Mathf.RoundToInt(l.SliderLabeled(key.Translate(val + "%"), val, min, max, 0.6f) / step) * step, min, max);

    static string Q(QualityCategory q) => q == QualityCategory.Legendary ? "RM_None".Translate().ToString() : q.GetLabel();

    /// Справка внизу раздела — приглушённым текстом после отступа, всплывающих подсказок нет.
    static void Help(Listing_Standard l, string text)
    {
        l.Gap(RMUI.HelpGap);
        GUI.color = RMUI.Muted;
        l.Label(text);
        GUI.color = Color.white;
    }

    /// Поле числа справа от подписи.
    void Number(Rect r, string label, ref int val, int b, int min, int max)
    {
        RMUI.Label(r.LeftPartPixels(r.width - 90f), label);
        Widgets.TextFieldNumeric(new Rect(r.xMax - 84f, r.y, 80f, 26f), ref val, ref buf[b], min, max);
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var view = new Rect(0f, 0f, inRect.width - 20f, height);
        Widgets.BeginScrollView(inRect, ref scroll, view);
        var l = new Listing_Standard();
        l.Begin(view);
        S.massReturn = Slider(l, "RM_SetMassReturn", S.massReturn, 0, 100, 5);
        S.valueReturn = Slider(l, "RM_SetValueReturn", S.valueReturn, 0, 50, 5);
        Rect color = l.GetRect(30f);
        RMUI.Label(color.LeftPart(0.6f), "RM_Color".Translate());
        // Квадрат цвета — сам кнопка; палитра та же, что у мехов (все ColorDef + цвета фракций, если игра идёт).
        Rect swatch = new(color.x + color.width * 0.6f, color.y, 60f, 30f);
        Widgets.DrawBoxSolidWithOutline(swatch, S.accent, Color.white);
        Widgets.DrawHighlightIfMouseover(swatch);
        if (Widgets.ButtonInvisible(swatch))
        {
            IEnumerable<Color> all = DefDatabase<ColorDef>.AllDefs.Select(c => c.color).Append(Settings.DefaultAccent);
            if (Current.Game != null) all = all.Concat(Find.FactionManager.AllFactionsVisible.Select(f => f.Color));
            List<Color> colors = all.Distinct().ToList();
            colors.SortByColor(c => c);
            Find.WindowStack.Add(new Dialog_ChooseColor("RM_Color".Translate(), S.accent, colors, c => S.accent = c));
        }
        l.Gap(6f);
        if (l.ButtonTextLabeledPct("RM_SetQualityCap".Translate(), Q(S.qualityCap), 0.6f))
            Find.WindowStack.Add(new FloatMenu(QualityUtility.AllQualityCategories.OrderBy(q => q == QualityCategory.Legendary ? -1 : (int)q)
                .Select(q => new FloatMenuOption(Q(q), () => S.qualityCap = q)).ToList()));
        Help(l, "RM_ColorDesc".Translate());

        l.GapLine();
        Rect head = l.GetRect(22f);
        GUI.color = ColorLibrary.Grey;
        RMUI.Label(head, "RM_SetGroups".Translate());
        RMUI.Label(new Rect(head.xMax - 180f, head.y, 90f, 22f), "RM_ColPrint".Translate());
        RMUI.Label(new Rect(head.xMax - 84f, head.y, 84f, 22f), "RM_ColMarkup".Translate());
        GUI.color = Color.white;
        for (int g = 0; g < Settings.Groups.Length; g++)
        {
            Rect r = l.GetRect(28f);
            int markup = S.groupMarkup[g];
            bool allowed = S.groupAllowed[g];
            Number(r, Settings.Groups[g].Translate().CapitalizeFirst(), ref markup, g, 1, 100);
            Widgets.Checkbox(r.xMax - 150f, r.y + 1f, ref allowed);
            S.groupMarkup[g] = markup;
            S.groupAllowed[g] = allowed;
            l.Gap(2f);
        }
        Help(l, string.Join("\n\n", Settings.Groups.Select(g => (g + "Desc").Translate().ToString())));

        l.GapLine();
        l.CheckboxLabeled("RM_SetNeedMechanitor".Translate(), ref S.needMechanitor);
        l.CheckboxLabeled("RM_SetMoveTanks".Translate(), ref S.moveTanks);
        Number(l.GetRect(28f), "RM_SetTankCapacity".Translate(), ref S.tankCapacity, buf.Length - 1, 10, 1000000);

        l.GapLine();
        l.Label("RM_SetPower".Translate().ToString());
        Rect grid = l.GetRect(24f + Settings.PowerGroups.Max(g => g.count) * 30f);
        float cw = (grid.width - 40f) / Settings.PowerGroups.Length;
        for (int g = 0, i = 0; g < Settings.PowerGroups.Length; g++)
        {
            float x = grid.x + g * (cw + 20f);
            GUI.color = ColorLibrary.Grey;
            RMUI.Label(new Rect(x, grid.y, cw, 22f), Settings.PowerGroups[g].key.Translate());
            GUI.color = Color.white;
            for (int k = 0; k < Settings.PowerGroups[g].count; k++, i++)
            {
                int w = S.power[i];
                Number(new Rect(x, grid.y + 24f + k * 30f, cw, 28f), DefDatabase<ThingDef>.GetNamed(Settings.PowerDefs[i]).LabelCap, ref w, Settings.Groups.Length + i, 0, 100000);
                S.power[i] = w;
            }
        }
        l.Gap();
        Rect reset = l.GetRect(32f);
        if (Widgets.ButtonText(new Rect(reset.x, reset.y, 160f, 32f), "RM_SetReset".Translate()))
        {
            S.Reset();
            buf = new string[buf.Length];
        }
        height = l.CurHeight + 10f;
        l.End();
        Widgets.EndScrollView();
    }
}

[StaticConstructorOnStartup]
public static class Startup
{
    static Startup()
    {
        AccentIcons.Init();
        ReplimatMechMod.S.Apply();
        ThingFilter hopper = RMDefOf.RM_Hopper.building.fixedStorageSettings.filter;
        foreach (ThingDef d in DefDatabase<ThingDef>.AllDefs.Where(Rules.Living).ToList()) hopper.SetAllow(d, false);
        hopper.RecalculateDisplayRootCategory();
        (StatDefOf.Mass.parts ??= new List<StatPart>()).Add(new StatPart_TankMass { parentStat = StatDefOf.Mass });
        // в осмотре строки печати — первыми, над ванильными про мощность, как у компьютера и бака
        foreach (ThingDef d in DefDatabase<ThingDef>.AllDefs.Where(d => d.HasComp(typeof(CompPrinter))))
        {
            CompProperties p = d.comps.Find(c => c is CompProperties_Printer);
            d.comps.Remove(p);
            d.comps.Insert(0, p);
        }
    }
}

/// Упакованный бак весит вместе с содержимым (масса упаковки берётся у постройки внутри).
public class StatPart_TankMass : StatPart
{
    public override void TransformValue(StatRequest req, ref float val)
    {
        if (req.Thing is Building_RMTank t) val += t.mass;
    }

    public override string ExplanationPart(StatRequest req) =>
        req.Thing is Building_RMTank { mass: > 0f } t ? "RM_TankContents".Translate(Fmt.Mass(t.mass)) : null;
}

public static class Fmt
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// Русский — «1 000,5», остальные языки — «1,000.5».
    static bool Ru => LanguageDatabase.activeLanguage?.folderName?.StartsWith("Russian") == true;

    static string Dec(float v, string f = "0.#") => Ru ? v.ToString(f, Inv).Replace('.', ',') : v.ToString(f, Inv);

    public static string Num(float v)
    {
        string s = Mathf.RoundToInt(v).ToString("#,0", Inv);
        return Ru ? s.Replace(',', ' ') : s;
    }

    public static string Time(float ticks)
    {
        float h = ticks / 2500f;
        // меньше минуты — секундами: двигаешь ползунок — число меняется
        if (h * 60f < 1f) return "RM_Seconds".Translate(Dec(h * 3600f));
        if (h < 1f) return "RM_Minutes".Translate(Dec(h * 60f));
        if (h < 48f) return "RM_Hours".Translate(Dec(h));
        return "RM_Days".Translate(Dec(h / 24f));
    }

    public static string MassNum(float kg) => kg < 1f ? Dec(kg, "0.##") : kg < 100f ? Dec(kg) : Num(kg);

    public static string Mass(float kg) => "RM_Kg".Translate(MassNum(kg));

    public static string SilverNum(float v) => v < 10f ? Dec(v) : Num(v);
    public static string Silver(float v) => "RM_SilverN".Translate(SilverNum(v));
}
