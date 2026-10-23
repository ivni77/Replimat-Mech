using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ReplimatMech;

/// Шаблон человека — собран из блоков в окне «Добавить задание» биорепликатора, хранится в колонии. Печать — строго по нему, без случайности:
/// внешность, навыки, интересы, черты и гены (черты и гены — из базы колонии: их собирают биосканер и расщепитель). Напечатанный — поселенец
/// 18 лет (начало взрослой стадии; взрослой предыстории ваниль до 20 не даёт), детство «выращен в биорепликаторе», с выбранными генами кожи
/// и волос и генами шаблона, без родни, болезней и одежды, имя — ванильное.
public class Human : IExposable, IRenameable
{
    /// Ступень черты и интерес — по 175 серебра до наценки (●● — вдвое): единственное новое число.
    public const float TraitValue = 175f, PassionValue = 175f;

    /// Черта в ступенях по 175 со знаком, как гены (2026-10-18: плохие компенсируют хорошие; нулевых нет — не вредная дороже,
    /// вредная дешевле): хорошая +1, очень хорошая +2, плохая −1, очень плохая −2. Ключ — defName:degree. Психическая чувствительность
    /// без Royalty — только вред от психических волн: восприимчивость дешевле, устойчивость дороже. Черта другого мода — по скидке ванили:
    /// есть — −1, нет — +1.
    static readonly Dictionary<string, int> TraitSteps = new()
    {
        ["Industriousness:2"] = 2, ["NaturalMood:2"] = 2, ["Nerves:2"] = 2, ["SpeedOffset:2"] = 2, ["Beauty:2"] = 2, ["PsychicSensitivity:-2"] = 2,
        ["Industriousness:1"] = 1, ["NaturalMood:1"] = 1, ["Nerves:1"] = 1, ["SpeedOffset:1"] = 1, ["Beauty:1"] = 1, ["Immunity:1"] = 1,
        ["Kind:0"] = 1, ["Tough:0"] = 1, ["Nimble:0"] = 1, ["FastLearner:0"] = 1, ["GreatMemory:0"] = 1, ["QuickSleeper:0"] = 1,
        ["PsychicSensitivity:-1"] = 1, ["DrugDesire:-1"] = 1, ["Neurotic:1"] = 1, ["Neurotic:2"] = 1, ["TooSmart:0"] = 1, ["Psychopath:0"] = 1,
        ["Cannibal:0"] = 1, ["Brawler:0"] = 1, ["Masochist:0"] = 1, ["NightOwl:0"] = 1, ["Ascetic:0"] = 1, ["Undergrounder:0"] = 1,
        ["Transhumanist:0"] = 1, ["ShootingAccuracy:1"] = 1, ["ShootingAccuracy:-1"] = 1, ["Gay:0"] = 1, ["Bisexual:0"] = 1, ["Asexual:0"] = 1,
        ["Industriousness:-1"] = -1, ["NaturalMood:-1"] = -1, ["Nerves:-1"] = -1, ["SpeedOffset:-1"] = -1, ["Beauty:-1"] = -1, ["Immunity:-1"] = -1,
        ["DrugDesire:1"] = -1, ["Abrasive:0"] = -1, ["AnnoyingVoice:0"] = -1, ["CreepyBreathing:0"] = -1, ["Pyromaniac:0"] = -1, ["Wimp:0"] = -1,
        ["SlowLearner:0"] = -1, ["Gourmand:0"] = -1, ["Greedy:0"] = -1, ["Jealous:0"] = -1, ["DislikesMen:0"] = -1, ["DislikesWomen:0"] = -1,
        ["Delicate:0"] = -1, ["PsychicSensitivity:1"] = -1, ["Recluse:0"] = -1, ["Nudist:0"] = -1, ["Bloodlust:0"] = -1, ["BodyPurist:0"] = -1,
        ["TorturedArtist:0"] = -1,
        ["Industriousness:-2"] = -2, ["NaturalMood:-2"] = -2, ["Nerves:-2"] = -2, ["Beauty:-2"] = -2, ["DrugDesire:2"] = -2, ["PsychicSensitivity:2"] = -2,
    };

    /// Ступень черты — она же её группа в окне (2026-10-21): +2, +1, −1, −2.
    public static int TraitStep((TraitDef def, int degree) t) => TraitSteps.TryGetValue(t.def.defName + ":" + t.degree, out int s) ? s
        : t.def.DataAtDegree(t.degree).marketValueFactorOffset < 0f ? -1 : 1;

    public static float TraitPrice((TraitDef def, int degree) t) => TraitValue * TraitStep(t);

    /// Ген (2026-10-18): генпак + капсулы архитов (цены ванили) + 175 за единицу метаболизма со знаком — полезные (ниже 0) дороже,
    /// вредные дешевле; генпаки и капсулы не нужны, их цена — в стоимости.
    public static float GenePrice(GeneDef g) => ThingDefOf.Genepack.BaseMarketValue + g.biostatArc * ThingDefOf.ArchiteCapsule.BaseMarketValue
        - TraitValue * g.biostatMet;
    public const int MaxTraits = 3, MaxLevel = 20;
    /// Запись навыков — предел опыта ванили в день.
    const float XpPerDay = 4000f;
    /// Пустой шаблон (навыки 0) — 25 дней при скорости 100%: с ползунком −40% — 15 (2026-10-20); остальное — в той же доле.
    const float BlankDays = 25f;

    public int id;
    public string name;
    public Gender gender = Gender.Female;
    public GeneDef body, skin, hairColor;
    public HeadTypeDef head;
    public HairDef hair;
    public BeardDef beard;
    public Dictionary<SkillDef, int> levels = new();
    public Dictionary<SkillDef, Passion> passions = new();
    public List<(TraitDef def, int degree)> traits = new();
    /// Гены шаблона — кроме внешности (кожа, волосы, тело — во «Внешности»).
    public List<GeneDef> genes = new();
    /// «Репликант» (2026-10-21): обычный шаблон, есть с начала — печатать человека без сканера; правится на месте; уходит в
    /// корзину, как всё, навсегда не удаляется.
    public bool blank;

    // образцы и цена — по данным шаблона, пересчёт только при их смене
    string built;
    Pawn probe;
    float value;
    bool floor;
    readonly Dictionary<SkillDef, float> skillValue = new();
    readonly Dictionary<int, Pawn> stages = new();

    public static float AdultAge => Pricing.AdultAge(ThingDefOf.Human) + 0.001f;

    // ---------------- варианты ----------------

    /// Тело — гены тела Biotech (их ванильные названия): стандартное первым.
    public static List<GeneDef> Bodies => DefDatabase<GeneDef>.AllDefs.Where(g => g.bodyType.HasValue && g.endogeneCategory == EndogeneCategory.BodyType)
        .OrderBy(g => g.bodyType != GeneticBodyType.Standard).ThenBy(g => g.bodyType).ToList();

    /// Цвет кожи — гены меланина ванили, от светлой к тёмной.
    public static List<GeneDef> Skins => DefDatabase<GeneDef>.AllDefs.Where(g => g.endogeneCategory == EndogeneCategory.Melanin && g.skinColorBase.HasValue)
        .OrderBy(g => g.minMelanin).ToList();

    /// Цвет волос — гены цвета волос ванили, в её порядке.
    public static List<GeneDef> HairColors => DefDatabase<GeneDef>.AllDefs.Where(g => g.endogeneCategory == EndogeneCategory.HairColor && g.hairColorOverride.HasValue).ToList();

    // ---------------- гены и черты ----------------

    /// Гены внешности — во «Внешности», не в генах шаблона и не в базе колонии.
    public static bool Look(GeneDef g) => g.endogeneCategory is EndogeneCategory.Melanin or EndogeneCategory.HairColor or EndogeneCategory.BodyType;

    /// От генов — головы, волосы и бороды, которым нужен ген, и черты, которые ген даёт сам.
    bool HasGenes(List<GeneDef> need) => need.NullOrEmpty() || need.All(genes.Contains);
    IEnumerable<(TraitDef def, int degree)> GeneTraits => genes.Where(g => g.forcedTraits != null).SelectMany(g => g.forcedTraits).Select(t => (t.def, t.degree));

    /// Головы — те, что ваниль выбирает случайно, своего пола; гены ксенотипа задают головы — только их, голова с нужным геном — если он есть.
    public List<HeadTypeDef> Heads
    {
        get
        {
            List<HeadTypeDef> forced = genes.Where(g => g.forcedHeadTypes != null).SelectMany(g => g.forcedHeadTypes).ToList();
            return (forced.Count > 0 ? forced : DefDatabase<HeadTypeDef>.AllDefs.Where(h => h.randomChosen && HasGenes(h.requiredGenes)))
                .Where(h => h.gender == Gender.None || h.gender == gender).Distinct().ToList();
        }
    }

    /// Причёски и бороды — как у ванили с генами (Pawn_GeneTracker.StyleItemAllowed): нужный ген есть, гены не запрещают.
    public List<HairDef> Hairs => DefDatabase<HairDef>.AllDefs.Where(h => h.requiredMutant == null && (h.requiredGene == null || genes.Contains(h.requiredGene))
            && genes.All(g => (g.forcedHair == null || g.forcedHair == h) && g.hairTagFilter?.Allows(h.styleTags) != false))
        .OrderBy(h => h != HairDefOf.Bald).ThenBy(h => h.label).ToList();

