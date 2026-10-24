using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace ReplimatMech;

/// Расщепитель — как крематорий: задания ванили с фильтром (прочность, качество, «с мёртвых»), носильщики несут подходящее (предметы
/// и трупы), и оно сразу расщепляется на материю. Задания «Излишки» ставит галка в задании репликатора — всегда в конец списка.
/// Пока расщепляет — светится в цвет реплимата.
public class Building_RMHopper : Building_WorkTable, IThingGlower
{
    public static readonly HashSet<Building_RMHopper> Spawned_ = new();
    const int GlowTime = 120;
    CompPowerTrader power;
    int glowTicks;
    static bool syncing;

    public PowerNet Net => power?.PowerNet;
    public bool Working => Spawned && power.PowerOn && Tanks.All(Net).Count > 0;
    public int GlowTicks => glowTicks;
    public bool ShouldBeLitNow() => glowTicks > 0;

    /// Свет — в цвет реплимата; цвет ставится, пока свет выключен, без лишней перестройки освещения.
    void SetGlow(int ticks)
    {
        bool was = glowTicks > 0, now = ticks > 0;
        CompGlower g = was != now && Spawned ? GetComp<CompGlower>() : null;
        if (now && g != null) g.GlowColor = new ColorInt(Settings.GlowColor) { a = 0 };
        glowTicks = ticks;
        g?.UpdateLit(Map);
    }

    public override void SpawnSetup(Map map, bool respawningAfterLoad)
    {
        base.SpawnSetup(map, respawningAfterLoad);
        power = GetComp<CompPowerTrader>();
        Spawned_.Add(this);
    }

    public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
    {
        Spawned_.Remove(this);
        base.DeSpawn(mode);
    }

    /// До 1.2.0 расщепитель был складом: заданий в сохранении нет.
    public override void ExposeData()
    {
        base.ExposeData();
        if (Scribe.mode == LoadSaveMode.PostLoadInit) billStack ??= new BillStack(this);
    }

    protected override void Tick()
    {
        base.Tick();
        if (glowTicks > 0) SetGlow(glowTicks - 1);
        if (this.IsHashIntervalTick(250)) SyncSurplus(Map);
    }

    /// Задания «Излишки» карты — по галкам в заданиях репликаторов: каждое — в первом расщепителе сети репликатора (нет такого — в первом на карте).
    /// Порядок — по номерам вещей: у всех игроков Multiplayer один.
    public static void SyncSurplus(Map map)
    {
        if (map == null) return;
        List<Building_RMHopper> hoppers = Spawned_.Where(h => h.Map == map).OrderBy(h => h.thingIDNumber).ToList();
        var want = new List<(Building_RMHopper h, CompPrinter c, Pattern p)>();
        if (hoppers.Count > 0)
            foreach (CompPrinter c in CompPrinter.All.Where(c => c.parent.Map == map).OrderBy(c => c.parent.thingIDNumber))
                foreach (Order o in c.orders.Where(o => o.surplus && o.CanSurplus))
                    want.Add((hoppers.FirstOrDefault(h => h.Net == c.Net) ?? hoppers[0], c, o.pattern));
        syncing = true;
        foreach (Building_RMHopper h in hoppers)
        {
            foreach (Bill_RMSurplus b in h.billStack.Bills.OfType<Bill_RMSurplus>().ToList())
                if (!want.Contains((h, b.Printer, b.Pattern))) h.billStack.Delete(b);
            foreach (var (_, c, p) in want.Where(w => w.h == h))
                if (!h.billStack.Bills.OfType<Bill_RMSurplus>().Any(b => b.Printer == c && b.Pattern == p)) h.billStack.AddBill(new Bill_RMSurplus(c, p));
        }
        syncing = false;
    }

    /// Задание «Излишки» удалил игрок — галка в задании репликатора снимается.
    public override void Notify_BillDeleted(Bill bill)
    {
        base.Notify_BillDeleted(bill);
        if (!syncing && bill is Bill_RMSurplus s && s.Order is { } o) o.surplus = false;
    }

    public void Process(Thing t)
    {
        if (t is Corpse c) Demat.Corpse(c, Net, this);
        else Demat.Item(t, Net, this);
        SetGlow(GlowTime);
    }

    protected override void DrawAt(Vector3 drawLoc, bool flip = false)
    {
        base.DrawAt(drawLoc, flip);
        Glow.Hopper(this);
    }

