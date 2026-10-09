using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ReplimatMech;

/// Общее для окна компьютера и вкладки репликатора: блок «План», строка очереди в две линии, статусы.
public static class RMUI
{
    /// HelpGap — наименьший отступ перед справкой: справка пишется текстом внизу блока, всплывающих подсказок нет.
    /// Gap — между карточками и блоками.
    public const float RowH = 54f, HelpGap = 24f, Gap = 16f;
    /// TotalColor — цвет итогов: ванильный цвет заголовков разделов в подсказках.
    public static readonly Color TotalColor = ColoredText.TipSectionTitleColor, Blue = new(0.62f, 0.72f, 1f), Yellow = new(0.89f, 0.71f, 0.36f), Edge = new(1f, 1f, 1f, 0.15f),
        Muted = new(0.80f, 0.80f, 0.78f), PausedShade = new(0.12f, 0.13f, 0.15f, 0.6f);
    static readonly int[] gids = { -1, -1 };

    /// Подпись строкой: TaggedString игра подкрашивает всё, похожее на время, — «239 с|еребра» как «239 с».
    public static void Label(Rect r, string s) => Widgets.Label(r, s);

    /// Подстановка без ванильного Translate(args): тот делает заглавной букву после «:» — «Мало серебра: На штуку».
    public static string Tr(string key, params object[] args) => string.Format(key.Translate().RawText, args);

    /// Справка — приглушённым текстом у нижнего края area.
    public static void Help(Rect area, string s)
    {
        float h = Text.CalcHeight(s, area.width);
        GUI.color = Muted;
        Label(new Rect(area.x, area.yMax - h, area.width, h), s);
        GUI.color = Color.white;
    }

    public static string ModeLabel(OrderMode m) => (m switch
    {
        OrderMode.Make => BillRepeatModeDefOf.RepeatCount,
        OrderMode.Until => BillRepeatModeDefOf.TargetCount,
        _ => BillRepeatModeDefOf.Forever,
    }).label;

    /// Выбор режима — список, как у задания в ванили.
    public static FloatMenu ModeMenu(Action<OrderMode> pick) => new(((OrderMode[])Enum.GetValues(typeof(OrderMode)))
        .Select(m => new FloatMenuOption(ModeLabel(m).CapitalizeFirst(), () => pick(m))).ToList());

    public static string Group(bool important) => (important ? "RM_GroupImportant" : "RM_GroupNormal").Translate();

    public static void Icon(Rect r, Pattern p)
    {
        if (p.IsMech) Widgets.DefIcon(r, p.def);
        else Widgets.ThingIcon(r, p.Sample);
    }

    /// Сколько в баках после used — того, что раньше заберёт первая очередь.
    public static (float mass, float value) Have(PowerNet net, float usedMass = 0f, float usedValue = 0f) =>
        (Mathf.Max(0f, Tanks.Mass(net) - usedMass), Mathf.Max(0f, Tanks.Value(net) - usedValue));

    /// Чего не хватает в баках — стоимость, потом масса; пусто — хватает.
    public static string Lack((float ticks, float mass, float value) p, (float mass, float value) have) => string.Join(", ", new[]
    {
        p.value > have.value ? Tr("RM_LackValue", Fmt.Silver(p.value - have.value)) : null,
        p.mass > have.mass ? Tr("RM_LackMass", Fmt.Mass(p.mass - have.mass)) : null,
    }.Where(x => x != null));

    static bool Any((float ticks, float mass, float value) p) => p.ticks > 0f || p.mass > 0f || p.value > 0f;

    /// Хватает ли баков — коротко, цветом: «нечего печатать», «хватает», «не хватает 329 серебра».
    public static (string text, Color color) Verdict((float ticks, float mass, float value) p, PowerNet net, float usedMass = 0f, float usedValue = 0f)
    {
        string lack = Lack(p, Have(net, usedMass, usedValue));
        return !Any(p) ? (Tr("RM_PlanNothing"), Muted) : lack.Length > 0 ? (Tr("RM_Short", lack), ColorLibrary.RedReadable) : (Tr("RM_PlanEnough"), ColorLibrary.Green);
    }

    /// «План» задания, без рамки: время слева, масса по центру, стоимость справа (не влезла — строкой ниже), ниже по центру в скобках —
    /// хватает ли баков, а если нет — сколько не хватает (что в баках — строкой «Накоплено в баках» над шаблоном). Возвращает высоту.
    public static float Plan(Rect r, (float ticks, float mass, float value) p, PowerNet net)
    {
        var have = Have(net);
        string lack = Lack(p, have);
        Color red = ColorLibrary.RedReadable;
        var cells = new (string text, Color color, TextAnchor anchor)[]
        {
            (Tr("RM_TotTime", Any(p) ? Fmt.Time(p.ticks) : "—"), Color.white, TextAnchor.MiddleLeft),
            (Tr("RM_TotMass", Any(p) ? Fmt.Mass(p.mass) : "—"), p.mass > have.mass ? red : Color.white, TextAnchor.MiddleCenter),
            (Tr("RM_TotValue", Any(p) ? Fmt.Silver(p.value) : "—"), p.value > have.value ? red : Color.white, TextAnchor.MiddleRight),
            ("(" + (!Any(p) ? Tr("RM_PlanNothing") : lack.Length > 0 ? Tr("RM_Short", lack) : Tr("RM_PlanEnough")) + ")",
                !Any(p) ? Muted : lack.Length > 0 ? red : ColorLibrary.Green, TextAnchor.MiddleCenter),
        };
        Rect i = r.ContractedBy(0f, 8f);
        float W(int k) => Text.CalcSize(cells[k].text).x + 20f;
        // строки: 0 — время, масса, стоимость; стоимость не влезла справа от массы, что стоит по центру, — своя строка; последняя — вердикт, может в две
        bool one = W(0) <= i.width / 2f - W(1) / 2f && W(2) <= i.width / 2f - W(1) / 2f;
        int[] row = { 0, 0, one ? 0 : 1, one ? 1 : 2 };
        float vh = Mathf.Max(24f, Text.CalcHeight(cells[3].text, i.width));
        for (int k = 0; k < cells.Length; k++)
        {
            GUI.color = cells[k].color;
            Text.Anchor = cells[k].anchor;
            Label(new Rect(i.x, i.y + row[k] * 24f, i.width, k == 3 ? vh : 24f), cells[k].text);
        }
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
        return row[3] * 24f + vh + 16f;
    }

    /// Что накоплено в баках — строкой цветом итога, как «План»: подпись слева, масса по центру, стоимость справа.
    public static void TanksRow(Rect r, PowerNet net)
    {
        GUI.color = TotalColor;
        Text.Anchor = TextAnchor.MiddleLeft;
        Label(r, "RM_InTanks".Translate());
        Text.Anchor = TextAnchor.MiddleCenter;
        Label(r, Tr("RM_TotMass", Fmt.MassNum(Tanks.Mass(net)) + " / " + Fmt.Mass(Tanks.Capacity(net))));
        Text.Anchor = TextAnchor.MiddleRight;
        Label(r, Tr("RM_TotValue", Fmt.Silver(Tanks.Value(net))));
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
    }