    public List<BeardDef> Beards => DefDatabase<BeardDef>.AllDefs.Where(b => b.requiredMutant == null && (b.requiredGene == null || genes.Contains(b.requiredGene))
            && genes.All(g => g.beardTagFilter?.Allows(b.styleTags) != false))
        .OrderBy(b => b != BeardDefOf.NoBeard).ThenBy(b => b.label).ToList();

    /// Сложность и метаболизм генов шаблона — итоги, как у сборщика генов ванили (капсулы с архитами — только в цене гена, 2026-10-21).
    public int Cpx => genes.Sum(g => g.biostatCpx);
    public int Met => genes.Sum(g => g.biostatMet);

    /// Предел метаболизма ванили — только снизу, −5 (сборщик генов не даёт ниже; 2026-10-21): сдвиг d можно, если итог не ниже −5
    /// или метаболизм не падает (шаблон ниже −5 не заперт).
    static bool MetOk(int met, int d) => met + d >= GeneTuning.BiostatRange.min || d >= 0;

    /// Ген можно добавить: не конфликтует с выбранными, его предпосылка выбрана, метаболизм не ниже −5, черты гена
    /// не конфликтуют с выбранными чертами.
    public bool CanAdd(GeneDef g) => !genes.Any(x => x.ConflictsWith(g)) && (g.prerequisite == null || genes.Contains(g.prerequisite)) && MetOk(Met, g.biostatMet)
        && g.forcedTraits?.Any(t => traits.Any(x => x.def == t.def || x.def.ConflictsWith(t.def))) != true;

    /// Ген можно убрать, если шаблон остаётся допустимым: он не нужен другому гену, метаболизм не ниже −5.
    public bool CanRemove(GeneDef g) => genes.Contains(g) && !genes.Any(x => x.prerequisite == g) && MetOk(Met, -g.biostatMet);

    /// Предел −5 не даёт добавить ген или убрать выбранный.
    public bool MetBlocked(GeneDef g) => !MetOk(Met, genes.Contains(g) ? -g.biostatMet : g.biostatMet);

    /// Гены меняют головы, причёски и бороды — Fixed подбирает их заново.
    public void Toggle(GeneDef g)
    {
        if (!genes.Remove(g)) genes.Add(g);
        Fixed();
    }

    /// Черту можно добавить: выбрано меньше трёх, нет конфликта с выбранными (и другой степени той же), с чертами генов и с выбранными
    /// интересами; черта бывает у этого пола.
    public bool CanAdd((TraitDef def, int degree) t) => traits.Count < MaxTraits && t.def.GetGenderSpecificCommonality(gender) > 0f
        && !traits.Concat(GeneTraits).Any(x => x.def == t.def || x.def.ConflictsWith(t.def))
        && !passions.Any(kv => kv.Value != Passion.None && t.def.ConflictsWithPassion(kv.Key));

    /// «Случайно» — внешность того же пола из тех же вариантов (2026-10-18: кому важны только навыки). Случайность окна, не игры:
    /// System.Random — Rand игры в Multiplayer общий у всех игроков.
    public void Randomize(System.Random r)
    {
        T Pick<T>(List<T> l) => l.Count == 0 ? default : l[r.Next(l.Count)];
        body = Pick(Bodies) ?? body;
        skin = Pick(Skins) ?? skin;
        hairColor = Pick(HairColors) ?? hairColor;
        head = Pick(Heads) ?? head;
        hair = Pick(Hairs.Where(x => x.styleGender == StyleGender.Any
            || (gender == Gender.Male ? x.styleGender is StyleGender.Male or StyleGender.MaleUsually : x.styleGender is StyleGender.Female or StyleGender.FemaleUsually)).ToList()) ?? hair;
        beard = gender == Gender.Male ? Pick(Beards) ?? beard : BeardDefOf.NoBeard;
    }

    /// Блок конструктора в строке данных — для его «Сбросить»: 0 — внешность (пол, тело, голова, кожа, волосы, цвет волос, борода),
    /// 1 — навыки и интересы, 2 — черты, 3 — гены.
    public const int LookBlock = 0, SkillsBlock = 1, TraitsBlock = 2, GenesBlock = 3;
    static readonly int[][] BlockFields = { new[] { 1, 2, 3, 4, 5, 6, 7 }, new[] { 8 }, new[] { 9 }, new[] { 10 } };

    public static string Block(string data, int block)
    {
        string[] f = data.Split('\n');
        return string.Join("\n", BlockFields[block].Select(i => i < f.Length ? f[i] : ""));
    }

    /// Содержимое шаблона — строка данных без названия (первой строки): его несут «Копировать» и «Вставить», получает новый шаблон.
    public static string Body(string d) => (d ?? "").Substring(Mathf.Max(0, (d ?? "").IndexOf('\n')));

    public static bool SameBody(string a, string b) => Body(a) == Body(b);

    /// Всё, кроме названия, — из строки данных другого (вставка, «Сбросить» задания): одним разбором, голова и волосы — под его гены.
    public void TakeAll(string data) => Decode(name + Body(data));

    /// Блок — как у другого шаблона, остальное своё.
    public void TakeBlock(Human from, int block)
    {
        string[] mine = Encode().Split('\n'), theirs = from.Encode().Split('\n');
        foreach (int i in BlockFields[block]) mine[i] = theirs[i];
        Decode(string.Join("\n", mine));
    }

    public string TraitLabel((TraitDef def, int degree) t) => t.def.DataAtDegree(t.degree).GetLabelCapFor(gender);

    /// Описание черты — текст ванили про Репликанта этого пола (2026-10-21: мы его печатаем, лор; образец так и зовут, см. Probe);
    /// имя не раскрашено — Resolve() красит его, цвет снимаем.
    public string TraitDesc((TraitDef def, int degree) t) => Portrait is { } p
        ? t.def.DataAtDegree(t.degree).description.Formatted(p.Named("PAWN")).AdjustedFor(p).Resolve().StripTags() : "";

    public int Level(SkillDef s) => levels.TryGetValue(s, out int v) ? v : 0;
    public Passion PassionOf(SkillDef s) => passions.TryGetValue(s, out Passion v) ? v : Passion.None;

    /// Интерес можно поставить, если ни одна выбранная черта с ним не конфликтует.
    public bool PassionAllowed(SkillDef s) => !traits.Any(t => t.def.ConflictsWithPassion(s));

    public void AddTrait((TraitDef def, int degree) t)
    {
        if (traits.Count >= MaxTraits) return;
        traits.Add(t);
        // интересы, которых требует черта, — хотя бы ●, как ставит ваниль
        foreach (SkillDef s in t.def.forcedPassions ?? new List<SkillDef>())
            if (PassionOf(s) == Passion.None) passions[s] = Passion.Minor;
    }

    /// Пол или гены сменили — голова, волосы и борода под них.
    public Human Fixed()
    {
        if (gender != Gender.Male) gender = Gender.Female;
        body ??= Bodies.FirstOrDefault();
        skin ??= Skins.ElementAtOrDefault(Skins.Count / 3) ?? Skins.FirstOrDefault();
        hairColor ??= HairColors.FirstOrDefault(c => c.defName == "Hair_DarkBrown") ?? HairColors.FirstOrDefault();
        List<HeadTypeDef> heads = Heads;
        if (head == null || !heads.Contains(head)) head = heads.FirstOrDefault();
        List<HairDef> hairs = Hairs;
        if (hair == null || !hairs.Contains(hair)) hair = hairs.FirstOrDefault(h => h.styleGender is StyleGender.Female or StyleGender.FemaleUsually) ?? hairs.FirstOrDefault();
        if (gender != Gender.Male || beard == null || !Beards.Contains(beard)) beard = BeardDefOf.NoBeard;
        return this;
    }

    /// Основа — «Репликант» (2026-10-21: не «Первозданный» ванили).
    public static Human Blank(int id) => new Human { id = id, blank = true, name = "RM_Replicant".Translate() }.Fixed();

    public Human Copy()
    {
        var h = new Human { id = id, blank = blank };
        h.Decode(Encode());
        return h;
    }

    // ---------------- строка данных: сохранение и Multiplayer ----------------

    public string Encode() => string.Join("\n", name, gender.ToString(), body?.defName, head?.defName, skin?.defName, hair?.defName, hairColor?.defName, beard?.defName,
        string.Join(",", DefDatabase<SkillDef>.AllDefsListForReading.Where(s => Level(s) > 0 || PassionOf(s) != Passion.None)
            .Select(s => s.defName + "=" + Level(s) + "/" + (int)PassionOf(s))),
        string.Join(",", traits.Select(t => t.def.defName + ":" + t.degree)), "g:" + string.Join(",", genes.Select(g => g.defName)));