    public override string GetInspectString()
    {
        string s = base.GetInspectString();
        if (Spawned && Tanks.All(Net).Count == 0) s = "RM_NoTanks".Translate() + "\n" + s;
        return s.Trim();
    }
}

/// «Излишки: сталь сверх 225» — несут то, что на складах и в руках сверх цели задания репликатора; живёт, пока в задании стоит галка.
public class Bill_RMSurplus : Bill
{
    Thing printerThing;
    string patternKey;
    Pattern pattern;

    public Bill_RMSurplus() { }

    public Bill_RMSurplus(CompPrinter c, Pattern p) : base(RMDefOf.RM_Split)
    {
        printerThing = c.parent;
        pattern = p;
    }

    public CompPrinter Printer => printerThing is { Spawned: true } t ? t.TryGetComp<CompPrinter>() : null;
    public Pattern Pattern => pattern ??= GameComponent_RM.Get.ByKey(patternKey);
    public Order Order => Printer?.orders.FirstOrDefault(o => o.surplus && o.CanSurplus && o.pattern == Pattern);

    public override string Label => Order is { } o ? RMUI.Tr("RM_SurplusBill", Pattern.Label.UncapitalizeFirst(), o.target) : recipe.label;
    public override string LabelCap => Label.CapitalizeFirst();
    protected override bool CanCopy => false;
    public override bool ShouldDoNow() => !suspended && Order != null;

    /// Вторая строка: чьё задание и сколько лишних.
    public string Info => Order is { } o ? RMUI.Tr("RM_SurplusInfo", Printer.RenamableLabel, Mathf.Max(0, Printer.Surplus(o))) : "";

    /// Что нести: подходящее с полок и у репликатора, не больше лишнего.
    public (Thing thing, int count) Find(Pawn pawn, Thing hopper)
    {
        if (Order is not { } o) return default;
        CompPrinter c = Printer;
        int n = c.Surplus(o);
        if (n <= 0) return default;
        Thing t = WorkGiver_RMCarry.Closest(pawn, hopper, ingredientSearchRadius, ThingRequest.ForDef(Pattern.minified ? ThingDefOf.MinifiedThing : Pattern.def),
            x => CompPrinter.Matches(x, Pattern) && c.Counted(x));
        return (t, t == null ? 0 : Mathf.Min(n, t.stackCount));
    }

    public override void ExposeData()
    {
        base.ExposeData();
        if (Scribe.mode == LoadSaveMode.Saving) patternKey = pattern?.Key;
        Scribe_References.Look(ref printerThing, "printer");
        Scribe_Values.Look(ref patternKey, "pattern");
    }
}

/// Вкладка «Задания» расщепителя — как у репликатора: «Добавить задание» сразу открывает окно нового задания (до «Добавить задание»
/// в нём ничего не создаётся), справа вверху — вставить; строки тащат мышью, в строке — копировать, пауза, окно задания, удалить.
/// «Излишки» не копируются: их ставит и снимает галка в задании репликатора, их окно — его задание.
public class ITab_RMSplitter : ITab
{
    /// Буфер обмена — черновик задания расщепителя (задание ванили сюда не вставить, наше — в верстак ванили).
    public static SplitDraft Clipboard;
    readonly RowList list = new();

    public ITab_RMSplitter()
    {
        size = new Vector2(460f, 480f);
        labelKey = "TabBills";
    }

    /// Ширина — как у панели осмотра под вкладкой.
    protected override void UpdateSize() => size = new Vector2(InspectPaneUtility.PaneWidthFor(Find.WindowStack.WindowOfType<IInspectPane>()), size.y);

    protected override void FillTab()
    {
        if (SelThing is not Building_RMHopper h) return;
        Rect r = new Rect(0f, 0f, size.x, size.y).ContractedBy(10f);
        if (Widgets.ButtonText(new Rect(r.x, r.y, 150f, 29f), "AddBill".Translate())) Find.WindowStack.Add(new Dialog_RMSplit(h, null));
        // вставить — слева от крестика вкладки, как у репликатора
        if (Clipboard != null && ITab_RMBills.Icon(new Rect(size.x - 54f, r.y + 2.5f, 24f, 24f), TexButton.Paste, true))
        {
            Edit.AddSplit(h, Clipboard);
            SoundDefOf.Tick_Low.PlayOneShotOnCamera();
        }
        RMUI.TanksBlock(new Rect(r.x, r.y + 33f, r.width, RMUI.TanksH), h.Net);
        float y = r.y + 37f + RMUI.TanksH;
        List<Bill> bills = h.billStack.Bills.ToList();
        list.Draw(new Rect(r.x, y, r.width, r.yMax - y), bills,
            b => b is Bill_Production p ? RMUI.RowHeight(null, true, null, Empty(p)) : RMUI.RowHeight(null, false, null, true), (row, b) => Row(row, h, b),
            (from, to) => Edit.ReorderSplit(h, from, to));
    }

