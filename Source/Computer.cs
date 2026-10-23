using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ReplimatMech;

/// Вкладка «Компьютер» над карточкой компьютера, как «Задания» у репликатора: нажатие открывает окно компьютера.
public class ITab_RMComputer : ITab
{
    public ITab_RMComputer()
    {
        size = new Vector2(10f, 10f);
        labelKey = "RM_TabComputer";
    }

    public override void TabUpdate()
    {
        if (SelThing is Building_RMComputer c) Find.WindowStack.Add(new Dialog_Computer(c));
        CloseTab();
    }

    protected override void FillTab() { }
}

/// Окно компьютера: «Главная» (материя, здания, оператор и его линки) — на одной странице; «Очереди» — сверху что в баках
/// и репликаторы сети (клик — его задания ниже, двойной клик — перейти к нему), снизу в рамке задания выбранного — как во вкладке репликатора.
public class Dialog_Computer : Window
{
    enum Tab { Home, Queues }

    readonly Building_RMComputer computer;
    readonly RowList bills = new();
    Tab tab;
    CompPrinter sel;
    Vector2 homeScroll;

    /// По содержимому (2026-10-20: компактность): две карточки «Главной» рядом без наложения столбцов, оператор и очереди под ними.
    public override Vector2 InitialSize => RMUI.Fit(900f, 660f);

    public Dialog_Computer(Building_RMComputer computer)
    {
        this.computer = computer;
        doCloseX = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = true;
    }

    PowerNet Net => computer.Net;

    /// Репликаторы, потом для животных, потом инкубаторы.
    List<CompPrinter> Printers => CompPrinter.Of(Net);

    public override void DoWindowContents(Rect inRect)
    {
        if (!computer.Spawned)
        {
            Close();
            return;
        }
        // что в баках — справа в строке заголовка, правым краем по краю окна (2026-10-20), на обеих вкладках
        float tanksW = RMUI.TanksTitle(new Rect(inRect.x, inRect.y, inRect.width, 36f), Net);
        Text.Font = GameFont.Medium;
        RMUI.Label(new Rect(inRect.x, inRect.y, inRect.width - 40f - tanksW, 36f), computer.LabelCap);
        Text.Font = GameFont.Small;
        var tabs = new List<TabRecord>
        {
            new("RM_TabHome".Translate(), () => tab = Tab.Home, tab == Tab.Home),
            new("RM_TabQueues".Translate(), () => tab = Tab.Queues, tab == Tab.Queues),
        };
        Rect body = new(inRect.x, inRect.y + 76f, inRect.width, inRect.height - 76f);
        Widgets.DrawMenuSection(body);
        TabDrawer.DrawTabs(body, tabs);
        body = body.ContractedBy(14f);
        // одна справка на окно, одна и та же на обеих вкладках — справа в строке вкладок, под баками (2026-10-20); блоками по частям окна
        RMUI.HelpButton(new Rect(inRect.x, inRect.y + 44f, inRect.width, TabDrawer.TabHeight), new List<(string, string)>
        {
            ("RM_CardMatter".Translate(), "RM_TanksExplain".Translate()),
            ("RM_CardGear".Translate(), "RM_GearExplain".Translate()),
            ("RM_CardOperator".Translate(), "RM_SpeedExplain".Translate()),
            ("RM_TabQueues".Translate(), "RM_QueuesHelp".Translate()),
        });
        if (tab == Tab.Home) DrawHome(body);
        else DrawQueues(body);
    }

    // ---------------- Главная ----------------