    public void Decode(string s)
    {
        string[] f = (s ?? "").Split('\n');
        string F(int i) => i < f.Length ? f[i] : "";
        name = F(0).NullOrEmpty() ? DefaultName : F(0);
        gender = F(1) == nameof(Gender.Male) ? Gender.Male : Gender.Female;
        body = DefDatabase<GeneDef>.GetNamedSilentFail(F(2));
        head = DefDatabase<HeadTypeDef>.GetNamedSilentFail(F(3));
        skin = DefDatabase<GeneDef>.GetNamedSilentFail(F(4));
        hair = DefDatabase<HairDef>.GetNamedSilentFail(F(5));
        hairColor = DefDatabase<GeneDef>.GetNamedSilentFail(F(6));
        beard = DefDatabase<BeardDef>.GetNamedSilentFail(F(7));
        levels.Clear();
        passions.Clear();
        foreach (string e in F(8).Split(',').Where(e => e.Contains('=')))
        {
            string[] kv = e.Split('=', '/');
            if (DefDatabase<SkillDef>.GetNamedSilentFail(kv[0]) is not { } sk) continue;
            if (int.TryParse(kv[1], out int lvl)) levels[sk] = Mathf.Clamp(lvl, 0, MaxLevel);
            if (kv.Length > 2 && int.TryParse(kv[2], out int ps)) passions[sk] = (Passion)Mathf.Clamp(ps, 0, 2);
        }
        traits.Clear();
        foreach (string e in F(9).Split(',').Where(e => e.Contains(':')))
        {
            string[] kv = e.Split(':');
            if (DefDatabase<TraitDef>.GetNamedSilentFail(kv[0]) is { } td && int.TryParse(kv[1], out int deg) && td.degreeDatas.Any(d => d.degree == deg) && traits.Count < MaxTraits)
                traits.Add((td, deg));
        }
        genes.Clear();
        // до «Шаблонов v2» здесь был ксенотип: ванильный (defName), свой — имя и гены «def:ксеноген» — гены шаблона берутся из него
        IEnumerable<GeneDef> gs = F(10).StartsWith("g:") ? F(10).Substring(2).Split(',').Select(DefDatabase<GeneDef>.GetNamedSilentFail)
            : F(11).NullOrEmpty() ? DefDatabase<XenotypeDef>.GetNamedSilentFail(F(10))?.genes ?? new List<GeneDef>()
            : F(12).Split(',').Select(e => DefDatabase<GeneDef>.GetNamedSilentFail(e.Split(':')[0]));
        genes.AddRange(gs.Where(g => g != null && !Look(g)).Distinct());
        Fixed();
    }

    public void ExposeData()
    {
        Scribe_Values.Look(ref id, "id");
        Scribe_Values.Look(ref blank, "blank");
        // название по умолчанию не пишется: после загрузки оно на языке игры; своё (основы тоже, 2026-10-21) — остаётся
        string data = Scribe.mode == LoadSaveMode.Saving ? (name == DefaultName ? "" : name) + Encode().Substring(name.Length) : null;
        Scribe_Values.Look(ref data, "data");
        if (Scribe.mode != LoadSaveMode.LoadingVars) return;
        Decode(data);
        // до людей круга 18 «Репликант» звался «Первозданный» ксенотипа ванили — это не своё название
        if (blank && name == XenotypeDefOf.Baseliner.LabelCap.ToString()) name = DefaultName;
    }

    // ---------------- IRenameable (окно шаблона правит черновик) ----------------

    /// Пустое название — снова по умолчанию: у основы «Репликант», у шаблонов «Шаблон N».
    string DefaultName => blank ? "RM_Replicant".Translate() : RMUI.Tr("RM_TemplateN", id);

    public string RenamableLabel { get => name; set => name = value.NullOrEmpty() ? DefaultName : value; }
    public string BaseLabel => name;
    public string InspectLabel => name;

    // ---------------- цена и время ----------------

    /// Стоимость до наценки: цена тела с навыками по ванили + черты со знаком + интересы; не ниже тела с навыками 0.
    public float Value
    {
        get
        {
            Build();
            return value;
        }
    }

    /// Сколько стоит навык строки (с интересом): насколько дешевле был бы человек с этим навыком на 0.
    public float SkillValue(SkillDef s)
    {
        Build();
        return skillValue.TryGetValue(s, out float v) ? v : 0f;
    }

    /// Цена — на пороге: черты и навыки не подняли её выше тела с навыками 0 (2026-10-20: порог — константа, «мин. стоимость»).
    public bool AtFloor
    {
        get
        {
            Build();
            return floor;
        }
    }

    public float TraitValueAt(int i) => i < traits.Count ? TraitPrice(traits[i]) : 0f;

    /// Склонность от черт (+N к уровню, как в ванили).
    public int Aptitude(SkillDef s)
    {
        Build();
        return probe?.skills.GetSkill(s) is { } r ? r.Aptitude : 0;
    }

    public static float PassionCost(Passion p) => p == Passion.Major ? 2f * PassionValue : p == Passion.Minor ? PassionValue : 0f;

    /// Запись навыка до уровня, дней: весь опыт его уровней по пределу ванили в день.
    public static float SkillDays(int level)
    {
        float xp = 0f;
        for (int i = 0; i < level; i++) xp += SkillRecord.XpRequiredToLevelUpFrom(i);
        return xp / XpPerDay;
    }

    /// Доля от «рост тела + запись навыков»: пустой шаблон — BlankDays.
    static float Scale => BlankDays / Pricing.GrowDays(ThingDefOf.Human);

    /// Время в тиках при скорости 100%: (рост тела + запись навыков) в доле Scale.
    public float Ticks => (Pricing.GrowDays(ThingDefOf.Human) + levels.Sum(kv => SkillDays(kv.Value))) * Scale * GenDate.TicksPerDay;

    /// Время строки навыка, тиков при скорости 100%.
    public static float SkillTicks(int level) => SkillDays(level) * Scale * GenDate.TicksPerDay;

    // ---------------- образцы ----------------

    /// Взрослый образец — для цены и портрета.
    public Pawn Portrait
    {
        get
        {
            Build();
            return probe;
        }
    }

    /// За стеклом человек растёт по стадиям ванили: младенец, ребёнок... взрослый — образец стадии по доле готовности.
    public Pawn Stage(float progress)
    {
        Build();
        List<LifeStageAge> ages = ThingDefOf.Human.race.lifeStageAges;
        int i = ages.FindLastIndex(a => a.minAge <= Mathf.Clamp01(progress) * ages[ages.Count - 1].minAge);
        if (i < 0 || i >= ages.Count - 1) return probe;
        if (!stages.TryGetValue(i, out Pawn p)) stages[i] = p = Probe(ages[i].minAge + 0.5f) ?? probe;
        return p;
    }

    void Build()
    {
        string d = Encode();
        if (d == built && probe != null) return;
        built = d;
        stages.Clear();
        skillValue.Clear();
        probe = Probe(AdultAge);
        if (probe == null)
        {
            (value, floor) = (0f, false);
            return;
        }
        // цена ванили — тела и навыков: образец без генов шаблона (они — по своей цене: склонности и красота генов в неё не идут) и без черт
        // шаблона (они — по своей шкале, сняты на время; у образца, игру не трогает)
        Pawn pp = genes.Count == 0 ? probe : Probe(AdultAge, false) ?? probe;
        List<Trait> had = pp.story.traits.allTraits.Where(t => t.sourceGene == null).ToList();
        foreach (Trait t in had) pp.story.traits.RemoveTrait(t);
        float v = pp.MarketValue;
        foreach (SkillRecord r in pp.skills.skills)
        {
            int was = r.levelInt;
            r.levelInt = 0;
            float without = pp.MarketValue;
            r.levelInt = was;
            skillValue[r.def] = v - without + PassionCost(PassionOf(r.def));
        }
        int[] levelsWas = pp.skills.skills.Select(r => r.levelInt).ToArray();
        foreach (SkillRecord r in pp.skills.skills) r.levelInt = 0;
        float body = pp.MarketValue;
        for (int i = 0; i < levelsWas.Length; i++) pp.skills.skills[i].levelInt = levelsWas[i];
        foreach (Trait t in had) pp.story.traits.GainTrait(t, true);
        float sum = v + traits.Sum(TraitPrice) + passions.Values.Sum(PassionCost) + genes.Sum(GenePrice);
        floor = sum <= body;
        value = Mathf.Max(body, sum);
    }

    static readonly List<FieldInfo> IdFields = typeof(UniqueIDsManager).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Where(f => f.FieldType == typeof(int)).ToList();

    /// Свои номера образцов — далеко ниже номеров игры и Multiplayer (у того в окне они отрицательные, от −1 вниз).
    static int nextProbeId = -1_000_000_000;

    /// Имя образца — полем: сеттер Pawn.Name Multiplayer шлёт командой, а образца нет в мире («Thing … is inaccessible», рассинхронизация).
    static readonly AccessTools.FieldRef<Pawn, Name> PawnName = AccessTools.FieldRefAccess<Pawn, Name>("nameInt");

    /// Образец не меняет игру, кто бы его ни создал (окно или тик): случайность и все номера ванили — как были. Иначе в Multiplayer
    /// игроки разойдутся. Свой номер у каждого образца — разный: ваниль сравнивает пешки по номеру (кэш портретов отдал бы чужой).
    Pawn Probe(float age, bool withGenes = true)
    {
        UniqueIDsManager ids = Find.UniqueIDsManager;
        object[] saved = IdFields.Select(f => f.GetValue(ids)).ToArray();
        Rand.PushState();
        try
        {
            Pawn p = Make(age, withGenes);
            p.thingIDNumber = nextProbeId--;
            PawnName(p) = new NameSingle("RM_Replicant".Translate());
            return p;
        }
        catch (System.Exception e)
        {
            Log.ErrorOnce("Replimat Mech: template probe failed: " + e, 0x52_4d_48 + id);
            return null;
        }
        finally
        {
            Rand.PopState();
            for (int i = 0; i < IdFields.Count; i++) IdFields[i].SetValue(ids, saved[i]);
        }
    }