    static bool Empty(Bill_Production b) => b.ingredientFilter.AllowedDefCount == 0;

    /// Строка: название задания (своё или рецепта); ниже сколько раз, как у задания ванили («Бесконечно» тоже), справа − / + и режим; у пустого
    /// фильтра — жёлтое «Ничего» (2026-10-21: без строки категорий); у «Излишков» — чьё задание и сколько лишних, количества нет.
    static void Row(Rect r, Building_RMHopper h, Bill bill)
    {
        string id = bill.GetUniqueLoadID();
        (Texture2D, Action) pause = (TexButton.Suspend, () =>
        {
            Edit.PauseSplit(h, id, !bill.suspended);
            SoundDefOf.Click.PlayOneShotOnCamera();
        });
        (Texture2D, Action) delete = (TexButton.Delete, () =>
        {
            Edit.DeleteSplit(h, id);
            SoundDefOf.Click.PlayOneShotOnCamera();
        });
        if (bill is Bill_RMSurplus s)
        {
            RMUI.BillRow(r, null, null, s.LabelCap, null, s.Info, RMUI.Muted, null, s.suspended, -1f, pause,
                (ITab_RMBills.Gear.Texture, () =>
                {
                    if (s.Order is { } o) Find.WindowStack.Add(new Dialog_Order(s.Printer, o));
                }), delete);
            return;
        }
        var b = (Bill_Production)bill;
        RMUI.BillRow(r, null, null, b.RenamableLabel, new RMUI.Counter(b.RepeatInfoText, Color.white, n => Edit.CountSplit(h, id, n), b.repeatMode.LabelCap,
                () => Find.WindowStack.Add(RMUI.SplitModeMenu(m => Edit.ModeSplit(h, id, m == BillRepeatModeDefOf.Forever)))),
            Empty(b) ? "Nothing".Translate().ToString() : "", RMUI.Yellow, null, b.suspended, -1f,
            (TexButton.Copy, () =>
            {
                Clipboard = SplitDraft.Of(b);
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }),
            pause, (TexButton.Rename, () => Find.WindowStack.Add(new Dialog_RMRename(b.RenamableLabel, n => Edit.RenameSplit(h, id, n)))),
            (ITab_RMBills.Gear.Texture, () => Find.WindowStack.Add(new Dialog_RMSplit(h, b))), delete);
    }
}

/// Черновик задания расщепителя: окно правит его, «Добавить задание» и «Применить» отдают его расщепителю (Edit), буфер обмена — он же.
/// Не задание ванили: новое задание берёт номер у игры, а в Multiplayer номера раздаёт только синхронная команда.
public sealed class SplitDraft : IRenameable
{
    /// Имя от игрока, как у задания ванили; null — название рецепта.
    public string name;
    public BillRepeatModeDef mode = BillRepeatModeDefOf.RepeatCount;
    public int count = 1;
    public bool suspended;
    public float radius = Bill.MaxIngredientSearchRadius;
    /// Кто работает, как у ванили: пешка, иначе only — любой (0), любой раб (1), любой механоид (2), любой не механоид (3).
    public Pawn pawn;
    public int only;
    public ThingFilter filter = new();

    public static SplitDraft New()
    {
        var d = new SplitDraft();
        d.filter.CopyAllowancesFrom(RMDefOf.RM_Split.defaultIngredientFilter);
        return d;
    }

    public static SplitDraft Of(Bill_Production b)
    {
        var d = new SplitDraft
        {
            mode = b.repeatMode, count = b.repeatCount, suspended = b.suspended, radius = b.ingredientSearchRadius, pawn = b.PawnRestriction,
            only = b.SlavesOnly ? 1 : b.MechsOnly ? 2 : b.NonMechsOnly ? 3 : 0, name = Name(b),
        };
        d.filter.CopyAllowancesFrom(b.ingredientFilter);
        return d;
    }

    /// Копия — в буфер обмена: окно правит свой черновик дальше.
    public SplitDraft Copy()
    {
        var c = (SplitDraft)MemberwiseClone();
        c.filter = new ThingFilter();
        c.filter.CopyAllowancesFrom(filter);
        return c;
    }