    /// Сверху два блока одной высоты: «Материя» и «Оборудование» (по содержимому); ниже во всю ширину — оператор и его линки. Справка — одна, в строке вкладок.
    /// Прокрутка — только если не влезло.
    void DrawHome(Rect r)
    {
        // оператор — в заголовке карточки: «Оператор: Beryl»
        Pawn who = computer.Operator;
        string matterT = "RM_CardMatter".Translate(), gearT = "RM_CardGear".Translate(),
            opT = who != null ? RMUI.Tr("RM_OperatorName", who.LabelShort) : "RM_CardOperator".Translate().ToString();
        List<Line> matter = MatterLines(), gear = GearLines();
        float Measure(string title, List<Line> lines, float w) => RMUI.Card(new Rect(0f, 0f, w, 0f), title, lines, false);
        // «Оборудование» — по содержимому, «Материя» — всё остальное (2026-10-20)
        float GearW(float w) => Mathf.Min(RMUI.CardW(gearT, gear), w - RMUI.Gap - RMUI.CardW(matterT, matter));
        float Top(float w) => Mathf.Max(Measure(matterT, matter, w - RMUI.Gap - GearW(w)), Measure(gearT, gear, GearW(w)));
        float full = Top(r.width) + Measure(opT, OperatorLines(r.width), r.width) + RMUI.Gap > r.height ? r.width - 16f : r.width, gw = GearW(full);
        List<Line> op = OperatorLines(full);
        float top = Top(full), oh = Measure(opT, op, full);
        Rect view = new(0f, 0f, full, top + oh + RMUI.Gap);
        Widgets.BeginScrollView(r, ref homeScroll, view);
        RMUI.Card(new Rect(0f, 0f, full - RMUI.Gap - gw, top), matterT, matter, true);
        RMUI.Card(new Rect(full - gw, 0f, gw, top), gearT, gear, true);
        RMUI.Card(new Rect(0f, top + RMUI.Gap, full, oh), opT, op, true);
        Widgets.EndScrollView();
    }

    /// Накоплено в баках и собирать ли стоимость; сведение о стоимости — жёлтым (красный — опасность и ошибки).
    List<Line> MatterLines()
    {
        float mass = Tanks.Mass(Net), cap = Tanks.Capacity(Net);
        return new List<Line>
        {
            new() { left = "RM_Value".Translate(), right = Fmt.Silver(Tanks.Value(Net)), bar = 1f },
            new() { left = "RM_Mass".Translate(), right = Fmt.MassNum(mass) + " / " + Fmt.Mass(cap), bar = cap > 0f ? mass / cap : 0f },
            new() { sep = true, h = 16f },
            new()
            {
                check = r =>
                {
                    bool on = computer.collectValue;
                    Widgets.CheckboxLabeled(r, "RM_CollectValue".Translate(), ref on);
                    if (on != computer.collectValue) Edit.CollectValue(computer, on);
                },
            },
            new() { wide = true, color = RMUI.Yellow, left = "RM_CollectValueWarn".Translate() },
        };
    }

    /// Здания реплимата в сети компьютера; у баков — сколько в них материи; без питания — красной строкой ниже.
    List<Line> GearLines()
    {
        var lines = new List<Line> { new() { color = RMUI.Muted, left = "RM_ColBuilding".Translate(), mid = "RM_Mass".Translate(), right = "RM_Value".Translate() } };
        foreach (ThingDef d in new[] { RMDefOf.RM_Computer, RMDefOf.RM_Tank, RMDefOf.RM_TankLarge, RMDefOf.RM_Hopper, RMDefOf.RM_Replicator, RMDefOf.RM_ReplicatorWall, RMDefOf.RM_Feeder, RMDefOf.RM_Bioscanner, RMDefOf.RM_Incubator })
        {
            List<Building> list = Tanks.On<Building>(Net).Where(b => b.def == d).ToList();
            if (list.Count == 0) continue;
            List<Building_RMTank> tanks = list.OfType<Building_RMTank>().ToList();
            int off = list.Count(b => b.GetComp<CompPowerTrader>()?.PowerOn != true);
            lines.Add(new Line
            {
                left = d.LabelCap + " × " + list.Count,
                mid = tanks.Count > 0 ? Fmt.MassNum(tanks.Sum(t => t.mass)) + " / " + Fmt.MassNum(tanks.Sum(t => t.Capacity)) : null,
                right = tanks.Count > 0 ? Fmt.SilverNum(tanks.Sum(t => t.value)) : null,
            });
            if (off > 0) lines.Add(new Line { wide = true, color = ColorLibrary.RedReadable, left = (tanks.Count > 0 ? "RM_TanksOff" : "RM_Unpowered").Translate(off).CapitalizeFirst() });
        }
        if (Tanks.All(Net).Count == 0) lines.Add(new Line { wide = true, color = ColorLibrary.RedReadable, left = "RM_NoTanks".Translate() });
        return lines;
    }