    /// Ксенотип ванили с ровно этими генами (без внешности); нет такого — null.
    XenotypeDef Xenotype => genes.Count == 0 ? XenotypeDefOf.Baseliner
        : DefDatabase<XenotypeDef>.AllDefs.FirstOrDefault(x => x.genes.Where(g => !Look(g)).ToHashSet().SetEquals(genes));

    /// Новый человек по шаблону; age — возраст (образцы стадий роста моложе). Гены — врождённые, поверх базового человека; ксенотип —
    /// ванильный с ровно этими генами, иначе свой с именем шаблона. withGenes false — образец цены без генов шаблона.
    public Pawn Make(float age, bool withGenes = true)
    {
        Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true,
            canGeneratePawnRelations: false, allowFood: false, allowAddictions: false, fixedBiologicalAge: age, fixedChronologicalAge: age,
            fixedGender: gender, forcedXenotype: XenotypeDefOf.Baseliner,
            developmentalStages: DevelopmentalStage.Baby | DevelopmentalStage.Child | DevelopmentalStage.Adult, allowDowned: true, forceNoGear: true));
        // здоровье — до генов: часть генов вешает своё состояние (химическая зависимость, смертный покой, психическая связь)
        p.health.RemoveAllHediffs();
        if (p.genes != null && genes.Count > 0 && withGenes)
        {
            foreach (GeneDef g in genes) p.genes.AddGene(g, false);
            if (Xenotype is { } x) p.genes.SetXenotypeDirect(x);
            else p.genes.xenotypeName = name;
        }
        Apply(p);
        return p;
    }

    void Apply(Pawn p)
    {
        bool adult = p.DevelopmentalStage.Adult();
        if (adult)
        {
            p.story.Childhood = RMDefOf.RM_Bioreplicated;
            p.story.Adulthood = null;
            if (body?.bodyType is { } bt) p.story.bodyType = bt.ToBodyType(p);
            foreach (Trait t in p.story.traits.allTraits.Where(t => t.sourceGene == null).ToList()) p.story.traits.RemoveTrait(t);
            foreach (var (def, degree) in traits) p.story.traits.GainTrait(new Trait(def, degree, true), true);
            p.style.beardDef = gender == Gender.Male && beard != null ? beard : BeardDefOf.NoBeard;
        }
        if (head != null) p.story.headType = head;
        if (hair != null) p.story.hairDef = hair;
        if (p.genes != null)
        {
            foreach (Gene g in p.genes.Endogenes.Where(g => g.def.endogeneCategory is EndogeneCategory.Melanin or EndogeneCategory.HairColor).ToList()) p.genes.RemoveGene(g);
            if (skin != null) p.genes.AddGene(skin, false);
            if (hairColor != null) p.genes.AddGene(hairColor, false);
        }
        // цвет — ровно по гену: ваниль у некоторых генов волос добавляет случайную яркость
        if (skin?.skinColorBase is { } sc) p.story.SkinColorBase = sc;
        if (hairColor?.hairColorOverride is { } hc) p.story.HairColor = hc.ClampToValueRange(GeneTuning.HairColorValueRange);
        if (p.skills != null)
            foreach (SkillRecord s in p.skills.skills)
            {
                s.levelInt = adult ? Level(s.def) : 0;
                s.passion = adult ? PassionOf(s.def) : Passion.None;
                s.xpSinceLastLevel = 0f;
                s.xpSinceMidnight = 0f;
            }
        p.Notify_DisabledWorkTypesChanged();
        p.skills?.Notify_SkillDisablesChanged();
        p.Drawer.renderer.SetAllGraphicsDirty();
        PortraitsCache.SetDirty(p);
    }
}

/// Конструктор шаблона человека — в правой половине окна «Добавить задание» биорепликатора, под заданием (2026-10-18: одно окно).
/// Три блока в рамках, как карточки «Главной» компьютера, — видно, где кончается задание (2026-10-20): шаблон (название, «Удалить»,
/// «Сохранить», вкладки «Внешность | Навыки»), «Черты характера» и «Гены» — что в шаблоне, карточками: клик убирает. Черты добавляются
/// кликом по карточкам категории «Черты характера» посередине окна. У каждого блока своя «Сбросить» (у вкладок — открытой вкладки).
/// Правится черновик h; «Применить» отдаёт его колонии (Edit.SaveHuman), «Репликанту» тоже — он меняется сам (2026-10-21).
public class TemplateEditor
{
    enum Part { Body, Head, Skin, Hair, HairColor, Beard }

    readonly CompPrinter printer;
    public readonly Human h;
    /// Что печатает задание над конструктором — черновик (в колонию он не попадает).
    public readonly Pattern Pat;
    Part part;
    Vector2 scroll;
    readonly Dictionary<SkillDef, string> bufs = new();
    /// «Копировать» у навыка: уровень и интерес — «Вставить» на другой навык (2026-10-20: так быстрее заполнять).
    (int level, Passion passion)? clip;
    static readonly System.Random Dice = new();
    public static readonly CachedTexture ResetTex = new("UI/Widgets/RotLeft");

    /// Pad и HeadH — как у карточек «Главной» (RMUI.Card); строка навыка — 24; портрет — как был во вкладке.
    const float Pad = 14f, HeadH = 34f, BtnH = 28f, PartH = 30f, PortraitH = 240f, SkillRowH = 24f, SkillHeadH = 28f;

    public TemplateEditor(CompPrinter printer, Human human)
    {
        this.printer = printer;
        h = human.Copy();
        Pat = new Pattern { def = ThingDefOf.Human, human = h };
    }

    /// Сохранённый шаблон черновика.
    public Human Saved => GameComponent_RM.Get.humans.FirstOrDefault(x => x.id == h.id);

    /// Название не в счёт: у сохранённого оно меняется сразу (с карточки).
    public bool Changed => Saved is not { } s || !Human.SameBody(s.Encode(), h.Encode());

    public void Save() => Edit.SaveHuman(h.id, h.Encode());

    /// Сохранение начнёт заново начатую печать по этому шаблону (правка не названия) — сначала спросить.
    public bool Restarts => Saved is { } s && !Human.SameBody(s.Encode(), h.Encode())
                            && CompPrinter.All.Any(c => c.orders.Any(o => o.paid && o.pattern?.human == s));

    /// К чему «Сбросить»: к сохранённому шаблону (пока колония его не получила — откатывать нечего).
    Human Was => Saved ?? h;

    /// Шаблон не как был — «Сбросить» задания активна (2026-10-21: она общая, как «Применить»).
    public bool Edited => !Human.SameBody(Was.Encode(), h.Encode());

    /// «Сбросить» задания: весь шаблон — как был, все блоки.
    public void Reset() => Paste(Was.Encode());

    /// «Вставить» на карточке (2026-10-21): скопированный человек — неприменённой правкой всех блоков, название своё.
    public void Paste(string data)
    {
        h.TakeAll(data);
        bufs.Clear();
    }

    /// Справка шаблона — блоками в кнопке «Справка» шапки «Шаблоны» (2026-10-20: там логичнее).
    public static List<(string, string)> Help => new()
    {
        ("RM_EdTemplate".Translate(), "RM_TemplateHelp".Translate()),
        ("Skills".Translate(), "RM_SkillsHelp".Translate()),
        ("Traits".Translate().CapitalizeFirst(), "RM_TraitsHelp".Translate()),
        ("Genes".Translate().CapitalizeFirst(), "RM_GenesHelp".Translate()),
    };

    /// Высота блока в рамке по высоте того, что под шапкой.
    static float BlockH(float content) => Pad + HeadH + 8f + content + Pad;

    /// Портрет, под ним пол и «Случайно».
    static float LookH => BlockH(PortraitH + 2f * (PartH + 8f));

    static float SkillsH => BlockH(SkillHeadH + DefDatabase<SkillDef>.DefCount * SkillRowH);

    /// Строка названия шаблона — над заданием (2026-10-20: всё под ней, и задание, — этой пешки).
    const float StripPad = 5f;
    public const float StripH = 2f * StripPad + HeadH;

    /// Без строки названия: блоки внешности, навыков, черт, генов.
    public float Height(float w) => LookH + RMUI.Gap + SkillsH + RMUI.Gap + Traits(w) + RMUI.Gap + Genes(w);