    /// Имя, что дал игрок заданию ванили; нет — null.
    public static string Name(Bill_Production b) => b.RenamableLabel == b.BaseLabel ? null : b.RenamableLabel;

    public string BaseLabel => RMDefOf.RM_Split.LabelCap;
    public string InspectLabel => RenamableLabel;
    public string RenamableLabel
    {
        get => name ?? BaseLabel;
        set => SetName(value);
    }

    /// Имя черновика из окна — не сеттером (как Order.SetName: сеттер Multiplayer шлёт командой).
    public void SetName(string v) => name = v.NullOrEmpty() || v == BaseLabel ? null : v;

    public void ApplyTo(Bill_Production b)
    {
        b.RenamableLabel = name;
        (b.repeatMode, b.repeatCount, b.suspended, b.ingredientSearchRadius) = (mode, count, suspended, radius);
        if (pawn != null) b.SetPawnRestriction(pawn);
        else if (only == 1) b.SetAnySlaveRestriction();
        else if (only == 2) b.SetAnyMechRestriction();
        else if (only == 3) b.SetAnyNonMechRestriction();
        else b.SetAnyPawnRestriction();
        b.ingredientFilter.CopyAllowancesFrom(filter);
    }

    /// Фильтр строкой — что разрешено, какие особые фильтры сняты, прочность, качество: для Multiplayer и чтобы видеть правку.
    public string FilterKey
    {
        get => string.Join(";", string.Join(",", filter.AllowedThingDefs.Select(d => d.defName).OrderBy(n => n, StringComparer.Ordinal)),
            string.Join(",", DefDatabase<SpecialThingFilterDef>.AllDefs.Where(f => !filter.Allows(f)).Select(f => f.defName)),
            Num(filter.AllowedHitPointsPercents.min), Num(filter.AllowedHitPointsPercents.max),
            (int)filter.AllowedQualityLevels.min, (int)filter.AllowedQualityLevels.max);
        set
        {
            string[] k = value.Split(';');
            filter.SetDisallowAll();
            foreach (string n in k[0].Split(','))
                if (DefDatabase<ThingDef>.GetNamedSilentFail(n) is { } d) filter.SetAllow(d, true);
            var off = new HashSet<string>(k[1].Split(','));
            foreach (SpecialThingFilterDef f in DefDatabase<SpecialThingFilterDef>.AllDefs) filter.SetAllow(f, !off.Contains(f.defName));
            filter.AllowedHitPointsPercents = new FloatRange(float.Parse(k[2], CultureInfo.InvariantCulture), float.Parse(k[3], CultureInfo.InvariantCulture));
            filter.AllowedQualityLevels = new QualityRange((QualityCategory)int.Parse(k[4]), (QualityCategory)int.Parse(k[5]));
        }
    }

    static string Num(float f) => f.ToString("R", CultureInfo.InvariantCulture);

    public bool Same(SplitDraft o) => name == o.name && mode == o.mode && count == o.count && suspended == o.suspended && radius == o.radius && pawn == o.pawn && only == o.only
                                      && FilterKey == o.FilterKey;
}

/// Окно задания расщепителя — настройки ванили (окно крематория), но одним столбцом (2026-10-20: левая часть была пустой); что
/// делает задание — в «Справке» второй строки (2026-10-18): «сделать X» или «бесконечно» и кто работает, сколько раз, фильтр и
/// радиус поиска. «Повторять до» и склада нет: расщеплённое не остаётся. Кнопки — как в окне задания
/// репликатора: слева «Сбросить», справа «Добавить задание» (новое), «Применить» (есть правка; отдаёт черновик и закрывает окно)
/// или пауза задания в очереди (сразу); крестик и клик мимо — без изменений.
/// Своё окно, не наследник ванильного: то Multiplayer перехватывает и правило бы задание в очереди, а не черновик.
public class Dialog_RMSplit : Window
{
    readonly Building_RMHopper hopper;
    readonly Bill_Production live;
    SplitDraft d;
    readonly ThingFilterUI.UIState filterState = new();
    string countBuf;