    /// Таблица линков оператора (имя — в заголовке карточки): шкала прокачки (как масса), уровень из наибольшего, опыт, скорость печати
    /// (сейчас → на следующем уровне). Нет линка — строкой ниже, что без него не печатается.
    List<Line> OperatorLines(float cardW)
    {
        Pawn op = computer.Operator;
        if (op == null)
        {
            string why = computer.operatorPawn != null ? RMUI.Tr("RM_OperatorLost", computer.operatorPawn.LabelShort) + "\n" : "";
            return new List<Line> { new() { wide = true, color = ColorLibrary.RedReadable, left = why + "RM_NoOperatorHelp".Translate() } };
        }
        var rows = new List<string[]> { new[] { "RM_ColLink", "", "RM_ColLevel", "RM_ColXp", "RM_ColSpeed" }.Select(k => k.Length > 0 ? k.Translate().ToString() : "").ToArray() };
        var bars = new List<float> { -1f };
        var missing = new List<string>();
        foreach (Link l in Links.All)
        {
            Hediff_RMLink h = Links.Own(op, l);
            bool has = Links.Has(op, l);
            int pct = Links.Pct(Links.Speed(op, l));
            if (!has) missing.Add(RMUI.Tr("RM_LinkMissing", Links.Name(l), ("RM_LinkPrints" + l).Translate()));
            // шкала — прокачка от наименьшего уровня до наибольшего, как полоса массы; у мехлинка уровни — гестационные процессоры
            bars.Add(has ? Links.Progress(op, l) : -1f);
            rows.Add(new[]
            {
                Links.Name(l),
                "",
                has ? Links.Level(op, l) + " / " + Links.Top(l) : "—",
                h == null ? "—" : h.Max ? Fmt.Num(h.xp) : Fmt.Num(h.xp) + " / " + Fmt.Num(h.Next),
                !has ? "—" : Links.MaxLevel(op, l) ? pct + "%" : pct + "% → " + Links.Pct(Links.NextSpeed(op, l)) + "%",
            });
        }
        // столбцы: название, шкала (всё, что осталось), уровень, опыт, скорость; числа не обрезаются — столбец раздвигается
        float w = cardW - 28f;
        float[] cw = { 0f, 0f, 0f, 0f, 0f };
        foreach (string[] t in rows)
            for (int k = 0; k < cw.Length; k++) cw[k] = Mathf.Max(cw[k], Text.CalcSize(t[k]).x + 24f);
        cw[1] = Mathf.Max(0f, w - cw[0] - cw[2] - cw[3] - cw[4]);
        var lines = new List<Line>();
        for (int i = 0; i < rows.Count; i++)
        {
            string[] t = rows[i];
            float bar = bars[i];
            Color c = i == 0 ? RMUI.Muted : Color.white;
            lines.Add(new Line
            {
                h = 28f,
                check = r =>
                {
                    float x = r.x;
                    for (int k = 0; k < cw.Length; k++)
                    {
                        Rect cell = new(x, r.y, cw[k], r.height);
                        x += cw[k];
                        if (k == 1 && bar >= 0f) Widgets.FillableBar(new Rect(cell.x + 8f, cell.y + 6.5f, cell.width - 16f, 15f), Mathf.Clamp01(bar));
                        GUI.color = c;
                        Text.Anchor = k == 0 ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
                        RMUI.Label(cell, t[k]);
                    }
                    Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;
                },
            });
        }
        lines.AddRange(missing.Select(m => new Line { wide = true, color = RMUI.Muted, left = m }));
        return lines;
    }

    // ---------------- Очереди ----------------