    /// То же для узкой вкладки репликатора — двумя строками, подпись слева, число справа, линия под ними. Возвращает высоту.
    public static float TanksLines(Rect r, PowerNet net)
    {
        GUI.color = TotalColor;
        string[] nums = { Fmt.MassNum(Tanks.Mass(net)) + " / " + Fmt.Mass(Tanks.Capacity(net)), Fmt.Silver(Tanks.Value(net)) };
        string[] keys = { "RM_MassInTanks", "RM_ValueInTanks" };
        for (int i = 0; i < 2; i++)
        {
            Rect row = new(r.x, r.y + i * 26f, r.width, 26f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Label(row, keys[i].Translate());
            Text.Anchor = TextAnchor.MiddleRight;
            Label(row, nums[i]);
        }
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
        Widgets.DrawLineHorizontal(r.x, r.y + 52f, r.width, Widgets.SeparatorLineColor);
        return 52f + Gap;
    }

    /// Заголовок блока без рамки: название цветом блока и хватает ли баков, второй строкой — итог (нет итога и не влезло — вердикт второй строкой), под ним линия.
    /// used — что из баков раньше заберёт первая очередь; cols — ширины столбцов итога (NumsCols). Возвращает высоту; draw=false — только измерить.
    public static float Head(Rect r, string title, Color color, (float ticks, float mass, float value) p, PowerNet net, float usedMass, float usedValue, float[] cols, bool draw)
    {
        var (text, col) = Verdict(p, net, usedMass, usedValue);
        string verdict = " (" + text + ")";
        var nums = Nums(p);
        float tw = Text.CalcSize(title).x;
        bool below = nums == null && tw + Text.CalcSize(verdict).x > r.width;
        float h = nums != null || below ? 54f : 30f;
        if (!draw) return h + 1f;
        Text.Anchor = TextAnchor.MiddleLeft;
        GUI.color = color;
        Label(new Rect(r.x, r.y, tw + 4f, 30f), title);
        GUI.color = col;
        if (below) Label(new Rect(r.x, r.y + 24f, r.width, 30f), verdict.TrimStart());
        else Label(new Rect(r.x + tw, r.y, r.width - tw, 30f), verdict);
        GUI.color = Color.white;
        Text.Anchor = TextAnchor.UpperLeft;
        if (nums != null) IconNums(new Rect(r.x, r.y + 24f, r.width, 30f), nums, cols);
        Widgets.DrawLineHorizontal(r.x, r.y + h, r.width, Widgets.SeparatorLineColor);
        return h + 1f;
    }

    const float IconS = 20f, IconGap = 4f, NumsGap = 14f;

    /// Итог без слов «время, масса, стоимость»: время и масса — с единицей, стоимость — иконкой серебра после числа.
    static (Texture2D icon, string text)[] Nums((float ticks, float mass, float value) p) =>
        Any(p) ? new[] { ((Texture2D)null, Fmt.Time(p.ticks)), (null, Fmt.Mass(p.mass)), ((Texture2D)ThingDefOf.Silver.uiIcon, Fmt.SilverNum(p.value)) } : null;

    static float PartW((Texture2D icon, string text) x) => Text.CalcSize(x.text).x + (x.icon != null ? IconGap + IconS : 0f);

    /// Столбцы итога, общие для всех очередей: ширина — по самому широкому значению, большое число раздвигает столбец, не налезает.
    public static float[] NumsCols(IEnumerable<(float ticks, float mass, float value)> ps)
    {
        var all = ps.Select(Nums).Where(n => n != null).ToList();
        return Enumerable.Range(0, 3).Select(i => all.Count == 0 ? 0f : all.Max(n => PartW(n[i]))).ToArray();
    }

    static void IconNums(Rect r, (Texture2D icon, string text)[] parts, float[] cols)
    {
        float x = r.x;
        Text.Anchor = TextAnchor.MiddleLeft;
        for (int i = 0; i < parts.Length; i++)
        {
            var (icon, text) = parts[i];
            float w = Text.CalcSize(text).x;
            Label(new Rect(x, r.y, w + 1f, r.height), text);
            if (icon != null) GUI.DrawTexture(new Rect(x + w + IconGap, r.y + (r.height - IconS) / 2f, IconS, IconS), icon);
            x += cols[i] + NumsGap;
        }
        Text.Anchor = TextAnchor.UpperLeft;
    }

    /// Шапка очередей: полоса с фоном и линиями сверху и снизу; над названием (с nameX) — «Задание», над столбцами справа — их подписи по центру.
    public static float ColumnsHead(Rect r, float nameX, IEnumerable<(string key, float x, float w)> cols, bool draw)
    {
        if (!draw) return 28f;
        Rect band = new(r.x, r.y, r.width, 28f);
        Widgets.DrawHighlight(band);
        Widgets.DrawLineHorizontal(r.x, r.y, r.width, Widgets.SeparatorLineColor);
        Widgets.DrawLineHorizontal(r.x, band.yMax, r.width, Widgets.SeparatorLineColor);
        GUI.color = Muted;
        Text.Anchor = TextAnchor.MiddleLeft;
        Label(new Rect(r.x + nameX, r.y, 200f, 28f), "RM_ColBill".Translate());
        Text.Anchor = TextAnchor.MiddleCenter;
        foreach (var (key, x, w) in cols) Label(new Rect(r.x + x, r.y, w, 28f), key.Translate());
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
        return 28f;
    }

    /// Выбор из двух — две равные кнопки во всё поле, выбранная обведена синим. Возвращает true, если выбрана левая.
    public static bool Pair(Rect r, bool left, string a, string b)
    {
        float w = (r.width - 6f) / 2f;
        foreach (bool k in new[] { true, false })
        {
            Rect btn = new(k ? r.x : r.xMax - w, r.y, w, r.height);
            if (Widgets.ButtonText(btn, k ? a : b)) left = k;
            if (left != k) continue;
            GUI.color = Blue;
            Widgets.DrawBox(btn, 2);
            GUI.color = Color.white;
        }
        return left;
    }

    /// Как ванильный IntEntry (−10 −1 [поле] +1 +10, Shift ×10, Ctrl ×100), но крупный шаг — big: у вещей в стопку — стопка (−75 … +75).
    public static void CountEntry(Rect r, ref int value, ref string buf, int big)
    {
        int w = Mathf.Min(40, (int)r.width / 5);
        int[] steps = { -big, -1, 1, big };
        for (int k = 0; k < steps.Length; k++)
        {
            int s = steps[k];
            if (!Widgets.ButtonText(new Rect(k < 2 ? r.x + k * w : r.xMax - (4 - k) * w, r.y, w, r.height), (s > 0 ? "+" : "") + s)) continue;
            value += s * GenUI.CurrentAdjustmentMultiplier();
            buf = value.ToString();
            (s > 0 ? SoundDefOf.Checkbox_TurnedOn : SoundDefOf.Checkbox_TurnedOff).PlayOneShotOnCamera();
        }
        Widgets.TextFieldNumeric(new Rect(r.x + 2 * w, r.y, r.width - 4 * w, r.height), ref value, ref buf);
        if (buf.Length == 0) value = 0;
    }

    /// Статус строки целиком: что делает и почему ждёт.
    public static (string text, Color color) Status(CompPrinter c, Order o)
    {
        if (o.running)
            return (Tr("RM_StPrinting", Mathf.RoundToInt(100f * o.progress / Mathf.Max(1f, o.batchTicks)), Fmt.Time(o.progress), Fmt.Time(Mathf.Max(0f, o.batchTicks - o.progress))), Blue);
        return o.status switch
        {
            "RM_StSuspended" => ("SuspendedCaps".Translate(), Yellow),
            "RM_StLowMass" or "RM_StLowSilver" => (o.status.Translate().CapitalizeFirst(), ColorLibrary.RedReadable),
            "RM_StNoMechanitor" => ("RM_StNoMechanitorFull".Translate(), ColorLibrary.RedReadable),
            "RM_StDone" => (Tr("RM_StDoneFull", c.Have(o), o.target), ColorLibrary.Green),
            null => ("RM_StWaitFull".Translate(), RMUI.Muted),
            _ => ((o.status + "Full").Translate(), RMUI.Muted),
        };
    }

    public static string Count(CompPrinter c, Order o) => (o.mode == OrderMode.Forever ? o.done.ToString() : c.Have(o).ToString()) + " / " + (o.mode == OrderMode.Forever ? "RM_Always".Translate().ToString() : o.target.ToString());

    static bool Below(CompPrinter c, Order o) => o.mode != OrderMode.Forever && c.Have(o) < o.target;

    /// Очереди репликатора без рамок: у каждой — заголовок (название и в скобках хватает ли, справа числа) и линия, ниже строки. top — что сверху в той же прокрутке
    /// (таблица репликаторов, шапка столбцов), возвращает высоту, draw=false — только измерить. edit — окно компьютера: ручка перетаскивания, пустые группы
    /// (чтобы было куда тащить) и числа у очередей; без edit (узкая вкладка репликатора) — только название и хватает ли. reorder — можно таскать.
    /// right рисует правую часть строки шириной rightW.
    public static void Queue(Rect outRect, CompPrinter c, ref Vector2 scroll, Func<Order, bool> visible, bool edit, bool reorder, float rightW, Action<Rect, Order> right,
        Func<Rect, bool, float> top)
    {
        var groups = new[] { true, false }.Select(imp => (imp, list: c.orders.Where(o => o.important == imp && visible(o)).ToList()))
            .Where(g => edit || g.list.Count > 0).ToList();
        float width = outRect.width - 16f;
        // вторая очередь берёт из баков то, что останется после первой
        var before = c.Totals(true);
        float[] cols = NumsCols(groups.Select(g => c.Totals(g.imp)));
        float GroupHead(bool imp, float y, bool draw) =>
            Head(new Rect(0f, y, width, 0f), Group(imp), Blue, c.Totals(imp), c.Net, imp ? 0f : before.mass, imp ? 0f : before.value, cols, draw) + 4f;
        float Top(bool draw) => top(new Rect(0f, 0f, width, 0f), draw);
        float Rows(int n) => Mathf.Max(n, edit ? 1 : 0) * RowH;
        float content = Top(false) + groups.Sum(g => GroupHead(g.imp, 0f, false) + Rows(g.list.Count)) + Mathf.Max(0, groups.Count - 1) * Gap;
        Rect view = new(0f, 0f, width, Mathf.Max(outRect.height, content));
        // Ваниль не прокручивает список, пока строку тащат: у края (и за ним) прокручиваем сами, 10 строк в секунду.
        Vector2 m = Event.current.mousePosition;
        if (reorder && ReorderableWidget.Dragging && Event.current.type == EventType.Repaint && m.x >= outRect.x && m.x <= outRect.xMax)
            scroll.y = Mathf.Clamp(scroll.y + (m.y < outRect.y + RowH ? -1f : m.y > outRect.yMax - RowH ? 1f : 0f) * 10f * RowH * Time.unscaledDeltaTime,
                0f, view.height - outRect.height);
        Widgets.BeginScrollView(outRect, ref scroll, view);
        float y = Top(true);
        bool repaint = reorder && Event.current.type == EventType.Repaint;
        foreach (var (imp, list) in groups)
        {
            if (imp != groups[0].imp) y += Gap;
            y += GroupHead(imp, y, true);
            Rect area = new(0f, y, width, Rows(list.Count));
            Widgets.BeginGroup(area);
            // NewGroup отдаёт id только на Repaint; на клике нужен тот же id — иначе строка не находится и не тащится.
            int gi = imp ? 0 : 1;
            if (repaint) gids[gi] = ReorderableWidget.NewGroup((from, to) => Edit.Reorder(c, imp, from, imp, to), ReorderableDirection.Vertical, area.AtZero());
            if (list.Count == 0 && edit)
            {
                // Заглушка: в пустую группу ваниль не бросает при масштабе интерфейса не 100% — ищет группу по экранным координатам
                // без поправки на масштаб. С заглушкой бросок идёт к «ближайшей строке», а её саму таскать нечего — Move вернётся.
                if (reorder) ReorderableWidget.Reorderable(gids[gi], area.AtZero());
                GUI.color = Muted;
                RMUI.Label(area.AtZero().ContractedBy(8f, 10f), "RM_GroupEmpty".Translate());
                GUI.color = Color.white;
            }
            for (int i = 0; i < list.Count; i++)
            {
                Order o = list[i];
                Rect r = new(0f, i * RowH, area.width, RowH);
                if (reorder) ReorderableWidget.Reorderable(gids[gi], r);
                if (i % 2 == 1) Widgets.DrawLightHighlight(r);
                float x = r.x + 4f;
                if (edit)
                {
                    GUI.DrawTexture(new Rect(x, r.y + (RowH - 16f) / 2f, 16f, 16f), TexButton.DragHash);
                    x += 22f;
                }
                Rect row = new(x, r.y, r.xMax - rightW - 8f - x, RowH);
                Row(row, c, o);
                // На паузе строка блёклая, как задание в ванили; кнопки справа — как обычно.
                if (o.paused) Widgets.DrawBoxSolid(new Rect(r.x, r.y, row.xMax - r.x, RowH), PausedShade);
                right(new Rect(r.xMax - rightW, r.y, rightW, RowH), o);
            }
            Widgets.EndGroup();
            y += area.height;
        }
        if (repaint && groups.Count > 1)
        {
            int first = gids[0];
            ReorderableWidget.NewMultiGroup(new List<int> { gids[0], gids[1] }, (from, fromGroup, to, toGroup) => Edit.Reorder(c, fromGroup == first, from, toGroup == first, to));
        }
        Widgets.EndScrollView();
    }

    /// Строка в две линии: иконка, название и с отступом в скобках — кто последним правил задание; ниже статус; печатается — полоска прогресса.
    static void Row(Rect r, CompPrinter c, Order o)
    {
        Icon(new Rect(r.x, r.y + (r.height - 28f) / 2f, 28f, 28f), o.pattern);
        float x = r.x + 36f, w = r.xMax - x;
        string name = o.pattern.LabelWithQuality(o.Quality);
        float nw = Mathf.Min(Text.CalcSize(name).x + 4f, w);
        Label(new Rect(x, r.y + 4f, nw, 24f), name.Truncate(nw));
        string tail = ReplimatMechMod.S.needMechanitor && o.editor != null ? "    (" + o.editor.LabelShort + ")" : "";
        if (tail.Length > 0 && nw < w - 20f)
        {
            GUI.color = Muted;
            Label(new Rect(x + nw, r.y + 4f, w - nw, 24f), tail.Truncate(w - nw));
        }
        var (s, col) = Status(c, o);
        GUI.color = col;
        Label(new Rect(x, r.y + 26f, w, 24f), s.Truncate(w));
        GUI.color = Color.white;
        if (o.running) Widgets.FillableBar(new Rect(x, r.yMax - 4f, w, 3f), o.progress / Mathf.Max(1f, o.batchTicks));
    }

    /// «Есть / нужно» по центру своего столбца.
    public static void CountCell(Rect r, CompPrinter c, Order o)
    {
        GUI.color = o.paused ? Muted.ToTransparent(0.5f) : Below(c, o) ? Yellow : Color.white;
        Text.Anchor = TextAnchor.MiddleCenter;
        RMUI.Label(r, Count(c, o));
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
    }
}

/// Вкладка «Очередь» на самом репликаторе (всегда, и у пустого) — только просмотр: менять — в компьютере.
public class ITab_Queue : ITab
{
    Vector2 scroll;

