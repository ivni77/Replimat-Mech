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
    public static ThingDef RM_Replicator, RM_ReplicatorWall, RM_Feeder, RM_Tank, RM_TankLarge, RM_Hopper, RM_Computer, RM_Biolink, RM_Bioscanner, RM_Incubator;
    public static HediffDef RM_ReplinkImplant, RM_BiolinkImplant, RM_Replicant;
    public static RecipeDef RM_Split;
    public static DesignationDef RM_Scan;
    public static JobDef RM_Carry, RM_UseComputer, RM_RopeToScanner;
    public static SoundDef RM_Replicate, RM_Dematerialize;
    public static ResearchProjectDef RM_Replication;
    public static StatDef MechFormingSpeed;
    public static BackstoryDef RM_Bioreplicated;

    static RMDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RMDefOf));
}

public class Settings : ModSettings
{
    public static readonly string[] PowerDefs = { "RM_Replicator", "RM_ReplicatorWall", "RM_Feeder", "RM_Tank", "RM_TankLarge", "RM_Computer", "RM_Hopper", "RM_Bioscanner", "RM_Incubator" };
    /// Группы мощности в окне настроек: подпись и сколько подряд идущих PowerDefs в группе.
    public static readonly (string key, int count)[] PowerGroups = { ("RM_PowerPrint", 3), ("RM_PowerStore", 2), ("RM_PowerRecycle", 2), ("RM_PowerBio", 2) };
    public static readonly string[] Groups = { "RM_GroupChips", "RM_GroupBio", "RM_GroupUnique" };
    /// Поменялись умолчания — сохранённые настройки сбрасываются на новые.
    const int Version = 6;
    public static readonly Color DefaultAccent = new(0.20f, 0.45f, 1.00f);
    /// Сбор стоимости из расщепителя: от 5% (при 0 стоимость в баки не идёт — печатать нечем) до 100 − Cap: печать со скидкой стоит не меньше
    /// 100 − Cap процентов цены, расщепление собирает свой процент от меньшего из материалов и цены — круг «напечатал — расщепил» в плюс не уходит.
    public const int MinValueReturn = 5, MaxValueReturn = 100 - Order.Cap;

    /// Свечение и свет — цвет реплимата, чуть разбавленный белым, чтобы горело ярко.
    public static Color GlowColor => Color.Lerp(ReplimatMechMod.S.accent, Color.white, 0.25f);

    int version = Version;
    public int massReturn = 100, valueReturn = 50, tankCapacity = 1000;
    public Color accent = DefaultAccent;
    public QualityCategory qualityCap = QualityCategory.Excellent;
    public bool moveTanks = true;
    public List<bool> groupAllowed = new() { true, true, true };
    public List<int> groupMarkup = new() { 2, 2, 2 };
    public List<int> power = new() { 200, 200, 200, 50, 200, 300, 200, 200, 200 };