    /// Столбцы таблицы навыков — по самому длинному названию и значению: навык, значки, интерес, уровень, время, стоимость. Шапка интереса,
    /// времени и стоимости — значок (2026-10-21: окно почти как у репликатора), столбец — не уже его. Время — самое широкое из всех
    /// уровней при этой скорости: «0 секунд» шире «25 дней».
    static (float name, float icons, float passion, float level, float time, float value) SkillCols(float speed, string maxValue)
    {
        const float fh = SkillRowH - 4f;
        float Col(float min) => Mathf.Max(min, fh) + 12f;
        float time = Enumerable.Range(0, Human.MaxLevel + 1).Max(l => Text.CalcSize(Fmt.Time(Human.SkillTicks(l) / speed)).x);
        return (Mathf.Max(Text.CalcSize("Skill".Translate()).x, fh + 4f + DefDatabase<SkillDef>.AllDefsListForReading.Max(s => Text.CalcSize(s.skillLabel.CapitalizeFirst()).x)) + 18f,
            3f * (fh + 4f) + 8f, Col(fh), 3f * 34f + 30f + 8f, Col(time), Col(Text.CalcSize(maxValue).x));
    }

    /// Шапка-значок столбца навыков: название столбца — подсказкой (2026-10-21).
    static void HeadIcon(Rect r, Texture2D tex, string key)
    {
        GUI.DrawTexture(r, tex);
        RMUI.Tip(r, key);
    }

    /// Свои часы (2026-10-21): значок расписания ванили — список, не часы; рисует Tools/textures.py, как значки навыков.
    static readonly CachedTexture ClockTex = new("UI/Icons/replimatClock");

    /// Самая узкая правая часть окна: таблица навыков не обрезается (столбцы, зазор перед числами), в блоках черт и генов — BlockCols
    /// карточек в ряд (запас 4: ваниль округляет окно вниз); плюс поля блока и прокрутка.
    public static float MinW
    {
        get
        {
            GameFont f = Text.Font;
            Text.Font = GameFont.Small;
            var c = SkillCols(1f, "00 000");
            Text.Font = f;
            return Mathf.Max(c.name + c.icons + c.passion + c.level + c.time + c.value + 16f, BlockCols * (MinCardW + CardGap) - CardGap + 4f)
                + 2f * Pad + 16f;
        }
    }

    /// Сверху вниз (под заданием) блоки без вкладок (2026-10-20): внешность, навыки, черты, гены; кнопок шаблона внизу нет
    /// (2026-10-21): сохраняют кнопки задания, удаляет значок на карточке шаблона.
    public void Draw(Rect r)
    {
        float y = r.y;
        void Next(float hgt, Action<Rect> draw)
        {
            draw(new Rect(r.x, y, r.width, hgt));
            y += hgt + RMUI.Gap;
        }
        Next(LookH, b => Look(Block(b, "RM_Appearance".Translate(), Human.LookBlock)));
        Next(SkillsH, b => Skills(Block(b, "Skills".Translate(), Human.SkillsBlock)));
        Next(Traits(r.width), b => Traits(b.width, b));
        Next(Genes(r.width), b => Genes(b.width, b));
    }

    /// Рамка блока: название шрифтом Medium, справа вверху «Сбросить» блока (неактивна, когда блок уже как был);
    /// возвращает место под шапкой.
    Rect Block(Rect r, string title, int block)
    {
        Widgets.DrawBoxSolidWithOutline(r, RMUI.CardFill, RMUI.Edge);
        float rw = Text.CalcSize("ResetButton".Translate()).x + 32f;
        Rect hr = new(r.x + Pad, r.y + Pad, r.width - 2f * Pad, HeadH);
        Text.Font = GameFont.Medium;
        RMUI.Label(new Rect(hr.x, hr.y, hr.width - rw, HeadH), title.Truncate(hr.width - rw));
        Text.Font = GameFont.Small;
        if (RMUI.Button(new Rect(hr.xMax - rw, hr.y + (HeadH - BtnH) / 2f, rw, BtnH), "ResetButton".Translate(),
                Human.Block(h.Encode(), block) != Human.Block(Was.Encode(), block)))
        {
            h.TakeBlock(Was, block);
            bufs.Clear();
        }
        return new Rect(hr.x, hr.yMax + 8f, hr.width, r.yMax - Pad - hr.yMax - 8f);
    }

    // ---------------- шаблон: название, кнопки ----------------

    /// Название — строкой на фоне выбранного, над заданием (2026-10-20: ниже — всё этой пешки).
    /// Без «Переименовать»: оно на карточке шаблона и сразу меняет название здесь (2026-10-21).
    public void Strip(Rect r)
    {
        Widgets.DrawHighlightSelected(r);
        RMUI.TitleRename(new Rect(r.x + Pad, r.y + StripPad, r.width - 2f * Pad, HeadH), h);
    }

    // ---------------- карточки ----------------

    /// Карточки во всю ширину строки, все одного размера: зазор 8; все влезают в ряд — растянуты на него (без дыры в конце), но не шире полутора минимальных.
    /// Своя прокрутка — только когда карточки не влезают: иначе колесо мыши над ними листает правую часть окна (2026-10-20).
    /// cardH — высота по ширине карточки. Клик — pick; can — нельзя выбрать: бледная и не нажимается; over — карточка под мышью.
    /// row — все одним рядом, сколько бы ни было (сжаты, без прокрутки).
    void Grid<T>(Rect outRect, List<T> items, float minW, Func<float, float> cardH, Func<T, bool> on, Action<Rect, T> draw, Action<T> pick,
        Func<T, bool> can = null, Action<T> over = null, bool row = false)
    {
        const float gap = 8f, pad = 3f;
        (int cols, float cw, float ch, float height) Lay(float width)
        {
            width -= 2f * pad;
            int cols = row ? Mathf.Max(1, items.Count) : Mathf.Max(1, (int)((width + gap) / (minW + gap) + 0.001f));
            if (!row && items.Count > 0 && items.Count < cols && (width + gap) / items.Count - gap <= 1.5f * minW) cols = items.Count;
            float cw = (width + gap) / cols - gap, ch = cardH(cw);
            return (cols, cw, ch, 2f * pad + (items.Count + cols - 1) / cols * (ch + gap) - gap);
        }
        var (cols, cw, ch, height) = Lay(outRect.width);
        bool scrolls = !row && height > outRect.height;
        if (scrolls)
        {
            (cols, cw, ch, height) = Lay(outRect.width - 16f);
            Widgets.BeginScrollView(outRect, ref scroll, new Rect(0f, 0f, outRect.width - 16f, height));
        }
        else Widgets.BeginGroup(outRect);
        for (int i = 0; i < items.Count; i++)
        {
            T x = items[i];
            Rect c = new(pad + i % cols * (cw + gap), pad + i / cols * (ch + gap), cw, ch);
            bool ok = can?.Invoke(x) ?? true;
            Widgets.DrawOptionBackground(c, on(x));
            GUI.color = ok ? Color.white : new Color(1f, 1f, 1f, 0.35f);
            draw(c, x);
            GUI.color = Color.white;
            if (Mouse.IsOver(c)) over?.Invoke(x);
            if (ok && Widgets.ButtonInvisible(c))
            {
                pick(x);
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }
        }
        if (scrolls) Widgets.EndScrollView();
        else Widgets.EndGroup();
    }

    Color SkinColor => h.skin?.skinColorBase ?? Color.white;
    Color HairColor => h.hairColor?.hairColorOverride ?? Color.white;

    static void Tex(Rect r, Texture2D t, Color c)
    {
        if (t == null) return;
        GUI.color = c;
        GUI.DrawTexture(r, t);
        GUI.color = Color.white;
    }

    static Texture2D South(string path) => path.NullOrEmpty() ? null : ContentFinder<Texture2D>.Get(path + "_south", false);

    /// Голова с волосами и бородой, как в игре (волосы и борода — на месте головы, борода со сдвигом головы); s — сторона квадрата
    /// текстуры головы (1,5 клетки).
    void Head(Rect at, HeadTypeDef head, HairDef hair, BeardDef beard)
    {
        Tex(at, head?.Icon, SkinColor);
        if (beard != null && !beard.noGraphic)
            Tex(new Rect(at.x, at.y - (head?.beardOffset.z ?? 0f) / 1.5f * at.height, at.width, at.height), South(beard.texPath), HairColor);
        if (hair != null && !hair.noGraphic) Tex(at, South(hair.texPath), HairColor);
    }

    /// Лицо крупно: голова в 1,6 раза больше карточки, обрезана по ней (на текстуре голова — середина квадрата).
    void Face(Rect c, HeadTypeDef head, HairDef hair, BeardDef beard)
    {
        GUI.BeginGroup(c);
        float s = Mathf.Min(c.width, c.height) * 1.6f;
        Head(new Rect((c.width - s) / 2f, (c.height - s) / 2f + s * 0.06f, s, s), head, hair, beard);
        GUI.EndGroup();
    }

    /// Фигура телосложения: тело ванили цвета кожи, над ним голова на сдвиге головы этого тела (0,34 клетки вверх), как в игре.
    void Figure(Rect c, BodyTypeDef body)
    {
        float s = Mathf.Min(c.width, c.height / 1.25f);
        Rect b = new(c.center.x - s / 2f, c.yMax - s, s, s);
        Tex(b, body.Icon, SkinColor);
        Head(new Rect(b.x, b.y - body.headOffset.y / 1.5f * s, s, s), h.head, h.hair, h.gender == Gender.Male ? h.beard : null);
    }