    public ITab_Queue()
    {
        size = new Vector2(460f, 520f);
        labelKey = "RM_TabQueue";
    }

    /// Ширина — как у панели осмотра под вкладкой.
    protected override void UpdateSize() => size = new Vector2(InspectPaneUtility.PaneWidthFor(Find.WindowStack.WindowOfType<IInspectPane>()), size.y);

    /// Сверху — что накоплено в баках сети, ниже как «Очередь» компьютера: шапка столбцов, очереди (числа — второй строкой: вкладка узкая), строки;
    /// только просмотр.
    protected override void FillTab()
    {
        if (SelThing.TryGetComp<CompPrinter>() is not { } c) return;
        const float countW = 90f;
        Rect r = new Rect(0f, 0f, size.x, size.y).ContractedBy(10f);
        r.yMin += RMUI.TanksLines(new Rect(r.x, r.y, r.width - 16f, 0f), c.Net);  // правый край — по столбцу под ним (без полосы прокрутки)
        if (c.orders.Count == 0)
        {
            GUI.color = RMUI.Muted;
            RMUI.Label(r, "RM_QueueEmpty".Translate());
            GUI.color = Color.white;
            return;
        }
        RMUI.Queue(r, c, ref scroll, _ => true, false, false, countW, (cell, o) => RMUI.CountCell(cell, c, o),
            (t, draw) => RMUI.ColumnsHead(t, 40f, new[] { ("RM_ColHave", t.width - countW, countW) }, draw));
    }
}

/// Окно компьютера: «Главная» (всегда первая), «Шаблоны» (дерево категорий ванили, справа одно задание),
/// «Очередь» (вкладка на каждый репликатор, правка прямо в строке — как в Replimat Universal).
[StaticConstructorOnStartup]
public class Dialog_Computer : Window
{
    enum Tab { Home, Patterns, Queue }

    /// Строка карточки «Главной»: подпись слева, две колонки значений справа; wide — текст на всю ширину с переносом;
    /// bar — полоса между подписью и значением (у стоимости без предела — полная); sep — черта; пустая строка — отступ;
    /// check — строка целиком рисует сама (галка с подписью).
    sealed class Line
    {
        public string left = "", mid, right;
        public Action<Rect> check;
        public Color color = Color.white;
        public bool wide, sep;
        public float bar = -1f;
    }

    sealed class Node
    {
        public string key, label;
        public int order, count;
        public readonly List<Node> kids = new();
        public readonly List<Pattern> items = new();
    }

    readonly Building_RMComputer computer;
    readonly Pawn pawn;
    Tab tab;
    Vector2 homeScroll, treeScroll, queueScroll;
    readonly QuickSearchWidget patternSearch = new(), queueSearch = new();
    readonly HashSet<string> toggled = new();
    Node tree;
    int treeCount = -1;
    string treeFilter, selCat;
    Vector2 tileScroll;
    Pattern sel;
    CompPrinter selPrinter, queuePrinter;
    Order existing, draft;
    string qtyBuf;

    static Settings S => ReplimatMechMod.S;
    static string SliderKey(Order o, int i) => i == 0 ? "RM_SliderTime" : o.MassSlider ? "RM_SliderMass" : "RM_SliderValue";
    const float LabelW = 140f, ControlsW = 672f;
    static readonly Texture2D Gear = ContentFinder<Texture2D>.Get("UI/Icons/Options/OptionsGeneral");

    /// Ваниль окно под экран не ужимает — не выше экрана.
    public override Vector2 InitialSize => new(1080f, Mathf.Min(840f, UI.screenHeight));

    public Dialog_Computer(Building_RMComputer computer, Pawn pawn)
    {
        this.computer = computer;
        this.pawn = pawn;
        forcePause = true;
        doCloseX = true;
        doCloseButton = false;
        absorbInputAroundWindow = true;
    }

