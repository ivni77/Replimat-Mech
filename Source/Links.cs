using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ReplimatMech;

/// Какой линк оператора нужен шаблону: риплинк — предметы, мехлинк ванили — механоиды, чипы и субъядра, биолинк — биорепликация.
public enum Link { Rep, Mech, Bio }

/// Риплинк и биолинк. Опыт — стоимость напечатанного по компьютеру оператора; каждый уровень втрое дороже прошлого, скорость +15% за уровень.
public class Hediff_RMLink : Hediff_Level
{
    /// Опыт до уровней 2–6 в долях Base: у риплинка 2000 / 8000 / 26 000 / 80 000 / 242 000, у биолинка вдвое меньше.
    static readonly int[] Steps = { 0, 2, 8, 26, 80, 242 };
    public float xp;

    float Base => def == RMDefOf.RM_BiolinkImplant ? 500f : 1000f;
    public static float SpeedAt(int level) => 1f + 0.15f * (level - 1);
    public float Speed => SpeedAt(level);
    public static int Levels => Steps.Length;
    public bool Max => level >= Levels;
    /// «риплинк (ур. 1 — 100%)»: вместо «(уровень N)» ванили — уровень и скорость печати.
    public override string Label => def.label + " (" + RMUI.Tr("RM_LinkBracket", level, Links.Pct(Speed)) + ")";
    /// Опыт, с которого начинается следующий уровень.
    public float Next => Base * Steps[Mathf.Min(level, Steps.Length - 1)];
    /// Доля пути от текущего уровня до следующего.
    public float LevelPart => Max ? 0f : Mathf.Clamp01((xp - Base * Steps[level - 1]) / (Next - Base * Steps[level - 1]));

    public void Gain(float value)
    {
        xp += value;
        int was = level;
        while (!Max && xp >= Next) SetLevelTo(level + 1);
        if (level > was)
            Messages.Message(RMUI.Tr("RM_MsgLinkLevel", pawn.LabelShort, def.label, level, Links.Pct(Speed)), pawn, MessageTypeDefOf.PositiveEvent);
    }

    /// В здоровье под описанием, пунктами, как у мехлинка ванили: уровень, скорость, опыт до следующего уровня.
    public override string TipStringExtra => Links.Tip(base.TipStringExtra, RMUI.Tr("RM_LinkLevelTip", level), Links.SpeedTip(Speed),
        RMUI.Tr("RM_LinkXpTip", Max ? Fmt.Num(xp) : Fmt.Num(xp) + " / " + Fmt.Num(Next)));

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref xp, "xp");
    }
}

public static class Links
{
    public static readonly Link[] All = { Link.Rep, Link.Mech, Link.Bio };

    public static Link Of(Pattern p) => p.IsBio ? Link.Bio : p.IsMech ? Link.Mech : Rules.Group(p.def) switch { 0 => Link.Mech, Rules.Bio => Link.Bio, _ => Link.Rep };

    public static HediffDef Def(Link l) => l switch { Link.Mech => HediffDefOf.MechlinkImplant, Link.Bio => RMDefOf.RM_BiolinkImplant, _ => RMDefOf.RM_ReplinkImplant };

    public static Hediff_RMLink Own(Pawn p, Link l) => p?.health.hediffSet.GetFirstHediffOfDef(Def(l)) as Hediff_RMLink;

    public static bool Has(Pawn p, Link l) => l == Link.Mech ? p != null && MechanitorUtility.IsMechanitor(p) : Own(p, l) != null;

    public static bool Any(Pawn p) => All.Any(l => Has(p, l));

    /// Скорость печати: риплинк и биолинк — от уровня, мехлинк — скорость гестации оператора (гестационные процессоры).
    public static float Speed(Pawn p, Link l) => !Has(p, l) ? 1f : l == Link.Mech ? p.GetStatValue(RMDefOf.MechFormingSpeed) : Own(p, l).Speed;

    static HediffDef formfeeder;
    /// Гестационный процессор ванили: имплант с верстака, ставится до 6 раз — это уровни мехлинка (без процессора — 0).
    static HediffDef Formfeeder => formfeeder ??= DefDatabase<HediffDef>.GetNamedSilentFail("MechFormfeederImplant");
    public static int MechMax => Mathf.RoundToInt(Formfeeder?.maxSeverity ?? 0f);

    public static int Level(Pawn p, Link l) => l == Link.Mech
        ? p?.health.hediffSet.GetFirstHediffOfDef(Formfeeder) is Hediff_Level h ? h.level : 0
        : Own(p, l)?.level ?? 0;

    /// Уровни линка: риплинк и биолинк — с 1, мехлинк — с 0 (без процессора); наибольший — 6.
    public static int MinLevel(Link l) => l == Link.Mech ? 0 : 1;
    public static int Top(Link l) => l == Link.Mech ? MechMax : Hediff_RMLink.Levels;

    /// Прокачка от наименьшего уровня до наибольшего — шкала в таблице оператора, у всех линков одна; у риплинка и биолинка — с долей
    /// опыта до следующего уровня.
    public static float Progress(Pawn p, Link l)
    {
        int lo = MinLevel(l), hi = Top(l);
        return hi <= lo ? 0f : Mathf.Clamp01((Level(p, l) - lo + (Own(p, l)?.LevelPart ?? 0f)) / (hi - lo));
    }

    public static bool MaxLevel(Pawn p, Link l) => l == Link.Mech ? Level(p, l) >= MechMax : Own(p, l)?.Max != false;

    /// Скорость на следующем уровне: риплинк и биолинк +15%, мехлинк — прибавка следующего процессора из его стадий.
    public static float NextSpeed(Pawn p, Link l)
    {
        int lv = Level(p, l);
        if (l != Link.Mech) return Hediff_RMLink.SpeedAt(lv + 1);
        float Off(int n) => n <= 0 ? 0f : Formfeeder.stages.LastOrDefault(s => s.minSeverity <= n)?.statOffsets?.GetStatOffsetFromList(RMDefOf.MechFormingSpeed) ?? 0f;
        return Speed(p, l) + Off(lv + 1) - Off(lv);
    }

    /// «ур. 1 — 100%» — в скобках у линка в здоровье.
    public static string Bracket(Pawn p, Link l) => RMUI.Tr("RM_LinkBracket", Level(p, l), Pct(Speed(p, l)));

    public static int Pct(float speed) => Mathf.RoundToInt(speed * 100f);

    /// Напечатано по компьютеру оператора — стоимость в опыт его линка (у мехлинка опыта нет).
    public static void Gain(Pawn p, Link l, float value) => Own(p, l)?.Gain(value);

    public static void Give(Pawn p, Link l)
    {
        if (!Has(p, l) && p.health.hediffSet.GetBrain() is { } brain) p.health.AddHediff(Def(l), brain);
    }

    public static string Name(Link l) => Def(l).LabelCap;

    public static string SpeedTip(float speed) => RMUI.Tr("RM_PrintSpeedTip", Pct(speed));

    /// Пункты подсказки «  - …» после пунктов ванили.
    public static string Tip(string vanilla, params string[] lines)
    {
        string v = (vanilla ?? "").TrimEnd();
        return (v.Length > 0 ? v + "\n" : "") + string.Join("\n", lines.Select(l => "  - " + l));
    }
}