    /// Узкое и длинное, как лист (2026-10-20): ширина — по первой строке (значок, название, два значка) или второй (баки, «Справка»),
    /// что шире; высота — до 900.
    public override Vector2 InitialSize
    {
        get
        {
            GameFont f = Text.Font;
            Text.Font = GameFont.Medium;
            float title = 40f + Mathf.Min(Text.CalcSize(d.RenamableLabel).x, 320f) + 16f + 64f + 32f;
            Text.Font = GameFont.Small;
            float row2 = Text.CalcSize(RMUI.TanksText(hopper.Net)).x + 16f + Text.CalcSize("RM_CardHelp".Translate()).x + 32f + 12f;
            Text.Font = f;
            return RMUI.Fit(Mathf.Max(460f, title, row2) + 2f * Margin, 900f);
        }
    }

    public Dialog_RMSplit(Building_RMHopper hopper, Bill_Production live)
    {
        this.hopper = hopper;
        this.live = live;
        d = live != null ? SplitDraft.Of(live) : SplitDraft.New();
        forcePause = true;
        doCloseX = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = true;
    }

    public override void WindowUpdate()
    {
        if (d.radius < GenRadial.MaxRadialPatternRadius) GenDraw.DrawRadiusRing(hopper.Position, d.radius);
    }

    public override void DoWindowContents(Rect inRect)
    {
        if (!hopper.Spawned || live != null && !hopper.billStack.Bills.Contains(live))
        {
            Close();
            return;
        }
        RecipeDef recipe = RMDefOf.RM_Split;
        // пауза — не правка черновика, а кнопка задания в очереди (как у репликатора): черновик держит её как есть
        if (live != null) d.suspended = live.suspended;
        // строка 1 (2026-10-20): значок, название, «Переименовать», «Копировать»; строка 2 — что в баках и «Справка» у правого края
        Widgets.DefIcon(new Rect(inRect.x, inRect.y + 4f, 34f, 34f), RMDefOf.RM_Hopper);
        float rx = RMUI.TitleRename(new Rect(inRect.x + 40f, inRect.y + 4f, inRect.width - 40f - 32f, 34f), d, d.SetName);
        if (ITab_RMBills.Icon(new Rect(rx + 8f, inRect.y + 9f, 24f, 24f), TexButton.Copy, true))
        {
            ITab_RMSplitter.Clipboard = d.Copy();
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
        }
        Rect row = new(inRect.x, inRect.y + 44f, inRect.width, 30f);
        float hw = RMUI.HelpButton(row, recipe.description);
        RMUI.TanksTitle(new Rect(row.x, row.y, row.width - hw, row.height), hopper.Net);
        // один столбец: режим, сколько раз, кто работает, фильтр, радиус; внизу «Сбросить» слева, действие справа (как у репликатора)
        const float bw = 180f, bh = 36f, radiusH = 50f;
        float top = Mid(new Rect(inRect.x, row.yMax + 10f, inRect.width, 0f)) + 10f, fy = inRect.yMax - bh - 10f - radiusH - 10f;
        ThingFilterUI.DoThingFilterConfigWindow(new Rect(inRect.x, top, inRect.width, fy - top), filterState, d.filter, recipe.fixedIngredientFilter, 4,
            null, recipe.forceHiddenSpecialFilters, map: hopper.Map);
        Radius(new Rect(inRect.x, fy + 10f, inRect.width, radiusH));
        Rect lb = new(inRect.x, inRect.yMax - bh, bw, bh), rb = new(inRect.xMax - bw, inRect.yMax - bh, bw, bh);
        SplitDraft def = SplitDraft.New();
        def.suspended = d.suspended;
        if (RMUI.Button(lb, "ResetButton".Translate(), !d.Same(def))) (d, countBuf) = (def, null);
        if (live == null)
        {
            if (!RMUI.Button(rb, "AddBill".Translate())) return;
            Edit.AddSplit(hopper, d);
        }
        else if (!d.Same(SplitDraft.Of(live)))
        {
            if (!RMUI.Button(rb, "RM_Apply".Translate())) return;
            Edit.ApplySplit(hopper, live.GetUniqueLoadID(), d);
        }
        else
        {
            if (!RMUI.Button(rb, (live.suspended ? "Suspended" : "NotSuspended").Translate())) return;
            Edit.PauseSplit(hopper, live.GetUniqueLoadID(), !live.suspended);
            SoundDefOf.Click.PlayOneShotOnCamera();
            return;
        }
        SoundDefOf.Tick_Low.PlayOneShotOnCamera();
        Close();
    }