    public override void ExposeData()
    {
        var d = new Settings();
        Scribe_Values.Look(ref version, "version");
        Scribe_Values.Look(ref massReturn, "massReturn", d.massReturn);
        Scribe_Values.Look(ref valueReturn, "valueReturn", d.valueReturn);
        Scribe_Values.Look(ref accent, "accent", d.accent);
        Scribe_Values.Look(ref tankCapacity, "tankCapacity", d.tankCapacity);
        Scribe_Values.Look(ref qualityCap, "qualityCap", d.qualityCap);
        Scribe_Values.Look(ref moveTanks, "moveTanks", d.moveTanks);
        Scribe_Collections.Look(ref groupAllowed, "groupAllowed", LookMode.Value);
        Scribe_Collections.Look(ref groupMarkup, "groupMarkup", LookMode.Value);
        Scribe_Collections.Look(ref power, "power", LookMode.Value);
        if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
        valueReturn = Mathf.Clamp(valueReturn, MinValueReturn, MaxValueReturn);
        // новые здания — в конце списка мощности: сохранённые значения остаются, новым — по умолчанию
        while (power != null && power.Count < d.power.Count) power.Add(d.power[power.Count]);
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

/// Настройки карточками, как «Главная» компьютера: ползунки с шагом, числа — полями ввода (предел сбора стоимости — Settings.MaxValueReturn). Скорости печати нет:
/// время — валюта ползунков.
public class ReplimatMechMod : Mod
{
    public static Settings S;
    Vector2 scroll;
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

    /// FieldW — столбец полей справа, у всех карточек; галки — по его центру. LeftW — левый столбец карточек (расщепление, мощность).
    /// RowGap — отступ между строками и группами строк карточки.
    const float FieldW = 64f, LeftW = 300f, RowGap = 10f;

    static Rect FieldCol(Rect r) => new(r.xMax - FieldW, r.y, FieldW, r.height);
    static Rect CheckCol(Rect r) => new(r.x, r.y, r.width - (FieldW - 24f) / 2f, r.height);

    static void Label(Rect r, string s, TextAnchor a = TextAnchor.MiddleLeft)
    {
        Text.Anchor = a;
        RMUI.Label(r, s);
        Text.Anchor = TextAnchor.UpperLeft;
    }

    static string Q(QualityCategory q) => q == QualityCategory.Legendary ? "RM_None".Translate().ToString() : q.GetLabel();

    /// Поле числа справа от подписи.
    void Number(Rect r, string label, ref int val, int b, int min, int max)
    {
        Label(r.LeftPartPixels(r.width - FieldW - 8f), label);
        Widgets.TextFieldNumeric(FieldCol(r), ref val, ref buf[b], min, max);
    }

    /// Ползунок с шагом 5% — от конца подписи lw до правого края.
    static int Slider(Rect r, float lw, string key, int val, int min, int max)
    {
        Label(r, key.Translate(val + "%"));
        float v = Widgets.HorizontalSlider(new Rect(r.x + lw, r.y, r.width - lw, r.height), val, min, max, true);
        return Mathf.Clamp(Mathf.RoundToInt(v / 5f) * 5, min, max);
    }

    /// Сверху «Расщепление» и «Здания», ниже «Мощность» и «Печать»: левый столбец узкий, карточки одной строки — одной высоты.
    /// Высоты — сначала измерить (draw: false), потом рисовать; не влезло — прокрутка. «Сбросить всё» — в нижнем ряду окна, на уровне ванильной «Закрыть».
    public override void DoSettingsWindowContents(Rect inRect)
    {
        var cards = new (string title, List<Line> lines)[]
        {
            ("RM_SetSecSplit".Translate(), SplitLines()), ("RM_SetSecBuildings".Translate(), BuildingLines()),
            ("RM_SetPower".Translate(), PowerLines()), ("RM_SetSecPrint".Translate(), PrintLines()),
        };
        float Card(int i, Rect at, bool draw = true) => RMUI.Card(at, cards[i].title, cards[i].lines, draw);
        float full = inRect.width, x2 = LeftW + RMUI.Gap, top = 0f, bottom = 0f;
        void Measure()
        {
            top = Mathf.Max(Card(0, new Rect(0f, 0f, LeftW, 0f), false), Card(1, new Rect(0f, 0f, full - x2, 0f), false));
            bottom = Mathf.Max(Card(2, new Rect(0f, 0f, LeftW, 0f), false), Card(3, new Rect(0f, 0f, full - x2, 0f), false));
        }
        Measure();
        if (top + RMUI.Gap + bottom > inRect.height)
        {
            full -= 16f;
            Measure();
        }
        Widgets.BeginScrollView(inRect, ref scroll, new Rect(0f, 0f, full, top + RMUI.Gap + bottom));
        Card(0, new Rect(0f, 0f, LeftW, top));
        Card(1, new Rect(x2, 0f, full - x2, top));
        Card(2, new Rect(0f, top + RMUI.Gap, LeftW, bottom));
        Card(3, new Rect(x2, top + RMUI.Gap, full - x2, bottom));
        Widgets.EndScrollView();
        // вровень с ванильной «Закрыть» и той же высоты: она стоит на FooterRowHeight выше края окна
        float closeH = Window.CloseButSize.y;
        if (RMUI.Button(new Rect(inRect.xMax - 160f, inRect.yMax + closeH + Window.StandardMargin - Window.FooterRowHeight, 160f, closeH), "RM_SetReset".Translate()))
        {
            S.Reset();
            buf = new string[buf.Length];
        }
    }

    /// Предел качества и таблица особых предметов: печатать ли, наценка; под каждой группой — её справка.
    List<Line> PrintLines()
    {
        float PrintX(Rect r) => r.xMax - FieldW - 60f;
        var lines = new List<Line>
        {
            new()
            {
                check = r =>
                {
                    Label(r, "RM_SetQualityCap".Translate());
                    if (Widgets.ButtonText(new Rect(r.xMax - 140f, r.y, 140f, r.height), Q(S.qualityCap)))
                        Find.WindowStack.Add(new FloatMenu(QualityUtility.AllQualityCategories.OrderBy(q => q == QualityCategory.Legendary ? -1 : (int)q)
                            .Select(q => new FloatMenuOption(Q(q), () => S.qualityCap = q)).ToList()));
                },
            },
            new() { sep = true },
            new()
            {
                color = RMUI.Muted,
                check = r =>
                {
                    Label(r, "RM_SetGroups".Translate());
                    Label(new Rect(PrintX(r) - 50f, r.y, 100f, r.height), "RM_ColPrint".Translate(), TextAnchor.MiddleCenter);
                    Label(r, "RM_ColMarkup".Translate(), TextAnchor.MiddleRight);
                },
            },
        };
        // на экране биорепликация — последней
        foreach (int i in new[] { 0, Rules.Unique, Rules.Bio })
        {
            if (i > 0) lines.Add(new Line { h = RowGap });
            lines.Add(new Line
            {
                check = r =>
                {
                    int markup = S.groupMarkup[i];
                    bool allowed = S.groupAllowed[i];
                    Number(r, Settings.Groups[i].Translate().CapitalizeFirst(), ref markup, i, 1, 100);
                    Widgets.Checkbox(PrintX(r) - 12f, r.y, ref allowed);
                    S.groupMarkup[i] = markup;
                    S.groupAllowed[i] = allowed;
                },
            });
            foreach (string help in (Settings.Groups[i] + "Desc").Translate().ToString().Split('\n'))
                lines.Add(new Line { wide = true, color = RMUI.Muted, left = help });
        }
        return lines;
    }

    /// Два ползунка, подписи одной ширины — по самой длинной.
    static List<Line> SplitLines()
    {
        float lw = new[] { "RM_SetMassReturn".Translate("100%"), "RM_SetValueReturn".Translate(Settings.MaxValueReturn + "%") }.Max(t => Text.CalcSize(t).x) + 12f;
        return new List<Line>
        {
            new() { check = r => S.massReturn = Slider(r, lw, "RM_SetMassReturn", S.massReturn, 0, 100) },
            new() { h = RowGap },
            new() { check = r => S.valueReturn = Slider(r, lw, "RM_SetValueReturn", S.valueReturn, Settings.MinValueReturn, Settings.MaxValueReturn) },
        };
    }

    /// Цвет реплимата, переносимость и объём баков.
    List<Line> BuildingLines() => new()
    {
        new()
        {
            check = r =>
            {
                Label(r, "RM_Color".Translate());
                // квадрат цвета — сам кнопка; палитра та же, что у мехов (все ColorDef + цвета фракций, если игра идёт)
                Rect swatch = FieldCol(r);
                Widgets.DrawBoxSolidWithOutline(swatch, S.accent, Color.white);
                Widgets.DrawHighlightIfMouseover(swatch);
                if (!Widgets.ButtonInvisible(swatch)) return;
                IEnumerable<Color> all = DefDatabase<ColorDef>.AllDefs.Select(c => c.color).Append(Settings.DefaultAccent);
                if (Current.Game != null) all = all.Concat(Find.FactionManager.AllFactionsVisible.Select(f => f.Color));
                List<Color> colors = all.Distinct().ToList();
                colors.SortByColor(c => c);
                Find.WindowStack.Add(new Dialog_ChooseColor("RM_Color".Translate(), S.accent, colors, c => S.accent = c));
            },
        },
        new() { h = RowGap },
        new() { check = r => Widgets.CheckboxLabeled(CheckCol(r), "RM_SetMoveTanks".Translate(), ref S.moveTanks) },
        new() { h = RowGap },
        new() { check = r => Number(r, "RM_SetTankCapacity".Translate(), ref S.tankCapacity, buf.Length - 1, 10, 1000000) },
    };

    /// Столбцом: подпись группы зданий (печать, баки, компьютер и расщепитель), под ней поля её зданий; между группами отступ.
    List<Line> PowerLines()
    {
        var lines = new List<Line>();
        int i = 0;
        foreach ((string key, int count) in Settings.PowerGroups)
        {
            if (i > 0) lines.Add(new Line { h = RowGap });
            lines.Add(new Line { left = key.Translate(), color = RMUI.Muted });
            for (int n = 0; n < count; n++, i++)
            {
                int k = i;
                lines.Add(new Line
                {
                    check = r =>
                    {
                        int w = S.power[k];
                        Number(r, DefDatabase<ThingDef>.GetNamed(Settings.PowerDefs[k]).LabelCap, ref w, Settings.Groups.Length + k, 0, 100000);
                        S.power[k] = w;
                    },
                });
            }
        }
        return lines;
    }
}

[StaticConstructorOnStartup]
public static class Startup
{
    static Startup()
    {
        AccentIcons.Init();
        ReplimatMechMod.S.Apply();
        // расщепитель берёт всё, что можно принести: что именно — решает игрок заданиями; животных и растений в фильтре нет — их не носят
        ThingFilter split = RMDefOf.RM_Split.fixedIngredientFilter;
        foreach (ThingDef d in DefDatabase<ThingDef>.AllDefs.Where(d => d.category is ThingCategory.Pawn or ThingCategory.Plant).ToList()) split.SetAllow(d, false);
        split.RecalculateDisplayRootCategory();
        (StatDefOf.Mass.parts ??= new List<StatPart>()).Add(new StatPart_TankMass { parentStat = StatDefOf.Mass });
        // «Открывает» у исследования — в порядке меню построек: ваниль сортирует по названию, а список кэширует один раз
        RMDefOf.RM_Replication.UnlockedDefs.SortBy(d => (d as BuildableDef)?.uiOrder ?? 0f);
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

    /// Время — как в ванили (гестатор, верстаки): секунды, до суток — часы, дальше дни.
    public static string Time(float ticks) => Mathf.RoundToInt(ticks).ToStringTicksToPeriod();

    public static string MassNum(float kg) => kg < 1f ? Dec(kg, "0.##") : kg < 100f ? Dec(kg) : Num(kg);

    public static string Mass(float kg) => "RM_Kg".Translate(MassNum(kg));

    public static string SilverNum(float v) => v < 10f ? Dec(v) : Num(v);
    public static string Silver(float v) => "RM_SilverN".Translate(SilverNum(v));
}