    /// Сверху таблица репликаторов сети (только числа: масса и стоимость зелёные — баков хватает, красные — нет), снизу в рамке (другой фон,
    /// чтобы не сливалось с таблицей) задания выбранного — с тем же управлением, что во вкладке.
    void DrawQueues(Rect r)
    {
        List<CompPrinter> printers = Printers;
        if (printers.Count == 0)
        {
            GUI.color = RMUI.Muted;
            RMUI.Label(r, "RM_NoPrinters".Translate());
            GUI.color = Color.white;
            return;
        }
        if (sel == null || !printers.Contains(sel)) sel = printers[0];
        float ty = r.y, y = ty + TableRowH * (printers.Count + 1) + RMUI.Gap;
        Rect box = new(r.x, y, r.width, r.yMax - y);
        Widgets.DrawBoxSolidWithOutline(box, RMUI.CardFill, RMUI.Edge);
        Rect i = box.ContractedBy(14f);
        Text.Font = GameFont.Medium;
        RMUI.Label(new Rect(i.x, i.y, i.width, 34f), sel.Title.Truncate(i.width));
        Text.Font = GameFont.Small;
        ITab_RMBills.Bar(new Rect(i.x, i.y + 38f, i.width, 29f), sel);
        Rect list = new(i.x, i.y + 75f, i.width, i.height - 75f);
        if (sel.orders.Count > 0) ITab_RMBills.Orders(list, sel, bills);
        else
        {
            GUI.color = RMUI.Muted;
            RMUI.Label(list, "RM_QueueEmpty".Translate());
            GUI.color = Color.white;
        }
        PrinterTable(new Rect(r.x, ty, r.width, 0f), printers);
    }

    const float TableRowH = 28f;

    /// Строка на репликатор: клик выбирает его, двойной — переходит к нему (окно закрывается).
    void PrinterTable(Rect r, List<CompPrinter> printers)
    {
        const float rh = TableRowH;
        float haveMass = Tanks.Mass(Net), haveValue = Tanks.Value(Net);
        string[] head = new[] { "RM_ColPrinter", "RM_ColBills", "RM_SliderTime", "RM_Mass", "RM_Value" }.Select(k => k.Translate().ToString()).ToArray();
        // время — того, что может печататься; масса и стоимость — всей очереди, даже если баков не хватает
        static bool Matter((float ticks, float mass, float value) t) => t.mass > 0f || t.value > 0f;
        var cells = printers.Select(p => (p, t: p.Totals())).Select(x => (x.p, x.t, text: new[] { x.p.Title, x.p.orders.Count.ToString(),
            x.t.ticks > 0f ? Fmt.Time(x.t.ticks) : "—", Matter(x.t) ? Fmt.MassNum(x.t.mass) : "—", Matter(x.t) ? Fmt.SilverNum(x.t.value) : "—" })).ToList();
        float[] cw = { 0f, 90f, 120f, 120f, 140f };
        foreach (string[] t in cells.Select(x => x.text).Append(head))
            for (int k = 1; k < cw.Length; k++) cw[k] = Mathf.Max(cw[k], Text.CalcSize(t[k]).x + 16f);
        cw[0] = r.width - 8f - cw.Skip(1).Sum();
        void Cells(float y, string[] text, Color[] color)
        {
            float x = r.x + 4f;
            for (int k = 0; k < cw.Length; k++)
            {
                GUI.color = color[k];
                Text.Anchor = k == 0 ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
                RMUI.Label(new Rect(x, y, cw[k], rh), text[k].Truncate(cw[k]));
                x += cw[k];
            }
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }
        Color m = RMUI.Muted, wh = Color.white;
        Cells(r.y, head, new[] { m, m, m, m, m });
        float y = r.y + rh;
        for (int i = 0; i < cells.Count; i++, y += rh)
        {
            var (p, t, text) = cells[i];
            Rect row = new(r.x, y, r.width, rh);
            if (i % 2 == 0) Widgets.DrawAltRect(row);
            if (p == sel) Widgets.DrawHighlightSelected(row);
            Widgets.DrawHighlightIfMouseover(row);
            Color Need(bool lack) => !Matter(t) ? wh : lack ? ColorLibrary.RedReadable : ColorLibrary.Green;
            Cells(y, text, new[] { RMUI.Blue, wh, wh, Need(t.mass > haveMass), Need(t.value > haveValue) });
            Event e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0 || !Mouse.IsOver(row)) continue;
            e.Use();
            if (e.clickCount >= 2)
            {
                CameraJumper.TryJumpAndSelect(p.parent);
                Close();
            }
            else
            {
                sel = p;
                bills.Reset();
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }
        }
    }
}