    PowerNet Net => computer.Net;

    List<CompPrinter> Printers => CompPrinter.All.Where(c => c.Net != null && c.Net == Net).OrderBy(c => c.Feeder).ThenBy(c => c.RenamableLabel).ToList();

    public override void DoWindowContents(Rect inRect)
    {
        if (!computer.Spawned)
        {
            Close();
            return;
        }
        Text.Font = GameFont.Medium;
        RMUI.Label(new Rect(inRect.x, inRect.y, inRect.width - 40f, 36f), "RM_ComputerTitle".Translate(S.needMechanitor ? "RM_MechanitorOption".Translate(pawn.LabelShort, Mathf.RoundToInt(Pricing.Speed(pawn) * 100f)) : pawn.LabelShort));
        Text.Font = GameFont.Small;
        var tabs = new List<TabRecord>
        {
            new("RM_TabHome".Translate(), () => tab = Tab.Home, tab == Tab.Home),
            new("RM_TabPatternsN".Translate(GameComponent_RM.Get.patterns.Count), () => tab = Tab.Patterns, tab == Tab.Patterns),
            new("RM_TabQueueN".Translate(Printers.Sum(c => c.orders.Count)), () => tab = Tab.Queue, tab == Tab.Queue),
        };
        Rect body = new(inRect.x, inRect.y + 76f, inRect.width, inRect.height - 76f);
        Widgets.DrawMenuSection(body);
        TabDrawer.DrawTabs(body, tabs);
        body = body.ContractedBy(14f);
        switch (tab)
        {
            case Tab.Home: DrawHome(body); break;
            case Tab.Patterns: DrawPatterns(body); break;
            default: DrawQueue(body); break;
        }
    }

    // ---------------- Главная ----------------

    /// Сверху два блока одной высоты: слева «Материя» (накоплено и собирать ли стоимость), справа «Оборудование» (у баков — сколько в них материи);
    /// ниже репликаторы по два в ряд; в самом низу — общая справка во всю ширину (нужна в начале игры, дальше не мешает).
    void DrawHome(Rect r)
    {
        float full = r.width - 16f, colW = (full - RMUI.Gap) / 2f, x2 = colW + RMUI.Gap;
        string matterT = "RM_CardMatter".Translate(), gearT = "RM_CardGear".Translate(), helpT = "RM_CardHelp".Translate();
        string help = string.Join("\n", new[] { "RM_TanksExplain", S.needMechanitor ? "RM_SpeedExplain" : null, "RM_GearExplain" }.Where(k => k != null).Select(k => k.Translate().ToString()));
        List<Line> matter = MatterLines(), gear = GearLines(), helpLines = new() { new Line { wide = true, color = RMUI.Muted, left = help } };
        float Measure(string title, List<Line> lines, float w = 0f) => Card(new Rect(0f, 0f, w > 0f ? w : colW, 0f), title, lines, false);
        float top = Mathf.Max(Measure(matterT, matter), Measure(gearT, gear)), hh = Measure(helpT, helpLines, full);
        var cards = Printers.Select(c => (title: Title(c), lines: PrinterLines(c), head: (Action<Rect>)(b =>
        {
            if (!Widgets.ButtonText(b, "RM_TabQueue".Translate())) return;
            queuePrinter = c;
            tab = Tab.Queue;
        }))).ToList();
        if (cards.Count == 0) cards.Add(("RM_CardPrinters".Translate(), new List<Line> { new() { wide = true, color = RMUI.Muted, left = "RM_NoPrinters".Translate() } }, null));
        // ряд по две карточки, высота ряда — по большей
        var rows = Enumerable.Range(0, (cards.Count + 1) / 2)
            .Select(i => cards.Skip(2 * i).Take(2).Max(k => Measure(k.title, k.lines))).ToList();
        float helpY = top + rows.Sum(h => RMUI.Gap + h) + RMUI.Gap;
        Rect view = new(0f, 0f, full, helpY + hh);
        Widgets.BeginScrollView(r, ref homeScroll, view);
        Card(new Rect(0f, 0f, colW, top), matterT, matter, true);
        Card(new Rect(x2, 0f, colW, top), gearT, gear, true);
        float y = top + RMUI.Gap;
        for (int i = 0; i < cards.Count; i++)
        {
            Card(new Rect(i % 2 == 0 ? 0f : x2, y, colW, rows[i / 2]), cards[i].title, cards[i].lines, true, cards[i].head);
            if (i % 2 == 1) y += rows[i / 2] + RMUI.Gap;
        }
        Card(new Rect(0f, helpY, full, hh), helpT, helpLines, true);
        Widgets.EndScrollView();
    }

    /// «Репликатор — Кухня»; имя по умолчанию («Репликатор 1») — как есть.
    static string Title(CompPrinter c) => c.RenamableLabel.StartsWith(c.BaseLabel) ? c.RenamableLabel : c.BaseLabel + " — " + c.RenamableLabel;

    /// Накоплено в баках и собирать ли стоимость; сведение о стоимости — жёлтым (красный — опасность и ошибки).
    List<Line> MatterLines()
    {
        float mass = Tanks.Mass(Net), cap = Tanks.Capacity(Net);
        return new List<Line>
        {
            new() { left = "RM_Value".Translate(), right = Fmt.Silver(Tanks.Value(Net)), bar = 1f },
            new(),
            new() { left = "RM_Mass".Translate(), right = Fmt.MassNum(mass) + " / " + Fmt.Mass(cap), bar = cap > 0f ? mass / cap : 0f },
            new() { sep = true },
            new()
            {
                check = r =>
                {
                    bool on = computer.collectValue;
                    Widgets.CheckboxLabeled(r, "RM_CollectValue".Translate(), ref on);
                    if (on != computer.collectValue) Edit.CollectValue(computer, on);
                },
            },
            new() { wide = true, color = RMUI.Muted, left = "RM_CollectValueDesc".Translate() },
            new() { wide = true, color = RMUI.Yellow, left = "\n" + "RM_CollectValueWarn".Translate() },
        };
    }