    /// Подписи без слова, общего для всех (у тел — «телосложение»: вкладка и так «Тело»), с заглавной.
    static List<string> Unshared(List<string> labels)
    {
        string[][] w = labels.Select(l => l.Split(' ')).ToArray();
        if (w.Length < 2 || w.Any(x => x.Length < 2)) return labels;
        if (w.All(x => x[x.Length - 1] == w[0][w[0].Length - 1])) return w.Select(x => string.Join(" ", x.Take(x.Length - 1)).CapitalizeFirst()).ToList();
        if (w.All(x => x[0] == w[0][0])) return w.Select(x => string.Join(" ", x.Skip(1)).CapitalizeFirst()).ToList();
        return labels;
    }

    /// Высота подписи карточек: самая длинная подпись списка в ширину карточки, не больше двух строк.
    static float LabelH(IEnumerable<string> labels, float cw) => Mathf.Min(2f * Text.LineHeight, labels.Max(l => Text.CalcHeight(l, cw - 8f)));

    /// Карточка с подписью снизу (по центру, до двух строк) и картинкой над ней; lh — высота подписи у всех карточек списка.
    static void Labeled(Rect c, string label, float lh, Action<Rect> pic)
    {
        pic(new Rect(c.x + 4f, c.y + 4f, c.width - 8f, c.height - lh - 8f));
        Text.Anchor = TextAnchor.UpperCenter;
        RMUI.Label(new Rect(c.x + 4f, c.yMax - lh - 4f, c.width - 8f, lh), label);
        Text.Anchor = TextAnchor.UpperLeft;
    }

    static void Swatch(Rect c, Color? color) => Widgets.DrawBoxSolid(c.ContractedBy(5f), color ?? Color.white);

    BodyTypeDef BodyOf(GeneDef g) => g.bodyType switch
    {
        GeneticBodyType.Fat => BodyTypeDefOf.Fat,
        GeneticBodyType.Thin => BodyTypeDefOf.Thin,
        GeneticBodyType.Hulk => BodyTypeDefOf.Hulk,
        _ => h.gender == Gender.Male ? BodyTypeDefOf.Male : BodyTypeDefOf.Female,
    };

    // ---------------- внешность ----------------

    /// Слева портрет (меняется сразу), пол и «Случайно» — во всю высоту раздела; справа — что выбирать (выбранное обведено) и карточки:
    /// низ карточек — вровень с «Случайно» (2026-10-20), все карточки списка одного размера и рядами без дыр.
    void Look(Rect r)
    {
        float ph = r.height - 2f * (PartH + 8f), lw = Mathf.Round(ph / 1.35f);
        Rect pr = new(r.x, r.y, lw, ph);
        Widgets.DrawMenuSection(pr);
        if (h.Portrait is { } p) GUI.DrawTexture(pr, PortraitsCache.Get(p, pr.size, Rot4.South, default, 1f));
        float y = pr.yMax + 8f;
        bool female = RMUI.Choice(new Rect(r.x, y, lw, PartH), Gender.Female.GetLabel().CapitalizeFirst(), Gender.Male.GetLabel().CapitalizeFirst(), h.gender == Gender.Female);
        if (female != (h.gender == Gender.Female))
        {
            h.gender = female ? Gender.Female : Gender.Male;
            h.Fixed();
        }
        if (Widgets.ButtonText(new Rect(r.x, r.yMax - PartH, lw, PartH), "Randomize".Translate())) h.Randomize(Dice);

        Rect rr = new(pr.xMax + RMUI.Gap, r.y, r.xMax - pr.xMax - RMUI.Gap, r.height);
        List<Part> parts = ((Part[])Enum.GetValues(typeof(Part))).Where(x => x != Part.Beard || h.gender == Gender.Male).ToList();
        if (!parts.Contains(part)) part = Part.Body;
        // не влезли в ряд по самой длинной подписи — в несколько рядов поровну (2026-10-20)
        float partW = parts.Max(x => Text.CalcSize(PartLabel(x)).x) + 16f;
        int rows = Mathf.CeilToInt(parts.Count / (float)Mathf.Clamp((int)((rr.width + 6f) / (partW + 6f)), 1, parts.Count)), per = Mathf.CeilToInt(parts.Count / (float)rows);
        float sw = (rr.width - (per - 1) * 6f) / per;
        for (int i = 0; i < parts.Count; i++)
        {
            Rect b = new(rr.x + i % per * (sw + 6f), rr.y + i / per * (PartH + 6f), sw, PartH);
            if (Widgets.ButtonText(b, PartLabel(parts[i]).Truncate(sw - 8f)))
            {
                part = parts[i];
                scroll = Vector2.zero;
            }
            if (part == parts[i]) Widgets.DrawBox(b, 2);
        }
        float gy = rows * (PartH + 6f) + 2f;
        Rect g = new(rr.x, rr.y + gy, rr.width, rr.height - gy);
        BeardDef beard = h.gender == Gender.Male ? h.beard : null;
        switch (part)
        {
            case Part.Body:
                // тела — все четыре одним рядом, вытянуты до низа портрета (2026-10-20); подпись — без общего слова
                List<GeneDef> bodies = Human.Bodies;
                List<string> bl = Unshared(bodies.Select(x => x.LabelCap.ToString()).ToList());
                string Bl(GeneDef x) => bl[bodies.IndexOf(x)];
                Grid(g, bodies, 0f, cw => Mathf.Max(pr.yMax - g.y - 3f, cw * 1.15f + LabelH(bl, cw)), x => h.body == x,
                    (c, x) => Labeled(c, Bl(x), LabelH(bl, c.width), ic => Figure(ic, BodyOf(x))), x => h.body = x, row: true);
                break;
            case Part.Head:
                Grid(g, h.Heads, 64f, cw => cw, x => h.head == x, (c, x) => Face(c, x, h.hair, beard), x => h.head = x);
                break;
            case Part.Skin:
                Grid(g, Human.Skins, 40f, cw => cw, x => h.skin == x, (c, x) => Swatch(c, x.skinColorBase), x => h.skin = x);
                break;
            case Part.Hair:
                List<HairDef> hairs = h.Hairs;
                Grid(g, hairs, 84f, cw => cw * 0.8f + LabelH(hairs.Select(x => x.LabelCap.ToString()), cw), x => h.hair == x,
                    (c, x) => Labeled(c, x.LabelCap, LabelH(hairs.Select(a => a.LabelCap.ToString()), c.width), ic => Face(ic, h.head, x, beard)), x => h.hair = x);
                break;
            case Part.HairColor:
                Grid(g, Human.HairColors, 40f, cw => cw, x => h.hairColor == x, (c, x) => Swatch(c, x.hairColorOverride), x => h.hairColor = x);
                break;
            default:
                List<BeardDef> beards = h.Beards;
                Grid(g, beards, 84f, cw => cw * 0.8f + LabelH(beards.Select(x => x.LabelCap.ToString()), cw), x => h.beard == x,
                    (c, x) => Labeled(c, x.LabelCap, LabelH(beards.Select(a => a.LabelCap.ToString()), c.width), ic => Face(ic, h.head, h.hair, x)), x => h.beard = x);
                break;
        }
    }

    static string PartLabel(Part p) => p switch
    {
        Part.Body => "RM_Body".Translate(),
        Part.Head => "RM_Head".Translate(),
        Part.Skin => Human.Skins.FirstOrDefault()?.LabelCap ?? "-",
        Part.Hair => "RM_Hair".Translate(),
        Part.HairColor => "RM_HairColor".Translate(),
        _ => "RM_Beard".Translate(),
    };

    // ---------------- навыки ----------------