    /// Рамка настроек — строками во всю ширину (2026-10-20): режим («сделать X» или «бесконечно»), сколько раз (у «сделать X»),
    /// кто работает; отдаёт низ рамки.
    float Mid(Rect r)
    {
        const float rowH = 30f, pad = 8f;
        bool count = d.mode == BillRepeatModeDefOf.RepeatCount;
        Rect box = new(r.x, r.y, r.width, pad + (count ? 3 : 2) * (rowH + pad));
        Widgets.DrawMenuSection(box);
        Rect i = box.ContractedBy(pad);
        float y = i.y;
        if (Widgets.ButtonText(new Rect(i.x, y, i.width, rowH), d.mode.LabelCap))
            Find.WindowStack.Add(RMUI.SplitModeMenu(m => d.mode = m));
        y += rowH + pad;
        if (count)
        {
            // подпись — по своему тексту (до трёх цифр поле не сдвигается), поле — сразу за ней до правого края (2026-10-20)
            string label = "RepeatCount".Translate(d.count);
            float lw = Mathf.Max(Text.CalcSize(label).x, Text.CalcSize("RepeatCount".Translate(999)).x) + pad;
            Text.Anchor = TextAnchor.MiddleLeft;
            RMUI.Label(new Rect(i.x, y, lw, rowH), label);
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.IntEntry(new Rect(i.x + lw, y, i.width - lw, rowH), ref d.count, ref countBuf);
            if (d.count < 1) (d.count, countBuf) = (1, "1");
            y += rowH + pad;
        }
        Widgets.Dropdown(new Rect(i.x, y, i.width, rowH), d, x => x.pawn, _ => Workers(), Worker());
        return box.yMax;
    }

    /// Кто носит: все, любой поселенец, любой механоид. Пешка и «любой раб» — только у заданий старых сохранений.
    string Worker() => d.pawn?.LabelShortCap ?? (d.only switch { 1 => "AnySlave", 2 => "AnyMech", 3 => "RM_WorkerColonist", _ => "RM_WorkerAll" }).Translate();

    IEnumerable<Widgets.DropdownMenuElement<Pawn>> Workers()
    {
        Widgets.DropdownMenuElement<Pawn> Any(string key, int only) => new() { option = new FloatMenuOption(key.Translate(), () => (d.pawn, d.only) = (null, only)) };
        yield return Any("RM_WorkerAll", 0);
        if (!ModsConfig.BiotechActive) yield break;
        yield return Any("RM_WorkerColonist", 3);
        yield return Any("AnyMech", 2);
    }

    /// Радиус поиска, как у ванили: до 100 клеток, дальше — без ограничения.
    void Radius(Rect r)
    {
        var l = new Listing_Standard();
        l.Begin(r);
        l.Label("IngredientSearchRadius".Translate() + ": " + (d.radius >= Bill.MaxIngredientSearchRadius ? "Unlimited".TranslateSimple() : d.radius.ToString("F0")));
        d.radius = l.Slider(Mathf.Min(d.radius, 100f), 3f, 100f);
        if (d.radius >= 100f) d.radius = Bill.MaxIngredientSearchRadius;
        l.End();
    }
}

/// Работа «Переноска»: в расщепитель — по его заданиям, в репликатор — труп механоида для воскрешения.
public class WorkGiver_RMCarry : WorkGiver_Scanner
{
    public override PathEndMode PathEndMode => PathEndMode.Touch;
    public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;