    /// Здания реплимата в сети компьютера; у баков — сколько в них материи; без питания — красной строкой ниже.
    List<Line> GearLines()
    {
        var lines = new List<Line> { new() { color = RMUI.Muted, left = "RM_ColBuilding".Translate(), mid = "RM_Mass".Translate(), right = "RM_Value".Translate() } };
        foreach (ThingDef d in new[] { RMDefOf.RM_Computer, RMDefOf.RM_Replicator, RMDefOf.RM_ReplicatorWall, RMDefOf.RM_Feeder, RMDefOf.RM_Hopper, RMDefOf.RM_Tank, RMDefOf.RM_TankLarge })
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

    static List<Line> PrinterLines(CompPrinter c)
    {
        var lines = new List<Line>();
        Order run = c.Running;
        if (run != null)
            lines.Add(new Line { color = RMUI.Blue, left = "RM_Now".Translate(run.BatchLabel, Pct(run)), bar = run.progress / Mathf.Max(1f, run.batchTicks) });
        else
            lines.Add(new Line { wide = true, color = c.CanWork ? RMUI.Muted : ColorLibrary.RedReadable, left = (c.CanWork ? "RM_NowIdle" : "RM_StOffFull").Translate() });
        var (t, _, v) = c.Totals();
        lines.Add(new Line { wide = true, color = RMUI.Muted, left = c.orders.Count == 0 ? "RM_QueueEmpty".Translate() : RMUI.Tr("RM_QueueSummary", c.orders.Count, Fmt.Time(t), Fmt.Silver(v)) });
        int low = c.orders.Count(o => o.status is "RM_StLowMass" or "RM_StLowSilver");
        if (low > 0) lines.Add(new Line { wide = true, color = ColorLibrary.RedReadable, left = "RM_QueueLow".Translate(low) });
        return lines;
    }

    static int Pct(Order o) => Mathf.RoundToInt(100f * o.progress / Mathf.Max(1f, o.batchTicks));

    /// Карточка: заголовок (справа — кнопка head), строки, справка у нижнего края; высота по содержимому, не меньше at.height; draw=false — только измерить.
    /// Строки в одну линию — текст по центру строки по высоте, черта — посередине своей строки: отступы над и под ней равны.
    static float Card(Rect at, string title, List<Line> lines, bool draw, Action<Rect> head = null)
    {
        const float pad = 14f;
        float w = at.width - 2f * pad;
        float LineH(Line l) => l.wide ? Text.CalcHeight(l.left, w) + 2f : 24f;
        float height = Mathf.Max(at.height, pad + 34f + lines.Sum(LineH) + pad);
        if (!draw) return height;
        Rect box = new(at.x, at.y, at.width, height);
        Widgets.DrawBoxSolidWithOutline(box, new Color(1f, 1f, 1f, 0.04f), RMUI.Edge);
        float x = at.x + pad, y = at.y + pad;
        Text.Font = GameFont.Medium;
        RMUI.Label(new Rect(x, y, head != null ? w - 130f : w, 34f), title.Truncate(head != null ? w - 130f : w));
        Text.Font = GameFont.Small;
        head?.Invoke(new Rect(x + w - 120f, y + 2f, 120f, 28f));
        y += 34f;
        // полосы и линии — одним столбцом посередине между самой длинной подписью и самым длинным значением, с отступом от обоих
        List<Line> bars = lines.Where(l => l.bar >= 0f).ToList();
        float a = x + bars.Select(l => Text.CalcSize(l.left).x).DefaultIfEmpty().Max() + 16f;
        float b = x + w - bars.Select(l => l.right != null ? Text.CalcSize(l.right).x : 0f).DefaultIfEmpty().Max() - 16f, bw = Mathf.Min(168f, b - a), bx = a + (b - a - bw) / 2f;
        foreach (Line l in lines)
        {
            float h = LineH(l);
            GUI.color = l.color;
            if (l.sep) Widgets.DrawLineHorizontal(x, y + h / 2f, w, RMUI.Edge);
            else if (l.check != null) l.check(new Rect(x, y, w, h));
            else if (l.wide) RMUI.Label(new Rect(x, y, w, h), l.left);
            else
            {
                Text.Anchor = TextAnchor.MiddleLeft;
                RMUI.Label(new Rect(x, y, l.mid == null && l.right == null ? w : w * 0.55f, h), l.left);
                Text.Anchor = TextAnchor.MiddleRight;
                if (l.mid != null) RMUI.Label(new Rect(x + w * 0.45f, y, w * 0.55f - 130f, h), l.mid);
                if (l.right != null) RMUI.Label(new Rect(x + w - 120f, y, 120f, h), l.right);
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                if (bw > 20f && l.bar >= 0f) Widgets.FillableBar(new Rect(bx, y + 4.5f, bw, 15f), Mathf.Clamp01(l.bar));
            }
            GUI.color = Color.white;
            y += h;
        }
        return height;
    }

    // ---------------- Шаблоны ----------------

    void DrawPatterns(Rect r)
    {
        if (GameComponent_RM.Get.patterns.Count == 0)
        {
            RMUI.Label(r, "RM_NoPatterns".Translate());
            return;
        }
        Rect left = new(r.x, r.y, 320f, r.height), right = new(r.x + 340f, r.y, r.width - 340f, r.height);
        // поиск кончается там, где числа дерева (за ними — полоса прокрутки)
        patternSearch.OnGUI(new Rect(left.x, left.y, left.width - 20f, 24f));
        Tree(new Rect(left.x, left.y + 30f, left.width, left.height - 30f));
        GUI.color = RMUI.Edge;
        Widgets.DrawLineVertical(left.xMax + 10f, r.y, r.height);
        GUI.color = Color.white;
        if (sel != null) Form(right);
        else if (ByKey(PatternTree, selCat) is { } cat) Tiles(right, cat);
        else
        {
            GUI.color = RMUI.Muted;
            RMUI.Label(right, "RM_PickPattern".Translate());
            GUI.color = Color.white;
        }
    }

    static Node ByKey(Node n, string key) => key == null ? null : n.key == key ? n : n.kids.Select(k => ByKey(k, key)).FirstOrDefault(k => k != null);

    static IEnumerable<Pattern> All(Node n) => n.items.Concat(n.kids.SelectMany(All));

    HashSet<Pattern> Queued => Printers.SelectMany(c => c.orders).Select(o => o.pattern).ToHashSet();

    static string InQueue => "RM_InQueueMark".Translate().CapitalizeFirst();

    /// Категория целиком — плитки всех шаблонов, по подкатегориям; клик по плитке — форма задания.
    void Tiles(Rect r, Node n)
    {
        Text.Font = GameFont.Medium;
        RMUI.Label(new Rect(r.x, r.y, r.width, 34f), n.label + " (" + n.count + ")");
        Text.Font = GameFont.Small;
        var sections = new List<(string title, List<Pattern> list)>();
        if (n.items.Count > 0) sections.Add((null, n.items.OrderBy(p => p.Label).ToList()));
        sections.AddRange(n.kids.OrderBy(k => k.order).Select(k => (k.label + " (" + k.count + ")", All(k).OrderBy(p => p.Label).ToList())));
        // плитки растянуты во всю ширину: зазор 8 и по горизонтали, и по вертикали, пустой полосы справа нет
        const float th = 144f, gap = 8f;
        Rect outRect = new(r.x, r.y + 40f, r.width, r.height - 40f);
        float width = outRect.width - 16f;
        int cols = Mathf.Max(1, (int)((width + gap) / (150f + gap)));
        float tw = (width + gap) / cols - gap;
        float Rows(int k) => (k + cols - 1) / cols * (th + gap);
        Rect view = new(0f, 0f, width, sections.Sum(x => (x.title != null ? 30f : 0f) + Rows(x.list.Count)));
        HashSet<Pattern> queued = Queued;
        float mass = Tanks.Mass(Net), value = Tanks.Value(Net);
        Widgets.BeginScrollView(outRect, ref tileScroll, view);
        float y = 0f;
        foreach (var (title, list) in sections)
        {
            if (title != null)
            {
                Widgets.ListSeparator(ref y, view.width, title);
                y += 5f;
            }
            for (int i = 0; i < list.Count; i++)
                Tile(new Rect(i % cols * (tw + gap), y + i / cols * (th + gap), tw, th), list[i], queued.Contains(list[i]), mass, value);
            y += Rows(list.Count);
        }
        Widgets.EndScrollView();
    }

    void Tile(Rect r, Pattern p, bool queued, float mass, float value)
    {
        // поля сверху и снизу равны: 10 + иконка 56 + 2 + три строки по 22 + 10
        Widgets.DrawOptionBackground(r, false);
        RMUI.Icon(new Rect(r.center.x - 28f, r.y + 10f, 56f, 56f), p);
        Text.Anchor = TextAnchor.MiddleCenter;
        RMUI.Label(new Rect(r.x + 4f, r.y + 68f, r.width - 8f, 22f), p.Label.Truncate(r.width - 8f));
        GUI.color = RMUI.Muted;
        RMUI.Label(new Rect(r.x + 4f, r.y + 90f, r.width - 8f, 22f), Rules.CanPrint(p) ? RMUI.Tr("RM_Enough", Enough(p, mass, value)) : "—");
        if (queued)
        {
            GUI.color = RMUI.Blue;
            RMUI.Label(new Rect(r.x + 4f, r.y + 112f, r.width - 8f, 22f), InQueue);
        }
        GUI.color = Color.white;
        Text.Anchor = TextAnchor.UpperLeft;
        if (Widgets.ButtonInvisible(r)) Load(p);
    }

    Node PatternTree
    {
        get
        {
            List<Pattern> all = GameComponent_RM.Get.patterns;
            if (tree != null && treeCount == all.Count && treeFilter == patternSearch.filter.Text) return tree;
            treeCount = all.Count;
            treeFilter = patternSearch.filter.Text;
            var root = new Node();
            var map = new Dictionary<string, Node>();
            foreach (Pattern p in all)
            {
                if (patternSearch.filter.Active && !patternSearch.filter.Matches(p.Label)) continue;
                Node n = root;
                foreach (var (key, label, order) in CategoryPath(p))
                {
                    if (!map.TryGetValue(key, out Node k))
                    {
                        map[key] = k = new Node { key = key, label = label, order = order };
                        n.kids.Add(k);
                    }
                    n = k;
                }
                n.items.Add(p);
            }
            Count(root);
            return tree = root;
        }
    }

    static int Count(Node n) => n.count = n.items.Count + n.kids.Sum(Count);

    /// Путь в дереве — категории склада ванили от корня; мехи — отдельной веткой.
    static IEnumerable<(string key, string label, int order)> CategoryPath(Pattern p)
    {
        if (p.IsMech)
        {
            yield return ("mechs", DefDatabase<MainButtonDef>.GetNamedSilentFail("Mechs")?.LabelCap ?? "RM_CatMechs".Translate(), 1000);
            yield break;
        }
        var path = new List<ThingCategoryDef>();
        for (ThingCategoryDef c = p.def.FirstThingCategory; c?.parent != null; c = c.parent) path.Insert(0, c);
        if (path.Count == 0)
        {
            yield return ("other", "RM_CatOther".Translate(), 1001);
            yield break;
        }
        foreach (ThingCategoryDef c in path) yield return (c.defName, c.LabelCap, c.parent.childCategories.IndexOf(c));
    }

    /// Верхний уровень раскрыт, глубже свёрнут; стрелка переключает; при поиске раскрыто всё.
    bool Open(Node n, int depth) => patternSearch.filter.Active || (depth == 0) != toggled.Contains(n.key);

    void Toggle(string key)
    {
        if (!patternSearch.filter.Active && !toggled.Remove(key)) toggled.Add(key);
    }

    void Flatten(Node n, int depth, List<(Node node, Pattern p, int depth)> rows)
    {
        foreach (Node k in n.kids.OrderBy(k => k.order))
        {
            rows.Add((k, null, depth));
            if (Open(k, depth)) Flatten(k, depth + 1, rows);
        }
        foreach (Pattern p in n.items.OrderBy(p => p.Label)) rows.Add((null, p, depth));
    }

    void Tree(Rect outRect)
    {
        var rows = new List<(Node node, Pattern p, int depth)>();
        Flatten(PatternTree, 0, rows);
        if (rows.Count == 0)
        {
            GUI.color = RMUI.Muted;
            RMUI.Label(outRect, "RM_NothingFound".Translate());
            GUI.color = Color.white;
            return;
        }
        const float h = 26f;
        HashSet<Pattern> queued = Queued;
        float mass = Tanks.Mass(Net), value = Tanks.Value(Net);
        Rect view = new(0f, 0f, outRect.width - 16f, rows.Count * h);
        Widgets.BeginScrollView(outRect, ref treeScroll, view);
        Text.Anchor = TextAnchor.MiddleLeft;
        for (int i = 0; i < rows.Count; i++)
        {
            var (node, p, depth) = rows[i];
            Rect r = new(depth * 14f, i * h, view.width - depth * 14f, h);
            if (node != null)
            {
                if (node.key == selCat && sel == null) Widgets.DrawHighlightSelected(r);
                else Widgets.DrawHighlightIfMouseover(r);
                bool open = Open(node, depth);
                GUI.DrawTexture(new Rect(r.x + 2f, r.y + 6f, 14f, 14f), open ? TexButton.Collapse : TexButton.Reveal);
                RMUI.Label(new Rect(r.x + 22f, r.y, r.width - 70f, h), node.label.Truncate(r.width - 70f));
                GUI.color = RMUI.Muted;
                Text.Anchor = TextAnchor.MiddleRight;
                RMUI.Label(new Rect(r.xMax - 50f, r.y, 46f, h), node.count.ToString());
                Text.Anchor = TextAnchor.MiddleLeft;
                GUI.color = Color.white;
                // стрелка — свернуть/раскрыть; строка — показать справа всё, что в категории
                if (Widgets.ButtonInvisible(new Rect(r.x, r.y, 20f, h))) Toggle(node.key);
                else if (Widgets.ButtonInvisible(r))
                {
                    selCat = node.key;
                    sel = null;
                    tileScroll = Vector2.zero;
                    if (!open) Toggle(node.key);
                }
                continue;
            }
            if (p == sel) Widgets.DrawHighlightSelected(r);
            else Widgets.DrawHighlightIfMouseover(r);
            RMUI.Icon(new Rect(r.x + 2f, r.y + 2f, 22f, 22f), p);
            float mw = queued.Contains(p) ? Text.CalcSize(InQueue).x + 8f : 0f, lw = r.width - 30f - 54f - mw;
            RMUI.Label(new Rect(r.x + 30f, r.y, lw, h), p.Label.Truncate(lw));
            Text.Anchor = TextAnchor.MiddleRight;
            if (mw > 0f)
            {
                GUI.color = RMUI.Blue;
                RMUI.Label(new Rect(r.xMax - 54f - mw, r.y, mw, h), InQueue);
            }
            GUI.color = RMUI.Muted;
            RMUI.Label(new Rect(r.xMax - 54f, r.y, 50f, h), Rules.CanPrint(p) ? Enough(p, mass, value) : "—");
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.MiddleLeft;
            if (Widgets.ButtonInvisible(r)) Load(p);
        }
        Text.Anchor = TextAnchor.UpperLeft;
        Widgets.EndScrollView();
    }

    /// «Хватит на N» — по тому, чего не хватит первым.
    static string Enough(Pattern p, float mass, float value)
    {
        QualityCategory q = p.Best > S.qualityCap ? S.qualityCap : p.Best;
        float m = Pricing.Mass(p), v = Pricing.Value(p, q);
        float n = Mathf.Min(m > 0f ? mass / m : float.MaxValue, v > 0f ? value / v : float.MaxValue);
        return n >= 99999f ? "∞" : Fmt.Num(Mathf.Floor(n));
    }

    /// Шаблон в форму: задание на этот репликатор уже есть — открывается оно, нет — новое.
    void Load(Pattern p, CompPrinter c = null, Order o = null)
    {
        if (o == null)
        {
            List<CompPrinter> ok = Printers.Where(x => x.Accepts(p)).ToList();
            if (c == null || !ok.Contains(c))
                c = ok.FirstOrDefault(x => x.orders.Any(y => y.pattern == p)) ?? ok.FirstOrDefault(x => !x.Feeder) ?? ok.FirstOrDefault();
            o = c?.orders.FirstOrDefault(y => y.pattern == p);
        }
        sel = p;
        selPrinter = c;
        existing = o;
        qtyBuf = null;
        draft = o?.Copy() ?? Default(p);
    }

    /// Задание по умолчанию: вторая очередь, «сделать X», лучшее качество, ползунки по нулям; правит тот, кто у консоли связи.
    Order Default(Pattern p) => new()
    {
        pattern = p, target = p.IsMech ? 1 : Mathf.Min(p.def.stackLimit, 75),
        mechanitor = p.IsMech && MechanitorUtility.IsMechanitor(pawn) ? pawn : null, editor = pawn,
    };

    void Form(Rect r)
    {
        // задание ушло или появилось (правка — в Multiplayer она приходит командой чуть позже, правка другого игрока) — форма показывает, что есть
        if (existing != null ? selPrinter == null || !selPrinter.orders.Contains(existing) : selPrinter?.orders.Any(y => y.pattern == sel) == true)
            Load(sel, selPrinter);
        Pattern p = sel;
        Order d = draft;
        CompPrinter c = selPrinter;
        int n = d.mode == OrderMode.Forever ? 1 : d.target;
        // сверху вниз: что накоплено в баках (линия под ним), шаблон, задание в очереди, «План» — сколько нужно и хватает ли, поля
        RMUI.TanksRow(new Rect(r.x, r.y, r.width, 30f), Net);
        Widgets.DrawLineHorizontal(r.x, r.y + 30f, r.width, Widgets.SeparatorLineColor);
        float y = r.y + 30f + RMUI.Gap;

        RMUI.Icon(new Rect(r.x, y, 40f, 40f), p);
        Text.Font = GameFont.Medium;
        RMUI.Label(new Rect(r.x + 52f, y - 4f, r.width - 52f, 32f), p.Label.Truncate(r.width - 52f));
        Text.Font = GameFont.Small;
        GUI.color = RMUI.Muted;
        RMUI.Label(new Rect(r.x + 52f, y + 28f, r.width - 52f, 24f), RMUI.Tr("RM_PerUnit", Fmt.Mass(d.UnitMass), Fmt.Silver(d.UnitValue), Fmt.Time(d.UnitTicks)));
        GUI.color = Color.white;
        y += 58f;

        if (existing != null)
        {
            // задание уже в очереди — одной строкой на зелёном фоне; его статус — на вкладке «Очередь»
            Rect box = new(r.x, y, r.width, 30f);
            Widgets.DrawBoxSolid(box, ColorLibrary.Green.ToTransparent(0.25f));
            Text.Anchor = TextAnchor.MiddleLeft;
            RMUI.Label(new Rect(box.x + 12f, box.y, box.width - 16f, box.height), "RM_InQueueEdit".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            y += box.height + 8f;
        }
        y += RMUI.Plan(new Rect(r.x, y, r.width, 0f), (n * d.UnitTicks, n * d.UnitMass, n * d.UnitValue), Net);

        Rect Field(string key)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            RMUI.Label(new Rect(r.x, y, LabelW, 30f), key.Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            Rect f = new(r.x + LabelW, y, Mathf.Min(320f, r.width - LabelW), 30f);
            y += 36f;
            return f;
        }
        // пояснение справа от поля, приглушённым текстом
        void Note(Rect f, string text)
        {
            GUI.color = RMUI.Muted;
            Text.Anchor = TextAnchor.MiddleLeft;
            RMUI.Label(new Rect(f.xMax + 12f, f.y, r.xMax - f.xMax - 12f, f.height), text);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }
        if (Widgets.ButtonText(Field("RM_EdPrinter"), c?.RenamableLabel ?? "RM_NoPrinters".Translate().ToString()))
            Find.WindowStack.Add(new FloatMenu(Printers.Where(x => x.Accepts(p))
                .Select(x => new FloatMenuOption(x.RenamableLabel + (x.orders.Any(o => o.pattern == p) ? " — " + "RM_InQueueMark".Translate() : ""), () => Load(p, x))).ToList()));
        if (!p.IsMech && Widgets.ButtonText(Field("RM_EdMode"), RMUI.ModeLabel(d.mode).CapitalizeFirst()))
            Find.WindowStack.Add(RMUI.ModeMenu(m => d.mode = m));
        if (d.mode != OrderMode.Forever)
        {
            Rect f = Field("RM_EdCount");
            RMUI.CountEntry(f, ref d.target, ref qtyBuf, d.Stack > 1 ? d.Stack : 10);
            // кнопки −10/−1 ванили уводят в минус — не меньше нуля (0 — задание на паузе) и не больше предела ванили, и в поле тоже
            int t = Mathf.Clamp(d.target, 0, Order.MaxTarget);
            if (t != d.target)
            {
                d.target = t;
                qtyBuf = t.ToString();
            }
            if (d.mode == OrderMode.Until && c != null) Note(f, "RM_InStock".Translate(c.Stock(d)));
        }
        if (p.HasQuality)
        {
            string ql = d.manualQuality ? d.Quality.GetLabel().CapitalizeFirst() : "RM_QualityBest".Translate(d.Quality.GetLabel()).ToString();
            if (Widgets.ButtonText(Field("RM_EdQuality"), ql))
            {
                var opts = new List<FloatMenuOption> { new("RM_QualityBest".Translate(p.Best.GetLabel()), () => d.manualQuality = false) };
                opts.AddRange(p.qualities.OrderByDescending(q => q).Select(q => new FloatMenuOption(q.GetLabel().CapitalizeFirst(), () =>
                {
                    d.manualQuality = true;
                    d.quality = q;
                })));
                Find.WindowStack.Add(new FloatMenu(opts));
            }
        }
        // механитор: у предмета — кто последним правил задание, у механоида — к кому он подключится, на выбор; в игре без механитора поля нет
        Rect mf = S.needMechanitor ? Field("RM_EdMechanitor") : default;
        string Name(Pawn m) => m?.LabelShort ?? "RM_None".Translate();
        if (S.needMechanitor && !p.IsMech)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            RMUI.Label(mf, Name(d.editor).Truncate(mf.width));
            Text.Anchor = TextAnchor.UpperLeft;
        }
        else if (S.needMechanitor && Widgets.ButtonText(mf, Name(d.mechanitor).Truncate(mf.width - 12f)))
        {
            List<FloatMenuOption> opts = Find.Maps.SelectMany(m => m.mapPawns.FreeColonists).Where(MechanitorUtility.IsMechanitor)
                .Select(m => new FloatMenuOption(m.LabelShort, () => d.mechanitor = m)).ToList();
            if (opts.Count == 0) opts.Add(new FloatMenuOption("RM_NoMechanitors".Translate(), null));
            Find.WindowStack.Add(new FloatMenu(opts));
        }
        y += 4f;
        for (int i = 0; i < 2; i++)
        {
            Slider(new Rect(r.x, y, r.width, 30f), d, i, n);
            y += 36f;
        }
        // очередь — две кнопки во всё поле, выбранная обведена; под ними — подсказка ползунка массы, по центру — «Изменения не применены»
        Rect g = Field("RM_EdGroup");
        d.important = RMUI.Pair(g, d.important, RMUI.Group(true), RMUI.Group(false));
        if (d.MassSlider)
        {
            string tip = "RM_SliderMassTip".Translate();
            float th = Text.CalcHeight(tip, r.width);
            GUI.color = RMUI.Muted;
            RMUI.Label(new Rect(r.x, y + 4f, r.width, th), tip);
            GUI.color = Color.white;
            y += th + 8f;
        }
        bool dirty = existing != null && !existing.SameSettings(d);
        if (dirty)
        {
            GUI.color = RMUI.Yellow;
            Text.Anchor = TextAnchor.MiddleCenter;
            RMUI.Label(new Rect(g.x, y + 2f, g.width, 24f), "RM_NotApplied".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }
        y += 32f;
        // под полями, с отступом: три кнопки — слева создать или применить, по центру сбросить к заданию по умолчанию, справа удалить
        // или отменить правку; справка — у нижнего края
        const float bw = 170f;
        Rect left = new(r.x, y + 8f, bw, 36f), mid = new(r.center.x - bw / 2f, left.y, bw, 36f), right = new(r.xMax - bw, left.y, bw, 36f);
        RMUI.Help(new Rect(r.x, left.yMax + RMUI.Gap, r.width, r.yMax - left.yMax - RMUI.Gap), "RM_GroupDesc".Translate());
        if (c == null)
        {
            GUI.color = ColorLibrary.RedReadable;
            RMUI.Label(new Rect(r.x, left.y + 6f, r.width, 24f), "RM_NoPrinters".Translate());
            GUI.color = Color.white;
            return;
        }
        if (existing != null)
        {
            if (Widgets.ButtonText(left, "RM_Apply".Translate(), active: dirty) && dirty)
            {
                d.editor = pawn;
                Edit.Apply(c, existing, d);
                Messages.Message("RM_MsgApplied".Translate(p.Label), MessageTypeDefOf.SilentInput, false);
            }
        }
        else if (Widgets.ButtonText(left, "RM_Create".Translate()))
        {
            Edit.Create(c, d);
            Messages.Message("RM_MsgAdded".Translate(p.Label, c.RenamableLabel, RMUI.Group(d.important)), MessageTypeDefOf.SilentInput, false);
        }
        Order def = Default(p);
        if (Widgets.ButtonText(mid, "ResetButton".Translate(), active: !d.SameSettings(def)))
        {
            draft = def;
            qtyBuf = null;
        }
        if (dirty)
        {
            if (Widgets.ButtonText(right, "CancelButton".Translate())) Load(p, c, existing);
        }
        else if (Widgets.ButtonText(right, "Delete".Translate(), active: existing != null) && existing != null)
        {
            Edit.Remove(c, existing);
            Messages.Message("RM_MsgDeleted".Translate(p.Label), MessageTypeDefOf.SilentInput, false);
        }
    }

    /// Два связанных ползунка (время, стоимость; у шаблона без стоимости — время, масса), шаг 5%, отметки через 10 без цифр. Вправо — параметра больше, влево — меньше; другой зеркально.
    /// Справа — что выйдет за n штук, как в «Плане» (время, стоимость или масса), в скобках — сколько ползунок сэкономил или добавил:
    /// зелёное — выгода, красное — плата за неё.
    static void Slider(Rect r, Order o, int i, int n)
    {
        int cap = Order.Cap, v = o.SliderValue(i);
        // полоса — там же, где поля (кнопки) над ней; рельс ванили — на 6 ниже верха полосы: по центру строки, как подпись и число
        Text.Anchor = TextAnchor.MiddleLeft;
        RMUI.Label(new Rect(r.x, r.y, LabelW, r.height), SliderKey(o, i).Translate());
        Rect bar = new(r.x + LabelW, r.y + r.height / 2f - 6f, Mathf.Min(320f, r.width - LabelW), 18f);
        int nv = -Mathf.RoundToInt(Widgets.HorizontalSlider(bar, -v, -cap, cap, roundTo: 5f));
        if (nv != v) o.SetSlider(i, nv);
        v = o.SliderValue(i);
        // в скобках у массы и стоимости — только число: единица та же; у времени — с единицей: минуты или часы
        Func<float, string> fmt = i == 0 ? Fmt.Time : o.MassSlider ? Fmt.Mass : Fmt.Silver, num = i == 0 ? Fmt.Time : o.MassSlider ? Fmt.MassNum : Fmt.SilverNum;
        float total = n * (i == 0 ? o.UnitTicks : o.MassSlider ? o.UnitMass : o.UnitValue), diff = total - total / o.K(i);
        GUI.color = n == 0 || v == 0 ? Color.white : v > 0 ? ColorLibrary.Green : ColorLibrary.RedReadable;
        Text.Anchor = TextAnchor.MiddleRight;
        RMUI.Label(new Rect(bar.xMax + 12f, r.y, r.xMax - bar.xMax - 12f, r.height), n == 0 ? "—" : fmt(total) + (v == 0 ? "" : " (" + (diff > 0f ? "+" : "−") + num(Mathf.Abs(diff)) + ")"));
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = RMUI.Muted;
        for (int t = -(cap / 10) * 10; t <= cap; t += 10)
            Widgets.DrawLineVertical(Mathf.Lerp(bar.x + 6f, bar.xMax - 6f, (t + cap) / (2f * cap)), bar.yMax, 4f);
        GUI.color = Color.white;
    }

    // ---------------- Очередь ----------------

    void DrawQueue(Rect r)
    {
        List<CompPrinter> printers = Printers;
        if (printers.Count == 0)
        {
            RMUI.Label(r, "RM_NoPrinters".Translate());
            return;
        }
        if (queuePrinter == null || !printers.Contains(queuePrinter)) queuePrinter = printers[0];
        // сверху поиск (кончается там, где название и статус строк, дальше — кнопки), справа от него красным — чего не хватает всем репликаторам;
        // под ним в одной прокрутке таблица репликаторов, шапка и очереди выбранного
        Rect search = new(r.x, r.y, r.width - 16f - ControlsW - 8f, 24f);
        queueSearch.OnGUI(search);
        string lack = RMUI.Lack(Total(printers), RMUI.Have(Net));
        if (lack.Length > 0)
        {
            GUI.color = ColorLibrary.RedReadable;
            Text.Anchor = TextAnchor.MiddleRight;
            RMUI.Label(new Rect(search.xMax + 8f, r.y, r.xMax - 16f - search.xMax - 8f, 24f), RMUI.Tr("RM_Short", lack).CapitalizeFirst());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }
        float y = r.y + 34f;
        CompPrinter c = queuePrinter;
        bool searching = queueSearch.filter.Active;
        RMUI.Queue(new Rect(r.x, y, r.width, r.yMax - y), c, ref queueScroll, o => !searching || queueSearch.filter.Matches(o.pattern.Label),
            true, !searching, ControlsW, (cell, o) => Controls(cell, c, o), (t, draw) =>
            {
                float h = PrinterTable(t, printers, draw) + RMUI.Gap;
                return h + QueueHead(new Rect(t.x, t.y + h, t.width, 0f), draw);
            });
    }

    /// Итог всех репликаторов: печатают одновременно — время, пока не допечатает самый занятый, масса и стоимость — сумма.
    static (float ticks, float mass, float value) Total(List<CompPrinter> printers)
    {
        var all = printers.Select(p => p.Totals()).ToList();
        return (all.Max(t => t.ticks), all.Sum(t => t.mass), all.Sum(t => t.value));
    }

    /// Таблица репликаторов — одна рамка на вкладку: итог всех и строка на каждый репликатор (через одну с фоном), клик выбирает его очереди; внизу — что в баках.
    /// В ячейках только числа: масса и стоимость зелёные — баков хватает, красные — нет. Числовой столбец — не уже заданного,
    /// длинное значение его раздвигает (место отдаёт название). Возвращает высоту; draw=false — только измерить.
    float PrinterTable(Rect r, List<CompPrinter> printers, bool draw)
    {
        const float rh = 28f;
        Rect box = new(r.x, r.y, r.width, rh * (printers.Count + 3) + 20f);
        if (!draw) return box.height;
        var rows = new List<(string name, int n, (float ticks, float mass, float value) t, CompPrinter p)>
            { ("RM_TotalAll".Translate(), printers.Sum(p => p.orders.Count), Total(printers), null) };
        rows.AddRange(printers.Select(p => (p.RenamableLabel, p.orders.Count, p.Totals(), p)));
        float haveMass = Tanks.Mass(Net), haveValue = Tanks.Value(Net);
        string[] head = new[] { "RM_EdPrinter", "RM_ColBills", "RM_SliderTime", "RM_Mass", "RM_Value" }.Select(k => k.Translate().ToString()).ToArray();
        var cells = rows.Select(x => x.t.ticks > 0f
            ? new[] { x.name, x.n.ToString(), Fmt.Time(x.t.ticks), Fmt.MassNum(x.t.mass), Fmt.SilverNum(x.t.value) }
            : new[] { x.name, x.n.ToString(), "—", "—", "—" }).ToList();
        string[] tanks = { "RM_InTanks".Translate(), "", "", Fmt.MassNum(haveMass) + " / " + Fmt.MassNum(Tanks.Capacity(Net)), Fmt.SilverNum(haveValue) };
        Widgets.DrawMenuSection(box);
        float x0 = box.x + 12f, w = box.width - 24f;
        float[] cw = { 0f, 70f, 110f, 130f, 150f };
        foreach (var t in cells.Append(head).Append(tanks))
            for (int k = 1; k < cw.Length; k++) cw[k] = Mathf.Max(cw[k], Text.CalcSize(t[k]).x + 16f);
        cw[0] = w - cw.Sum();
        void Cells(float y, string[] text, Color[] color)
        {
            float x = x0;
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
        float y = box.y + 8f;
        Color m = RMUI.Muted, wh = Color.white, gold = RMUI.TotalColor;
        Cells(y, head, new[] { m, m, m, m, m });
        for (int k = 0; k < rows.Count; k++)
        {
            var (name, n, t, p) = rows[k];
            y += rh;
            Rect row = new(box.x + 4f, y, box.width - 8f, rh);
            if (k % 2 == 1) Widgets.DrawLightHighlight(row);
            if (p == queuePrinter) Widgets.DrawHighlightSelected(row);
            if (p != null)
            {
                Widgets.DrawHighlightIfMouseover(row);
                if (Widgets.ButtonInvisible(row)) queuePrinter = p;
            }
            bool any = t.ticks > 0f;
            Color Need(bool lack) => !any ? wh : lack ? ColorLibrary.RedReadable : ColorLibrary.Green;
            Cells(y, cells[k],
                new[] { p == null ? gold : wh, wh, wh, Need(t.mass > haveMass), Need(t.value > haveValue) });
        }
        y += rh + 4f;
        Widgets.DrawLineHorizontal(x0, y, w, Widgets.SeparatorLineColor);
        Cells(y, tanks, new[] { gold, gold, gold, gold, gold });
        return box.height;
    }

    /// Столбцы кнопок строки: подпись в шапке, отступ от начала кнопок, ширина. Корзина — справа, без подписи.
    static readonly (string key, float x, float w)[] Cols =
        { ("RM_ColHave", 0f, 140f), ("RM_EdMode", 148f, 140f), ("RM_ColPause", 296f, 130f), ("RM_EdPrinter", 434f, 150f), ("RM_ColDetails", 592f, 80f) };

    /// Шапка очередей: над названием — «Задание», над кнопками — что они делают.
    static float QueueHead(Rect r, bool draw) => RMUI.ColumnsHead(r, 62f, Cols.Select(k => (k.key, r.width - ControlsW + k.x, k.w)), draw);

    /// Правка прямо в строке, как у задания в ванили: количество − / + на стопку (Shift ×10, Ctrl ×100; 0 — пауза; у «всегда» кнопок нет), режим,
    /// пауза, репликатор; в «Настройках» — шестерёнка (форма в «Шаблонах»: ползунки и точная настройка) и, с отступом от неё, корзина. Количество и режим — правка: скорость того, кто у консоли.
    void Controls(Rect r, CompPrinter c, Order o)
    {
        float y = r.y + (r.height - 28f) / 2f, x = r.x;
        Rect Col(int k) => new(x + Cols[k].x, y, Cols[k].w, 28f);
        int step = o.Stack * GenUI.CurrentAdjustmentMultiplier();
        bool count = o.mode != OrderMode.Forever;
        void Set(Action<Order> change)
        {
            Order d = o.Copy();
            change(d);
            d.editor = pawn;
            Edit.Apply(c, o, d);
        }
        if (count && Widgets.ButtonText(new Rect(x, y, 28f, 28f), "−")) Set(d => d.target -= step);
        RMUI.CountCell(count ? new Rect(x + 30f, y, 80f, 28f) : Col(0), c, o);
        if (count && Widgets.ButtonText(new Rect(x + 112f, y, 28f, 28f), "+")) Set(d => d.target += step);
        if (!o.pattern.IsMech && Widgets.ButtonText(Col(1), RMUI.ModeLabel(o.mode).CapitalizeFirst().Truncate(Cols[1].w - 12f)))
            Find.WindowStack.Add(RMUI.ModeMenu(m => Set(d => d.mode = m)));
        if (Widgets.ButtonText(Col(2), (o.paused ? "Suspended" : "NotSuspended").Translate())) Edit.Pause(c, o, !o.paused);
        if (Widgets.ButtonText(Col(3), c.RenamableLabel.Truncate(Cols[3].w - 10f)))
        {
            List<FloatMenuOption> opts = Printers.Where(p => p != c && p.Accepts(o.pattern)).Select(p => new FloatMenuOption(p.RenamableLabel, () =>
            {
                Edit.Move(c, o, p);
                Messages.Message("RM_MsgMoved".Translate(o.pattern.Label, p.RenamableLabel), MessageTypeDefOf.SilentInput, false);
            })).ToList();
            if (opts.Count == 0) opts.Add(new FloatMenuOption("RM_NoOtherPrinters".Translate(), null));
            Find.WindowStack.Add(new FloatMenu(opts));
        }
        if (Widgets.ButtonImage(new Rect(x + Cols[4].x + 4f, y + 2f, 24f, 24f), Gear))
        {
            Load(o.pattern, c, o);
            tab = Tab.Patterns;
        }
        if (Widgets.ButtonImage(new Rect(x + Cols[4].x + 52f, y + 2f, 24f, 24f), TexButton.Delete)) Edit.Remove(c, o);
    }
}