    /// Таблица с шапкой, столбцы как в ванили (2026-10-20): навык (перед названием — свой значок, 2026-10-21); значки ↺ (строка к началу), «Копировать», «Вставить» (уровень
    /// с интересом на другой навык) — без шапки, как в расписании ванили; интерес (клик: нет → ● → ●●); уровень (−1, поле, +1; склонность
    /// черт — «+N» справа); время и стоимость строки.
    void Skills(Rect r)
    {
        float speed = printer.Speed(Pat);
        List<SkillDef> skills = DefDatabase<SkillDef>.AllDefsListForReading.OrderByDescending(s => s.listOrder).ToList();
        float y = r.y, fh = SkillRowH - 4f, ib = fh;
        // столбцы: числа справа не обрезаются — ширина по самому длинному значению; навык — по самому длинному названию
        var (nw, iw, pw, ew, tw, vw) = SkillCols(speed, Fmt.Num(skills.Max(s => h.SkillValue(s) * Rules.BioMarkup)));
        ew -= 8f;
        float ix = r.x + nw, px = ix + iw, ex = px + pw + 8f, vx = r.xMax - vw, tx = vx - tw;
        Text.Anchor = TextAnchor.MiddleLeft;
        GUI.color = RMUI.Muted;
        RMUI.Label(new Rect(r.x + 6f, y, nw, SkillHeadH - 4f), "Skill".Translate());
        Text.Anchor = TextAnchor.MiddleCenter;
        RMUI.Label(new Rect(ex, y, ew, SkillHeadH - 4f), "RM_ColLevel".Translate());
        GUI.color = Color.white;
        // значки ванили: интерес — над огоньками, время и стоимость — у правого края чисел
        float hy = y + (SkillHeadH - 4f - fh) / 2f;
        HeadIcon(new Rect(px + (pw - fh) / 2f, hy, fh, fh), SkillUI.PassionMajorIcon, "Passion");
        HeadIcon(new Rect(tx + tw - fh, hy, fh, fh), ClockTex.Texture, "RM_ColTime");
        HeadIcon(new Rect(vx + vw - 6f - fh, hy, fh, fh), ThingDefOf.Silver.uiIcon, "RM_ColValue");
        y += SkillHeadH;
        for (int i = 0; i < skills.Count; i++)
        {
            SkillDef s = skills[i];
            if (i % 2 == 0) Widgets.DrawAltRect(new Rect(r.x, y, r.width, SkillRowH));
            Text.Anchor = TextAnchor.MiddleLeft;
            RMUI.SkillIcon(new Rect(r.x + 6f, y + 2f, fh, fh), s);
            RMUI.Label(new Rect(r.x + 10f + fh, y, nw - 10f - fh, SkillRowH), s.skillLabel.CapitalizeFirst());
            int lvl = h.Level(s);
            Rect Ic(int k) => new(ix + k * (ib + 4f), y + 2f, ib, ib);
            // ↺ — вся строка к началу: уровень 0, без интереса (2026-10-20)
            if (ITab_RMBills.Icon(Ic(0), ResetTex.Texture, lvl > 0 || h.PassionOf(s) != RimWorld.Passion.None))
            {
                lvl = 0;
                h.passions[s] = RimWorld.Passion.None;
                bufs.Remove(s);
                SoundDefOf.Checkbox_TurnedOff.PlayOneShotOnCamera();
            }
            if (ITab_RMBills.Icon(Ic(1), TexButton.Copy, true))
            {
                clip = (lvl, h.PassionOf(s));
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }
            if (ITab_RMBills.Icon(Ic(2), TexButton.Paste, clip != null) && clip is { } cl)
            {
                lvl = cl.level;
                if (h.PassionAllowed(s)) h.passions[s] = cl.passion;
                bufs.Remove(s);
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
            }
            Passion(new Rect(px + (pw - fh) / 2f, y + 2f, fh, fh), s);
            bufs.TryGetValue(s, out string buf);
            buf ??= lvl.ToString();
            RMUI.CountEntry(new Rect(ex, y + 2f, 3f * 34f, fh), ref lvl, ref buf, 0);
            int c = Mathf.Clamp(lvl, 0, Human.MaxLevel);
            if (c != lvl) buf = c.ToString();
            bufs[s] = buf;
            h.levels[s] = c;
            int apt = h.Aptitude(s);
            Text.Anchor = TextAnchor.MiddleCenter;
            if (apt != 0) RMUI.Label(new Rect(ex + 3f * 34f, y, 30f, SkillRowH), (apt > 0 ? "+" : "") + apt);
            Text.Anchor = TextAnchor.MiddleRight;
            RMUI.Label(new Rect(tx, y, tw, SkillRowH), Fmt.Time(Human.SkillTicks(c) / speed));
            RMUI.Label(new Rect(vx, y, vw - 6f, SkillRowH), Fmt.Num(h.SkillValue(s) * Rules.BioMarkup));
            y += SkillRowH;
        }
        Text.Anchor = TextAnchor.UpperLeft;
    }

    /// Интерес — ванильные огоньки; конфликт с выбранной чертой — нельзя (кнопка серая).
    void Passion(Rect r, SkillDef s)
    {
        Passion p = h.PassionOf(s);
        bool allowed = h.PassionAllowed(s);
        Widgets.DrawBox(r);
        GUI.color = allowed ? Color.white : Color.gray;
        if (p != RimWorld.Passion.None) GUI.DrawTexture(r.ContractedBy(2f), p == RimWorld.Passion.Major ? SkillUI.PassionMajorIcon : SkillUI.PassionMinorIcon);
        GUI.color = Color.white;
        Widgets.DrawHighlightIfMouseover(r);
        if (!Widgets.ButtonInvisible(r) || !allowed) return;
        h.passions[s] = (Passion)(((int)p + 1) % 3);
        SoundDefOf.Tick_High.PlayOneShotOnCamera();
    }

    // ---------------- карточки черт и генов ----------------

    /// Карточки черт и генов растянуты по ряду, зазор 8 (2026-10-21): посередине окна по 2 в ряд, в блоках шаблона всегда по 3
    /// (правая часть окна не уже 3 карточек, MinW; окно почти как у репликатора), названия до трёх строк. Черта — название и стоимость;
    /// ген — значок, название, стоимость.
    public const int MidCols = 2, BlockCols = 3;
    public const float CardGap = 8f, IconS = 40f;
    static float LineH => Text.LineHeightOf(GameFont.Small);
    static float NameH => 3f * LineH;
    /// top — пустая полоса сверху под значок «Удалить» (посередине окна, 2026-10-21); в блоках шаблона значка нет — и полосы.
    public static float TraitCardH(float top = 0f) => 4f + top + NameH + LineH + 4f;
    public static float GeneCardH => 4f + IconS + NameH + LineH + 4f;

    static (LoadedLanguage lang, float w) minCardW;

    /// Самая узкая карточка, где любое название черты (оба пола) и гена игры целиком: до трёх строк, слово не разрывается.
    public static float MinCardW
    {
        get
        {
            if (minCardW.lang == LanguageDatabase.activeLanguage) return minCardW.w;
            GameFont f = Text.Font;
            Text.Font = GameFont.Small;
            List<string> names = DefDatabase<TraitDef>.AllDefs.SelectMany(d => d.degreeDatas)
                .SelectMany(x => new[] { x.GetLabelCapFor(Gender.Male), x.GetLabelCapFor(Gender.Female) })
                .Concat(DefDatabase<GeneDef>.AllDefs.Select(g => g.LabelCap.ToString())).Where(s => !s.NullOrEmpty()).Distinct().ToList();
            float w = Mathf.Ceil(names.SelectMany(s => s.Split(' ')).Max(x => Text.CalcSize(x).x)), three = Text.CalcHeight("A\nA\nA", w) + 0.5f;
            while (names.Any(s => Text.CalcHeight(s, w) > three)) w += 2f;
            Text.Font = f;
            minCardW = (LanguageDatabase.activeLanguage, w + 8f);
            return minCardW.w;
        }
    }

    /// Карточек в ряду ширины w: не больше max, каждая не уже MinCardW.
    static int Cols(float w, int max) => Mathf.Clamp((int)((w + CardGap) / (MinCardW + CardGap) + 0.001f), 1, max);

    /// Высота n карточек рядами в ширину w; без карточек — строка («Нет»).
    public static float GridH(int n, float w, int max, float ch) => n == 0 ? LineH : (n + Cols(w, max) - 1) / Cols(w, max) * (ch + CardGap) - CardGap;

    /// Карточки рядами во всю ширину r от левого верхнего угла; список — копией: клик по карточке меняет его.
    public static void Grid<T>(Rect r, List<T> items, int max, float ch, Action<Rect, T> tile)
    {
        int cols = Cols(r.width, max);
        float cw = (r.width + CardGap) / cols - CardGap;
        List<T> l = items.ToList();
        for (int i = 0; i < l.Count; i++) tile(new Rect(r.x + i % cols * (cw + CardGap), r.y + i / cols * (ch + CardGap), cw, ch), l[i]);
    }

    /// Карточка черты: сверху полоса top, под ней название, внизу стоимость со знаком; подсказка — у h (пол, «Репликант» в описании).
    public static void TraitCard(Rect c, Human h, (TraitDef def, int degree) t, float top = 0f)
    {
        NameAndPrice(c, new Rect(c.x + 4f, c.y + 4f + top, c.width - 8f, c.height - 8f - LineH - top), h.TraitLabel(t), Human.TraitPrice(t));
        TraitTip(c, h, t);
    }

    /// Описание черты и гена подсказкой над r — у их карточек и на карточке человека (2026-10-21).
    public static void TraitTip(Rect r, Human h, (TraitDef def, int degree) t)
    {
        if (Mouse.IsOver(r)) TooltipHandler.TipRegion(r, new TipSignal(() => TraitTip(h, t), t.def.shortHash * 7 + t.degree));
    }

    public static void GeneTip(Rect r, GeneDef g)
    {
        if (Mouse.IsOver(r)) TooltipHandler.TipRegion(r, new TipSignal(() => GeneTip(g), g.shortHash));
    }

    static readonly CachedTexture EndoBg = new("UI/Icons/Genes/GeneBackground_Endogene"), ArcBg = new("UI/Icons/Genes/GeneBackground_ArchiteGene");

    /// Значок гена на фоне ванили (врождённый, у архитового — архитовый); бледность — от GUI.color.
    public static void GeneIcon(Rect ic, GeneDef g)
    {
        float a = GUI.color.a;
        Color Faded(Color x) => new(x.r, x.g, x.b, x.a * a);
        Tex(ic, (g.biostatArc > 0 ? ArcBg : EndoBg).Texture, Faded(Color.white));
        Tex(ic.ContractedBy(ic.width * 0.05f), g.Icon, Faded(g.IconColor));
        GUI.color = Faded(Color.white);
    }

    /// Карточка гена: сверху значок, под ним название, внизу стоимость со знаком.
    public static void GeneCard(Rect c, GeneDef g)
    {
        Rect ic = new(c.center.x - IconS / 2f, c.y + 4f, IconS, IconS);
        GeneIcon(ic, g);
        NameAndPrice(c, new Rect(c.x + 4f, ic.yMax, c.width - 8f, NameH), g.LabelCap, Human.GenePrice(g));
        GeneTip(c, g);
    }