    /// По номерам вещей: при равном расстоянии все игроки Multiplayer выберут одно и то же.
    public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) =>
        Building_RMHopper.Spawned_.Where(h => h.Map == pawn.Map).Cast<Thing>()
            .Concat(CompPrinter.All.Where(c => c.parent.Map == pawn.Map && WantsCorpse(c)).Select(c => c.parent))
            .OrderBy(t => t.thingIDNumber).ToList();

    public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) => JobOnThing(pawn, t, forced) != null;

    public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
    {
        if (t.IsForbidden(pawn) || !pawn.CanReach(t, PathEndMode.Touch, Danger.Deadly)) return null;
        if (t is Building_RMHopper h)
        {
            if (!h.Working) return null;
            foreach (Bill bill in h.billStack.Bills)
            {
                if (!bill.ShouldDoNow() || !bill.PawnAllowedToStartAnew(pawn)) continue;
                (Thing thing, int count) = bill is Bill_RMSurplus s ? s.Find(pawn, h)
                    : (Closest(pawn, h, bill.ingredientSearchRadius, ThingRequest.ForGroup(ThingRequestGroup.HaulableEver),
                        x => bill.IsFixedOrAllowedIngredient(x) && x.GetSlotGroup()?.parent is not Building_RMFeeder), 0);
                if (thing != null) return Carry(thing, h, count > 0 ? count : thing.stackCount, bill);
            }
            return null;
        }
        CompPrinter c = t.TryGetComp<CompPrinter>();
        List<Order> want = c?.orders.Where(x => x.status == "RM_StNoCorpse").ToList();
        if (want.NullOrEmpty() || JobDriver_RMCarry.Bringing(t)) return null;
        Thing corpse = Closest(pawn, t, 9999f, ThingRequest.ForGroup(ThingRequestGroup.Corpse),
            x => x is Corpse cp && Pricing.Resurrectable(cp) && want.Any(o => o.Fits(cp) && !CompPrinter.NoBandwidth(o.mechanitor, o.pattern, cp)));
        return corpse == null ? null : Carry(corpse, t, 1, null);
    }

    static bool WantsCorpse(CompPrinter c) => c.Corpse == null && c.orders.Any(o => o.status == "RM_StNoCorpse");

    static Job Carry(Thing thing, Thing to, int count, Bill bill)
    {
        Job job = JobMaker.MakeJob(RMDefOf.RM_Carry, thing, to);
        job.count = count;
        job.bill = bill;
        return job;
    }

    /// Ближайшее к носильщику, что можно взять, не дальше radius от места назначения.
    public static Thing Closest(Pawn pawn, Thing to, float radius, ThingRequest req, Predicate<Thing> ok) =>
        GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, req, PathEndMode.ClosestTouch, TraverseParms.For(pawn), 9999f,
            x => !x.IsForbidden(pawn) && x.Position.InHorDistOf(to.Position, radius) && !x.IsBurning() && pawn.CanReserve(x) && ok(x));
}

/// Несёт A в B: в расщепитель — расщепить сразу (задание расщепителя, сканирование), в репликатор — положить труп внутрь.
public class JobDriver_RMCarry : JobDriver
{
    Thing Target => job.GetTarget(TargetIndex.B).Thing;

    public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed);

    static IEnumerable<Pawn> Carriers(Map map) => map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer).Where(p => p.CurJobDef == RMDefOf.RM_Carry);

    /// Сколько штук шаблона носильщики идут взять (ещё не взяли): их пока считает склад.
    public static int Incoming(Map map, Pattern p) =>
        map == null ? 0 : Carriers(map).Where(x => x.carryTracker.CarriedThing == null && x.CurJob.targetA.Thing is { } t && CompPrinter.Matches(t, p)).Sum(x => x.CurJob.count);

    /// В репликатор уже несут труп.
    public static bool Bringing(Thing to) => Carriers(to.Map).Any(x => x.CurJob.targetB.Thing == to);

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedNullOrForbidden(TargetIndex.B);
        // задание удалили, пока шёл (как у ванили): иначе сохранение ссылается на удалённое задание
        this.FailOn(() => job.bill is { DeletedOrDereferenced: true });
        this.FailOn(() => Target is Building_RMHopper h ? !h.Working
            : Target.TryGetComp<CompPrinter>() is not { } c || c.Corpse != null || TargetThingA is Corpse cp && !c.orders.Any(o => o.Fits(cp)));
        // сканирование отменили, пока шёл
        bool scan = job.bill == null && Target is Building_RMHopper;
        yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.A)
            .FailOn(() => scan && Map.designationManager.DesignationOn(TargetThingA, RMDefOf.RM_Scan) == null);
        yield return Toils_General.DoAtomic(() => Map.designationManager.TryRemoveDesignationOn(TargetThingA, RMDefOf.RM_Scan));
        yield return Toils_Haul.StartCarryThing(TargetIndex.A, false, true).FailOnDestroyedNullOrForbidden(TargetIndex.A);
        yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.Touch);
        Toil deliver = ToilMaker.MakeToil("RM_Deliver");
        deliver.initAction = () =>
        {
            if (pawn.carryTracker.CarriedThing is not { } t) return;
            if (Target is not Building_RMHopper h)
            {
                Target.TryGetComp<CompPrinter>().Hold(t);
                return;
            }
            pawn.carryTracker.innerContainer.Remove(t);
            h.Process(t);
            RMDefOf.RM_Dematerialize.PlayOneShot(new TargetInfo(h.Position, Map));
            job.bill?.Notify_IterationCompleted(pawn, new List<Thing> { t });
        };
        yield return deliver;
    }
}

// ---- сканирование ----

public class Designator_RMScan : Designator
{
    protected override DesignationDef Designation => RMDefOf.RM_Scan;
    public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.FilledRectangle;
    public override bool Visible => RMDefOf.RM_Replication.IsFinished && base.Visible;

    public Designator_RMScan()
    {
        defaultLabel = "RM_DesignatorScan".Translate();
        icon = ContentFinder<Texture2D>.Get("UI/Designators/ReplimatScanPattern");
        soundDragSustain = SoundDefOf.Designate_DragStandard;
        soundDragChanged = SoundDefOf.Designate_DragStandard_Changed;
        soundSucceeded = SoundDefOf.Designate_Haul;
        useMouseIcon = true;
        // на животном и лежачем человеке: уже ждёт, сканеры заняты — кнопка серая с причиной
        showReverseDesignatorDisabledReason = true;
    }

    public override AcceptanceReport CanDesignateCell(IntVec3 c)
    {
        if (!c.InBounds(Map) || c.Fogged(Map)) return false;
        Thing t = c.GetThingList(Map).FirstOrDefault(x => CanDesignateThing(x).Accepted);
        return t != null;
    }

    public override void DesignateSingleCell(IntVec3 c)
    {
        foreach (Thing t in c.GetThingList(Map).ToList())
            if (CanDesignateThing(t).Accepted) DesignateThing(t);
    }

    /// Сканировать — только то, чего нет в шаблонах, или вещь лучше шаблона по качеству. Кнопка есть и на самой вещи.
    /// Животное колонии, сбитое дикое или лежачий человек без ДНК — в биосканер (Building_RMBioscanner.CanSend); свежий труп без ДНК — в расщепитель.
    public override AcceptanceReport CanDesignateThing(Thing t)
    {
        if (t is Pawn p) return Building_RMBioscanner.CanSend(p);
        if (t is Corpse c) return Bio.CorpseDna(c) && RMDefOf.RM_Replication.IsFinished && Map.designationManager.DesignationOn(t, Designation) == null;
        if (!RMDefOf.RM_Replication.IsFinished || t.def.category != ThingCategory.Item || !t.def.EverHaulable || t is Corpse
            || Map.designationManager.DesignationOn(t, Designation) != null) return false;
        Pattern fresh = Pattern.From(t, out QualityCategory q);
        if (fresh == null) return false;
        Pattern known = GameComponent_RM.Get.ByKey(fresh.Key);
        return known == null || known.HasQuality && q > known.Best;
    }

    public override void DesignateThing(Thing t)
    {
        if (t is Pawn p) Building_RMBioscanner.Send(p);
        else Map.designationManager.AddDesignation(new Designation(t, Designation));
    }

    public override void SelectedUpdate() => GenUI.RenderMouseoverBracket();
}

/// Носильщик несёт одну штуку в расщепитель, тот её расщепляет и записывает шаблон — задания расщепителя для этого не нужны.
public class WorkGiver_RMScan : WorkGiver_Scanner
{
    public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;
    public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;

    public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) =>
        pawn.Map.designationManager.SpawnedDesignationsOfDef(RMDefOf.RM_Scan).Select(d => d.target.Thing).ToList();

    public override bool ShouldSkip(Pawn pawn, bool forced = false) => !pawn.Map.designationManager.AnySpawnedDesignationOfDef(RMDefOf.RM_Scan);

    public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
    {
        if (pawn.Map.designationManager.DesignationOn(t, RMDefOf.RM_Scan) == null || t.IsForbidden(pawn) || t.IsBurning()
            || !pawn.CanReserve(t, 1, -1, null, forced)) return false;
        if (FindHopper(pawn, t) != null) return true;
        JobFailReason.Is("RM_NoHopper".Translate());
        return false;
    }

    public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
    {
        Building_RMHopper hopper = FindHopper(pawn, t);
        if (hopper == null) return null;
        Job job = JobMaker.MakeJob(RMDefOf.RM_Carry, t, hopper);
        job.count = 1;
        return job;
    }

    static Building_RMHopper FindHopper(Pawn pawn, Thing t) =>
        Building_RMHopper.Spawned_.Where(h => h.Map == pawn.Map && h.Working && !h.IsForbidden(pawn)
                                               && pawn.CanReach(h, PathEndMode.Touch, Danger.Deadly))
            .OrderBy(h => h.Position.DistanceToSquared(t.Position)).ThenBy(h => h.thingIDNumber).FirstOrDefault();
}