    /// Название по центру name — до трёх строк, целиком (ширина карточки — MinCardW); стоимость — внизу карточки. Бледность — от GUI.color.
    static void NameAndPrice(Rect c, Rect name, string label, float price)
    {
        float a = GUI.color.a;
        GUI.color = new Color(1f, 1f, 1f, a);
        Text.Anchor = TextAnchor.MiddleCenter;
        RMUI.Label(name, label);
        GUI.color = new Color(RMUI.Muted.r, RMUI.Muted.g, RMUI.Muted.b, a);
        RMUI.Label(new Rect(c.x, c.yMax - 4f - LineH, c.width, LineH), Signed(price * Rules.BioMarkup));
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = new Color(1f, 1f, 1f, a);
    }

    /// Стоимость со знаком: «+175», «−175».
    static string Signed(float v) => v > 0f ? "+" + Fmt.Num(v) : v < 0f ? "−" + Fmt.Num(-v) : "0";

    static string PriceLine(float price) => RMUI.Tr("RM_ValueLine", RMUI.Tr("RM_SilverN", Signed(price * Rules.BioMarkup)));

    /// Подсказка черты (2026-10-21 — исключение из «без подсказок»): «Название: описание», без цвета. Описание — раз на черту
    /// и пол: грамматика ванили.
    static readonly Dictionary<(TraitDef, int, Gender), string> traitDescs = new();

    static string TraitTip(Human h, (TraitDef def, int degree) t)
    {
        var k = (t.def, t.degree, h.gender);
        if (!traitDescs.TryGetValue(k, out string d) && (d = h.TraitDesc(t)).Length > 0) traitDescs[k] = d;
        return h.TraitLabel(t) + (d.Length > 0 ? ": " + d : "");
    }

    /// Подсказка гена: название, стоимость, сложность, метаболизм (не 0), архиты (больше 0), ниже — описание ванили без цвета, пустых строк
    /// и её строк сложности, метаболизма и архитов (они — выше).
    static readonly Dictionary<GeneDef, string> geneDescs = new();

    static string GeneTip(GeneDef g)
    {
        string Stat(string key, string v) => key.Translate().CapitalizeFirst() + ": " + v;
        if (!geneDescs.TryGetValue(g, out string d))
        {
            string[] stats = { Stat("Complexity", ""), Stat("Metabolism", ""), Stat("ArchitesRequired", "") };
            geneDescs[g] = d = string.Join("\n", g.DescriptionFull.StripTags().Split('\n').Select(l => l.TrimEnd())
                .Where(l => l.Trim().Length > 0 && !stats.Any(x => l.CapitalizeFirst().StartsWith(x.TrimEnd()))));
        }
        var lines = new List<string> { g.LabelCap, PriceLine(Human.GenePrice(g)), Stat("Complexity", g.biostatCpx.ToString()) };
        if (g.biostatMet != 0) lines.Add(Stat("Metabolism", g.biostatMet.ToStringWithSign()));
        if (g.biostatArc > 0) lines.Add(Stat("ArchitesRequired", g.biostatArc.ToString()));
        lines.Add(d);
        return string.Join("\n", lines);
    }

    // ---------------- блоки черт и генов ----------------

    /// Без at — только высота блока.
    float Traits(float w, Rect? at = null) => Cards(w, at, "Traits".Translate().CapitalizeFirst() + " (" + h.traits.Count + " / " + Human.MaxTraits + ")",
        Human.TraitsBlock, h.traits, TraitCardH(), (c, t) => TraitCard(c, h, t), t => h.traits.Remove(t));

    /// Гены шаблона; под ними итоги, как у сборщика генов ванили. Ген не убрать, если шаблон станет недопустимым (Human.CanRemove) — бледный.
    /// Предел метаболизма не даёт добавить или убрать ген базы — вверху блока, над карточками, жёлтая строка, что делать (2026-10-21:
    /// игрок должен понимать, как это работает, и видеть сразу).
    float Genes(float w, Rect? at = null)
    {
        GameComponent_RM gc = GameComponent_RM.Get;
        string warn = gc.geneBase.Any(g => (h.genes.Contains(g) || !gc.Trashed(g)) && h.MetBlocked(g)) ? "RM_MetLimit".Translate().ToString() : null;
        return Cards(w, at, "Genes".Translate().CapitalizeFirst(), Human.GenesBlock, h.genes, GeneCardH, GeneCard, h.Toggle, h.CanRemove, TotalsH, Totals, warn);
    }

    /// Блок черт или генов (2026-10-21): только карточки рядами во всю ширину блока, как посередине окна; блок растёт рядами.
    /// Клик по карточке — убрать (can false — нельзя: бледная); пусто — «Нет»; над карточками — жёлтая строка warn, под ними — foot
    /// высотой footH (итоги генов). Без at — только высота.
    float Cards<T>(float w, Rect? at, string title, int block, List<T> items, float ch, Action<Rect, T> card, Action<T> remove,
        Func<T, bool> can = null, float footH = 0f, Action<Rect> foot = null, string warn = null)
    {
        float gh = GridH(items.Count, w - 2f * Pad, BlockCols, ch), fh = items.Count > 0 && foot != null ? 8f + footH : 0f,
            wh = warn == null ? 0f : Text.CalcHeight(warn, w - 2f * Pad) + 8f;
        if (at is { } r)
        {
            Rect inner = Block(r, title, block);
            if (warn != null)
            {
                GUI.color = RMUI.Yellow;
                RMUI.Label(new Rect(inner.x, inner.y, inner.width, wh - 8f), warn);
                GUI.color = Color.white;
                inner.yMin += wh;
            }
            if (items.Count == 0) Muted(new Rect(inner.x, inner.y, inner.width, gh), "None".Translate());
            Grid(inner, items, BlockCols, ch, (c, x) =>
            {
                bool ok = can?.Invoke(x) ?? true;
                Widgets.DrawOptionBackground(c, true);
                GUI.color = ok ? Color.white : new Color(1f, 1f, 1f, 0.35f);
                card(c, x);
                GUI.color = Color.white;
                if (!ok || !Widgets.ButtonInvisible(c)) return;
                remove(x);
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
            });
            if (fh > 0f) foot(new Rect(inner.x, inner.y + gh + 8f, inner.width, footH));
        }
        return BlockH(wh + gh + fh);
    }

    static void Muted(Rect r, string s)
    {
        GUI.color = RMUI.Muted;
        Text.Anchor = TextAnchor.MiddleLeft;
        RMUI.Label(r, s);
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
    }

    /// Итоги генов шаблона — строки сборщика генов ванили: сложность, метаболизм со скоростью голода.
    List<(Texture2D icon, string label, string value, string note)> TotalRows
    {
        get
        {
            int met = h.Met;
            return new()
            {
                (GeneUtility.GCXTex.Texture, "Complexity".Translate().CapitalizeFirst(), h.Cpx.ToString(), null),
                (GeneUtility.METTex.Texture, "Metabolism".Translate().CapitalizeFirst(), met.ToStringWithSign(),
                    met == 0 ? null : "HungerRate".Translate() + " x" + GeneTuning.MetabolismToFoodConsumptionFactorCurve.Evaluate(met).ToStringPercent()),
            };
        }
    }

    float TotalsH => TotalRows.Count * SkillRowH;

    /// Строки итогов — сверху r.
    void Totals(Rect r)
    {
        var rows = TotalRows;
        float lw = rows.Max(x => Text.CalcSize(x.label).x), vw = rows.Max(x => Text.CalcSize(x.value).x);
        Text.Anchor = TextAnchor.MiddleLeft;
        for (int i = 0; i < rows.Count; i++)
        {
            var (icon, label, value, note) = rows[i];
            Rect row = new(r.x, r.y + i * SkillRowH, r.width, SkillRowH);
            if (i % 2 == 0) Widgets.DrawAltRect(row);
            Tex(new Rect(row.x + 6f, row.y + 1f, 22f, 22f), icon, Color.white);
            float x = row.x + 34f;
            RMUI.Label(new Rect(x, row.y, lw + 2f, SkillRowH), label);
            x += lw + 16f;
            RMUI.Label(new Rect(x, row.y, vw + 2f, SkillRowH), value);
            x += vw + 16f;
            if (note != null) RMUI.Label(new Rect(x, row.y, row.xMax - x, SkillRowH), note);
        }
        Text.Anchor = TextAnchor.UpperLeft;
    }
}

/// Работа «Переноска»: выбранного в биосканере заключённого или лежачего человека несут, как в генный экстрактор ванили
/// (колонист идёт сам). Животных ведёт работа «Животные» (WorkGiver_RMBioscanner).
public class WorkGiver_RMCarryToBioscanner : WorkGiver_CarryToBuilding
{
    public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForDef(RMDefOf.RM_Bioscanner);

    public override bool ShouldSkip(Pawn pawn, bool forced = false) => !Rules.BioOn;

    public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
        t is Building_RMBioscanner { Working: false, SelectedPawn: { RaceProps.Humanlike: true } } && base.HasJobOnThing(pawn, t, forced);
}
