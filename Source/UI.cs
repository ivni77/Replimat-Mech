using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ReplimatMech;

/// Общее для окон: блок «План», строка задания в две линии, статусы, карточки.
public static class RMUI
{
    /// Gap — между карточками и блоками.
    public const float RowH = 54f, Gap = 16f;
    /// TotalColor — цвет итогов: ванильный цвет заголовков разделов в подсказках.
    public static readonly Color TotalColor = ColoredText.TipSectionTitleColor, Blue = new(0.62f, 0.72f, 1f), Yellow = new(0.89f, 0.71f, 0.36f), Edge = new(1f, 1f, 1f, 0.15f),
        Muted = new(0.80f, 0.80f, 0.78f), CardFill = new(1f, 1f, 1f, 0.04f);

    /// Подпись строкой: TaggedString игра подкрашивает всё, похожее на время, — «239 с|еребра» как «239 с».
    public static void Label(Rect r, string s) => Widgets.Label(r, s);

    /// Свой значок навыка (Tools/textures.py: у SkillDef ванили значка нет); у навыка из другого мода — null.
    static readonly Dictionary<SkillDef, Texture2D> skillTex = new();

    public static Texture2D SkillTex(SkillDef s) =>
        skillTex.TryGetValue(s, out Texture2D t) ? t : skillTex[s] = ContentFinder<Texture2D>.Get("UI/Icons/Skills/" + s.defName, false);

    public static void SkillIcon(Rect r, SkillDef s)
    {
        if (SkillTex(s) is { } t) GUI.DrawTexture(r, t);
    }

    /// Название подсказкой над r — только где разрешено (значки карточки человека, шапка таблицы навыков).
    public static void Tip(Rect r, string key)
    {
        if (Mouse.IsOver(r)) TooltipHandler.TipRegion(r, key.Translate());
    }

    /// Подстановка без ванильного Translate(args): тот делает заглавной букву после «:» — «Мало серебра: На штуку».
    public static string Tr(string key, params object[] args) => string.Format(key.Translate().RawText, args);

    /// Окно по содержимому, не больше экрана (2026-10-20: компактность — важнее одного размера). Вверх до целого: ваниль
    /// округляет окно вниз, и содержимому не хватило бы доли пикселя.
    public static Vector2 Fit(float w, float h) => new(Mathf.Min(Mathf.Ceil(w), UI.screenWidth), Mathf.Min(Mathf.Ceil(h), UI.screenHeight));

    /// Есть вопрос (ключ) — сначала спросить, нет — сразу: правка начнёт заново начатую печать (2026-10-20) и т.п.
    public static void Confirm(string key, Action go, bool destructive = false)
    {
        if (key != null) Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(key.Translate(), go, destructive));
        else go();
    }

    /// Удаление задания — во вкладке и в «Добавить задание»; печать уже идёт — сначала спросить (2026-10-21): начатое вернётся в баки.
    public static void DeleteOrder(CompPrinter c, Order o) => Confirm(o.paid ? "RM_DeleteConfirm" : null, () =>
    {
        Edit.Remove(c, o);
        SoundDefOf.Click.PlayOneShotOnCamera();
    }, true);

    /// Кнопка «Справка» у правого края строки r — шапки блока (справка блока — только справа вверху, место — содержимому). Справка
    /// выпадает под кнопкой, как список ванили (2026-10-20), блоками с заголовками; клик мимо — закрыть. Справки нет — нет и кнопки.
    /// Возвращает занятую ширину с зазором — на столько короче заголовок.
    public static float HelpButton(Rect r, string text) => HelpButton(r, new List<(string, string)> { (null, text) });

    public static float HelpButton(Rect r, List<(string title, string text)> blocks)
    {
        blocks = blocks.Where(x => !x.text.NullOrEmpty()).ToList();
        if (blocks.Count == 0) return 0f;
        GameFont font = Text.Font;
        Text.Font = GameFont.Small;
        string label = "RM_CardHelp".Translate();
        float w = Text.CalcSize(label).x + 32f, h = Mathf.Min(30f, r.height);
        Rect b = new(r.xMax - w, r.y + (r.height - h) / 2f, w, h);
        if (Widgets.ButtonText(b, label)) Dialog_RMHelp.Toggle(blocks, b);
        Text.Font = font;
        return w + 12f;
    }

    /// Текст справки сверху вниз: каждое предложение (\n) — с новой строки без зазора, абзац (\n\n) — с небольшим (2026-10-18).
    public static void Help(Rect area, string s)
    {
        float y = area.y;
        foreach (var (line, gap) in HelpLines(s))
        {
            y += gap;
            float h = Text.CalcHeight(line, area.width);
            Label(new Rect(area.x, y, area.width, h), line);
            y += h;
        }
    }

    public static float HelpH(string s, float w) => HelpLines(s).Sum(l => l.gap + Text.CalcHeight(l.line, w));

    static IEnumerable<(string line, float gap)> HelpLines(string s)
    {
        float gap = 0f;
        foreach (string line in s.Split('\n'))
        {
            if (line.Length == 0)
            {
                gap = 10f;
                continue;
            }
            yield return (line, gap);
            gap = 0f;
        }
    }

    public const float TanksH = 48f;

    /// Что в баках — справа в строке заголовка окна, цветом итога, шрифтом Small (2026-10-18: во всех окнах реплимата, как в
    /// компьютере). Возвращает ширину с зазором — на столько короче заголовок слева.
    public static string TanksText(PowerNet net) => Tr("RM_TanksLine", Fmt.MassNum(Tanks.Mass(net)) + " / " + Fmt.Mass(Tanks.Capacity(net)), Fmt.Silver(Tanks.Value(net)));

    public static float TanksTitle(Rect r, PowerNet net)
    {
        GameFont font = Text.Font;
        Text.Font = GameFont.Small;
        string s = TanksText(net);
        GUI.color = TotalColor;
        Text.Anchor = TextAnchor.MiddleRight;
        Label(r, s);
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
        float w = Text.CalcSize(s).x + 16f;
        Text.Font = font;
        return w;
    }

    /// Что в баках сети — цветом итога, двумя строками: масса и стоимость (подпись слева, число справа — большие числа
    /// не налезают). Во вкладках репликатора и расщепителя; в окне компьютера — строкой в заголовке.
    public static void TanksBlock(Rect r, PowerNet net)
    {
        GUI.color = TotalColor;
        string[] values = { Fmt.MassNum(Tanks.Mass(net)) + " / " + Fmt.Mass(Tanks.Capacity(net)), Fmt.Silver(Tanks.Value(net)) };
        string[] labels = { "RM_Mass", "RM_Value" };
        for (int i = 0; i < 2; i++)
        {
            Rect row = new(r.x, r.y + 24f * i, r.width, 24f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Label(row, labels[i].Translate());
            Text.Anchor = TextAnchor.MiddleRight;
            Label(row, values[i]);
        }
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
    }

    /// Заголовок окна задания шрифтом Medium, за ним — значок «Переименовать», как у задания ванили. Возвращает правый край значка —
    /// за ним «Копировать» (2026-10-20). Значкам место — в r (r.width − 64 на название). Новое имя — в rename; null — только название
    /// (задание биорепликатора: переименовывают пешку, не задание).
    public static float TitleRename(Rect r, IRenameable x, Action<string> rename = null)
    {
        Text.Font = GameFont.Medium;
        string s = x.RenamableLabel.Truncate(r.width - 64f);
        Label(r, s);
        float tw = Text.CalcSize(s).x;
        Text.Font = GameFont.Small;
        if (rename == null) return r.x + tw;
        Rect ib = new(r.x + tw + 8f, r.y + 5f, 24f, 24f);
        if (Widgets.ButtonImage(ib, TexButton.Rename)) Find.WindowStack.Add(new Dialog_RMRename(x.RenamableLabel, rename));
        return ib.xMax;
    }

    /// Выбор из двух — две равные кнопки во всё поле, выбранная обведена; true — выбрана первая.
    public static bool Choice(Rect r, string a, string b, bool first)
    {
        float w = (r.width - 8f) / 2f;
        Rect ra = new(r.x, r.y, w, r.height), rb = new(r.xMax - w, r.y, w, r.height);
        if (Widgets.ButtonText(ra, a)) first = true;
        if (Widgets.ButtonText(rb, b)) first = false;
        Widgets.DrawBox(first ? ra : rb, 2);
        return first;
    }

    /// Кнопка ванили; неактивная — серая (ваниль рисует её как активную) и не нажимается.
    public static bool Button(Rect r, string label, bool active = true)
    {
        if (!active) GUI.color = Color.gray;
        bool clicked = Widgets.ButtonText(r, label, doMouseoverSound: active, active: active);
        GUI.color = Color.white;
        return clicked;
    }

    public static string ModeLabel(OrderMode m) => (m switch
    {
        OrderMode.Make => BillRepeatModeDefOf.RepeatCount,
        OrderMode.Until => BillRepeatModeDefOf.TargetCount,
        _ => BillRepeatModeDefOf.Forever,
    }).label;

    /// Выбор режима — список, как у задания в ванили; у воскрешения и людей «повторять до» нет.
    public static FloatMenu ModeMenu(Pattern p, Action<OrderMode> pick) => new(((OrderMode[])Enum.GetValues(typeof(OrderMode)))
        .Where(m => m != OrderMode.Until || p.CanUntil).Select(m => new FloatMenuOption(ModeLabel(m).CapitalizeFirst(), () => pick(m))).ToList());

    /// Режим задания расщепителя: «сделать X» или «бесконечно» (повторять до — у него нет).
    public static FloatMenu SplitModeMenu(Action<BillRepeatModeDef> pick) => new(new[] { BillRepeatModeDefOf.RepeatCount, BillRepeatModeDefOf.Forever }
        .Select(m => new FloatMenuOption(m.LabelCap, () => pick(m))).ToList());

    /// Иконка шаблона; воскрешение — все механоиды его класса, совсем маленькие, квадратом (2026-10-22: не путать с печатью).
    public static void Icon(Rect r, Pattern p)
    {
        if (p.resurrect)
        {
            List<ThingDef> m = Mechs(p);
            int per = Mathf.CeilToInt(Mathf.Sqrt(m.Count)), rows = Mathf.CeilToInt(m.Count / (float)per);
            float s = Mathf.Min(r.width / per, r.height / rows);
            MechGrid(r.center.x, r.center.y - rows * s / 2f, m, per, s, 0f);
        }
        else if (p.IsAnimal) Widgets.DefIcon(r, p.animal);
        else if (p.IsHuman) Bio.Face(r, p.human.Portrait);
        else if (p.IsMech) Widgets.DefIcon(r, p.def);
        else Widgets.ThingIcon(r, p.Sample);
    }

    /// Механоиды рецепта воскрешения ванили — все, кого воскрешает шаблон (лёгкий — не только чистильщик).
    public static List<ThingDef> Mechs(Pattern p) =>
        p.recipe.fixedIngredientFilter.AllowedThingDefs.Select(d => d.ingestible?.sourceDef).Where(d => d != null).OrderBy(d => d.label).ToList();

    /// Механоиды рядами по per от top, клетка s с зазором gap, ряды — по центру cx (неполный тоже).
    public static void MechGrid(float cx, float top, List<ThingDef> m, int per, float s, float gap)
    {
        for (int i = 0; i < m.Count; i++)
        {
            int inRow = Mathf.Min(per, m.Count - i / per * per);
            Widgets.DefIcon(new Rect(cx - (inRow * (s + gap) - gap) / 2f + i % per * (s + gap), top + i / per * (s + gap), s, s), m[i]);
        }
    }

    /// Сколько в баках, с оплаченной пачкой своего задания (paid).
    public static (float mass, float value) Have(PowerNet net, (float mass, float value) paid = default) =>
        (Tanks.Mass(net) + paid.mass, Tanks.Value(net) + paid.value);

    /// Чего не хватает в баках — стоимость, потом масса; пусто — хватает.
    public static string Lack((float ticks, float mass, float value) p, (float mass, float value) have) => string.Join(", ", new[]
    {
        p.value > have.value ? Tr("RM_LackValue", Fmt.Silver(p.value - have.value)) : null,
        p.mass > have.mass ? Tr("RM_LackMass", Fmt.Mass(p.mass - have.mass)) : null,
    }.Where(x => x != null));

    static bool Any((float ticks, float mass, float value) p) => p.ticks > 0f || p.mass > 0f || p.value > 0f;

    /// «План» задания — итог на фоне раздела (2026-10-18: важная информация): время слева, масса по центру, стоимость справа
    /// (не влезла — строкой ниже), ниже по центру в скобках — хватает ли баков, а если нет — сколько не хватает. Пачка, которую задание
    /// уже оплатило (paid), в плане есть — значит, и в доступном: баки + она (2026-10-21). Возвращает высоту; draw=false — только измерить.
    public static float Plan(Rect r, (float ticks, float mass, float value) p, PowerNet net, (float mass, float value) paid, bool draw = true)
    {
        var have = Have(net, paid);
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
        Rect i = r.ContractedBy(10f, 8f);
        // раскладка — по худшим числам, не по своим: рамка одной высоты, какой шаблон ни выбран (2026-10-20); свои длиннее — по своим
        string[][] worst = Worst;
        float W(int k) => worst[k].Append(cells[k].text).Max(x => Text.CalcSize(x).x) + 20f;
        // строки: 0 — время, масса, стоимость; стоимость не влезла справа от массы, что стоит по центру, — своя строка; последняя — вердикт, может в две
        bool one = W(0) <= i.width / 2f - W(1) / 2f && W(2) <= i.width / 2f - W(1) / 2f;
        int[] row = { 0, 0, one ? 0 : 1, one ? 1 : 2 };
        float vh = Mathf.Max(24f, worst[3].Append(cells[3].text).Max(x => Text.CalcHeight(x, i.width)));
        float total = row[3] * 24f + vh + 16f;
        if (!draw) return total;
        Widgets.DrawMenuSection(new Rect(r.x, r.y, r.width, total));
        for (int k = 0; k < cells.Length; k++)
        {
            GUI.color = cells[k].color;
            Text.Anchor = cells[k].anchor;
            Label(new Rect(i.x, i.y + row[k] * 24f, i.width, k == 3 ? vh : 24f), cells[k].text);
        }
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
        return total;
    }

    /// Худшие строки «Плана» по ячейкам: время — часы, дни, годы; масса и стоимость — шесть цифр; вердикт — не хватает и стоимости, и массы.
    static string[][] Worst => new[]
    {
        new[] { 59_000f, 3_594_000f, 35_940_000f }.Select(t => Tr("RM_TotTime", Fmt.Time(t))).ToArray(),
        new[] { Tr("RM_TotMass", Fmt.Mass(999_999f)) },
        new[] { Tr("RM_TotValue", Fmt.Silver(999_999f)) },
        new[] { "(" + Tr("RM_Short", Lack((0f, 999_999f, 999_999f), (0f, 0f))) + ")" },
    };

    /// Как ванильный IntEntry (−10 −1 [поле] +1 +10, Shift ×10, Ctrl ×100), но крупный шаг — big: у вещей в стопку — стопка (−75 … +75);
    /// big 0 — только −1 и +1 (уровень навыка в шаблоне человека).
    public static void CountEntry(Rect r, ref int value, ref string buf, int big)
    {
        int[] steps = big > 1 ? new[] { -big, -1, 1, big } : new[] { -1, 1 };
        int n = steps.Length / 2, w = Mathf.Min(40, (int)r.width / (2 * n + 1));
        for (int k = 0; k < steps.Length; k++)
        {
            int s = steps[k];
            if (!Widgets.ButtonText(new Rect(k < n ? r.x + k * w : r.xMax - (steps.Length - k) * w, r.y, w, r.height), (s > 0 ? "+" : "") + s)) continue;
            value += s * GenUI.CurrentAdjustmentMultiplier();
            buf = value.ToString();
            (s > 0 ? SoundDefOf.Checkbox_TurnedOn : SoundDefOf.Checkbox_TurnedOff).PlayOneShotOnCamera();
        }
        Widgets.TextFieldNumeric(new Rect(r.x + n * w, r.y, r.width - 2 * n * w, r.height), ref value, ref buf);
        if (buf.Length == 0) value = 0;
    }

    /// Статус задания целиком, как в ванили: что делает и почему ждёт. Ошибки — красным, сведения — жёлтым.
    public static (string text, Color color) Status(CompPrinter c, Order o)
    {
        Color red = ColorLibrary.RedReadable;
        if (o.running)
            return (Tr("RM_StPrinting", Mathf.RoundToInt(100f * o.progress / Mathf.Max(1f, o.batchTicks)), Fmt.Time(o.progress), Fmt.Time(o.batchTicks)), Blue);
        string lack = o.status is "RM_StLowMass" or "RM_StLowSilver" ? Lack((0f, o.UnitMass, o.UnitValue), Have(c.Net)) : "";
        return o.status switch
        {
            "RM_StSuspended" => ("SuspendedCaps".Translate(), Yellow),
            "RM_StLowMass" or "RM_StLowSilver" => (lack.Length > 0 ? Tr("RM_Short", lack).CapitalizeFirst() : o.status.Translate().CapitalizeFirst(), red),
            "RM_StDone" => (o.mode == OrderMode.Until ? Tr("RM_StDoneFull", c.Stock(o), o.target) : "RM_StDone".Translate().CapitalizeFirst().ToString(), ColorLibrary.Green),
            "RM_StMechWaits" or "RM_StNoCorpse" => ((o.status + "Full").Translate(), Yellow),
            "RM_StNoResearch" => (Tr("RM_StNoResearchFull", Rules.MissingResearch(o.pattern)?.LabelCap.ToString()), red),
            "RM_StNoLink" => (Tr("RM_StNoLinkFull", Links.Def(Links.Of(o.pattern)).label), red),
            // «печатается другое» — только когда что-то печатается: статус от прошлого Refresh, пачка уже готова
            null or "RM_StWait" when c.Running == null => ("RM_StWait".Translate().CapitalizeFirst(), Muted),
            null or "RM_StWait" or "RM_StNoRoom" => (((o.status ?? "RM_StWait") + "Full").Translate(), Muted),
            _ => ((o.status + "Full").Translate(), red),
        };
    }

    /// Счётчик, как у задания ванили (Bill_Production.RepeatInfoText): «сделать X» — сколько осталось (убывает до нуля), «повторять до X» — есть / нужно,
    /// «бесконечно» — «Бесконечно» (2026-10-23).
    public static string Count(CompPrinter c, Order o) => o.mode switch
    {
        OrderMode.Make => o.target + "x",
        OrderMode.Until => c.Stock(o) + " / " + o.target,
        _ => "Forever".Translate(),
    };

    static bool Below(CompPrinter c, Order o) => o.mode != OrderMode.Forever && c.Need(o) > 0;

    const float LineH = 24f, RowPad = 4f, Btn = 24f, BtnGap = 10f;

    /// Высота строки задания: имя (если есть), что делает, количество (если есть), подробности и статус (если есть), кнопки.
    public static float RowHeight(string title, bool count, string tail, bool status) =>
        ((title == null ? 0 : 1) + (count ? 3 : 2) + (tail.NullOrEmpty() ? 0 : 1) + (status ? 1 : 0)) * LineH + 2f * RowPad;

    /// Количество в строке задания: текст, − / + (step) и кнопка режима (mode, menu — список режимов).
    public record struct Counter(string text, Color color, Action<int> step, string mode, Action menu);

    /// Строка задания, как у верстака ванили (2026-10-23): первая линия (если игрок дал заданию имя) — имя; дальше иконка и что делает во всю
    /// ширину; под ним слева количество, справа − / + (Shift ×10, Ctrl ×100) и кнопка режима со списком — от правого края, как WidgetRow ванили,
    /// режим одной ширины во всех строках: ничего не сдвигается. Ниже во всю ширину — подробности (механитор, пол, излишки) приглушённо и статус;
    /// последняя линия — кнопки в ряд с левого края (под именем), удалить — последней (2026-10-23: промах мимо режима не попадает в корзину; act null — серая).
    /// На паузе — плашка «ПАУЗА» посередине, печатается — полоска прогресса внизу (progress < 0 — нет).
    public static void BillRow(Rect r, Action<Rect> icon, string title, string name, Counter? count, string status, Color statusColor,
        string tail, bool paused, float progress, params (Texture2D tex, Action act)[] buttons)
    {
        float y = r.y + RowPad, x = r.x + (icon != null ? 36f : 0f), w = r.xMax - 4f - x;
        void Line(string s)
        {
            Label(new Rect(x, y, w, LineH), s.Truncate(w));
            y += LineH;
        }
        Text.Anchor = TextAnchor.MiddleLeft;
        if (title != null) Line(title);
        icon?.Invoke(new Rect(r.x, y + LineH - 14f, 28f, 28f));
        Line(name);
        if (count is { } c)
        {
            // режим — одной ширины во всех строках, по самому длинному режиму ванили
            float mw = DefDatabase<BillRepeatModeDef>.AllDefs.Max(m => Text.CalcSize(m.LabelCap).x) + 16f, bx = x + w - mw - BtnGap - 2f * Btn - 4f;
            GUI.color = c.color;
            Label(new Rect(x, y, bx - x, LineH), c.text);
            GUI.color = Color.white;
            for (int k = 0; k < 2; k++)
                if (Widgets.ButtonImage(new Rect(bx + k * (Btn + 4f), y, Btn, Btn), k == 0 ? TexButton.Minus : TexButton.Plus))
                {
                    c.step((k == 0 ? -1 : 1) * GenUI.CurrentAdjustmentMultiplier());
                    SoundDefOf.DragSlider.PlayOneShotOnCamera();
                }
            if (Widgets.ButtonText(new Rect(x + w - mw, y, mw, LineH), c.mode)) c.menu();
            y += LineH;
        }
        if (!tail.NullOrEmpty())
        {
            GUI.color = Muted;
            Line(tail);
        }
        GUI.color = statusColor;
        if (!status.NullOrEmpty()) Line(status);
        GUI.color = Color.white;
        Text.Anchor = TextAnchor.UpperLeft;
        for (int k = 0; k < buttons.Length; k++)
            if (ITab_RMBills.Icon(new Rect(x + k * (Btn + BtnGap), r.yMax - RowPad - LineH, Btn, Btn), buttons[k].tex, buttons[k].act != null))
                buttons[k].act();
        if (paused) Suspended(new Rect(x, r.y, w, r.height));
        if (progress >= 0f) Widgets.FillableBar(new Rect(x, r.yMax - 4f, w, 3f), progress);
    }

    /// Задание на паузе — плашка «ПАУЗА» посередине, как в ванили.
    public static void Suspended(Rect r)
    {
        Text.Font = GameFont.Medium;
        Text.Anchor = TextAnchor.MiddleCenter;
        Rect plate = new(r.center.x - 70f, r.center.y - 20f, 140f, 40f);
        GUI.DrawTexture(plate, TexUI.GrayTextBG);
        Label(plate, "SuspendedCaps".Translate());
        Text.Anchor = TextAnchor.UpperLeft;
        Text.Font = GameFont.Small;
    }

    /// Подробности задания третьей линией: к кому механоид, пол животного или излишки в расщепитель.
    static string Tail(Order o) => o.pattern.IsMech && o.mechanitor != null ? Tr("RM_RowMech", o.mechanitor.LabelShort, o.group)
        : o.Gendered ? o.GenderLabel.CapitalizeFirst()
        : o.surplus && o.CanSurplus ? "RM_RowSurplus".Translate().CapitalizeFirst() : null;

    public static float OrderHeight(Order o) => RowHeight(o.Title, true, Tail(o), true);

    /// Задание репликатора — во вкладке и в «Очередях» компьютера: − / + у количества; копировать, пауза, имя (не у биорепликатора), окно задания,
    /// репликатор, удалить. «Репликатор» (значок здания) — список других репликаторов сети, что примут шаблон, выбор переносит задание туда
    /// (2026-10-23: вместо перетаскивания между ними); других в сети нет — кнопки нет, ни один не примет — серая.
    public static void OrderRow(Rect r, CompPrinter c, Order o)
    {
        var (s, col) = Status(c, o);
        var buttons = new List<(Texture2D, Action)>
        {
            (TexButton.Copy, () => ITab_RMBills.Copy(new[] { o })),
            (TexButton.Suspend, () =>
            {
                Edit.Pause(c, o, !o.paused);
                SoundDefOf.Click.PlayOneShotOnCamera();
            }),
        };
        if (o.Renamable) buttons.Add((TexButton.Rename, () => Find.WindowStack.Add(new Dialog_RMRename(o.RenamableLabel, n => Edit.Rename(c, o, n)))));
        buttons.Add((ITab_RMBills.Gear.Texture, () => Find.WindowStack.Add(new Dialog_Order(c, o))));
        List<CompPrinter> others = CompPrinter.Of(c.Net).Where(x => x != c).ToList(), fit = others.Where(x => x.Accepts(o.pattern)).ToList();
        if (others.Count > 0)
            buttons.Add((c.parent.def.uiIcon, fit.Count == 0 ? null : () => Find.WindowStack.Add(new FloatMenu(fit
                .Select(x => new FloatMenuOption(x.Title, () => Edit.Transfer(c, o, x), x.parent, Color.white)).ToList()))));
        buttons.Add((TexButton.Delete, () => DeleteOrder(c, o)));
        BillRow(r, ir => Icon(ir, o.pattern), o.Title, o.pattern.LabelWithQuality(o.Quality),
            new Counter(Count(c, o), o.paused ? Muted.ToTransparent(0.5f) : Below(c, o) ? Yellow : Color.white, n => Edit.Count(c, o, n),
                ModeLabel(o.mode).CapitalizeFirst(), () => Find.WindowStack.Add(ModeMenu(o.pattern, m => Edit.Mode(c, o, m)))),
            s, col, Tail(o), o.paused, o.running ? o.progress / Mathf.Max(1f, o.batchTicks) : -1f, buttons.ToArray());
    }

    /// Карточка, как на «Главной» и в настройках мода: заголовок (справа — кнопка head), строки; высота по содержимому, не меньше at.height; draw=false — только измерить.
    /// Строки в одну линию — текст по центру строки по высоте, черта — посередине своей строки: отступы над и под ней равны.
    /// Ширина карточки по содержимому: заголовок; строка — подпись, столбцы чисел (как раскладывает Card), у полосы — место хотя бы на короткую.
    /// Строки во всю ширину и галки переносятся — не в счёт.
    public static float CardW(string title, List<Line> lines)
    {
        List<Line> text = lines.Where(l => !l.sep && l.check == null && !l.wide).ToList();
        float W(string x) => x != null ? Text.CalcSize(x).x : 0f;
        float rw = text.Select(l => W(l.right)).DefaultIfEmpty().Max(), mw = text.Select(l => W(l.mid)).DefaultIfEmpty().Max();
        float row = text.Select(l => W(l.left) + 8f + (l.mid != null ? mw + 24f + rw : l.right != null ? rw : 0f) + (l.bar >= 0f ? 60f : 0f)).DefaultIfEmpty().Max();
        Text.Font = GameFont.Medium;
        float tw = W(title);
        Text.Font = GameFont.Small;
        return Mathf.Ceil(Mathf.Max(tw, row) + 2f) + 2f * 14f;
    }

    public static float Card(Rect at, string title, List<Line> lines, bool draw, Action<Rect> head = null)
    {
        const float pad = 14f;
        float w = at.width - 2f * pad;
        float LineH(Line l) => l.wide ? Text.CalcHeight(l.left, w) + 2f : l.h;
        float height = Mathf.Max(at.height, pad + 34f + lines.Sum(LineH) + pad);
        if (!draw) return height;
        Rect box = new(at.x, at.y, at.width, height);
        Widgets.DrawBoxSolidWithOutline(box, CardFill, Edge);
        float x = at.x + pad, y = at.y + pad;
        Text.Font = GameFont.Medium;
        RMUI.Label(new Rect(x, y, head != null ? w - 130f : w, 34f), title.Truncate(head != null ? w - 130f : w));
        Text.Font = GameFont.Small;
        head?.Invoke(new Rect(x + w - 120f, y + 2f, 120f, 28f));
        y += 34f;
        // столбцы чисел справа — по самому длинному значению, подпись — до них (не налезают друг на друга)
        List<Line> text = lines.Where(l => !l.sep && l.check == null && !l.wide).ToList();
        float rw = text.Select(l => l.right != null ? Text.CalcSize(l.right).x : 0f).DefaultIfEmpty().Max();
        float mw = text.Select(l => l.mid != null ? Text.CalcSize(l.mid).x : 0f).DefaultIfEmpty().Max(), mx = x + w - rw - 24f - mw;
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
                RMUI.Label(new Rect(x, y, (l.mid != null ? mx : l.right != null ? x + w - rw : x + w) - x - 8f, h), l.left);
                Text.Anchor = TextAnchor.MiddleRight;
                if (l.mid != null) RMUI.Label(new Rect(mx, y, mw, h), l.mid);
                if (l.right != null) RMUI.Label(new Rect(x + w - rw, y, rw, h), l.right);
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                if (bw > 20f && l.bar >= 0f) Widgets.FillableBar(new Rect(bx, y + 4.5f, bw, 15f), Mathf.Clamp01(l.bar));
            }
            GUI.color = Color.white;
            y += h;
        }
        return height;
    }
}

/// Список заданий: строки разной высоты, через одну подсвечены, слева — за что тащить; отпустили — reorder(откуда, куда).
/// Пока тащат, у края списка прокручивает сам: ваниль этого не делает.
/// Имя — окно ванили; новое имя отдаёт в done: из окна задания — в черновик (применяет кнопка окна), из строки очереди — командой Edit
/// (в Multiplayer — у всех). Пустое имя — снова название по умолчанию.
public class Dialog_RMRename : Dialog_Rename<IRenameable>
{
    readonly Action<string> done;

    public Dialog_RMRename(string current, Action<string> done) : base(null)
    {
        curName = current;
        this.done = done;
    }

    protected override AcceptanceReport NameIsValid(string name) => true;

    protected override void OnRenamed(string name) => done?.Invoke(name);
}

/// Строки, что тащат мышью, как ReorderableWidget ванили: нажали на строку и повели больше 5 px — она подсвечена, черта — куда встанет,
/// отпустили — reorder(from, to). Своё, не ReorderableWidget: в Multiplayer ванильное перетаскивание не срабатывало (JOURNAL mp-fix).
public sealed class RowList
{
    static readonly Color LineColor = new(1f, 1f, 1f, 0.6f), HeldColor = new(1f, 1f, 1f, 0.3f);
    Vector2 scroll, at;
    int from = -1, frame;
    bool dragging;

    public void Reset() => (scroll, from, dragging) = (Vector2.zero, -1, false);

    public void Draw<T>(Rect outRect, List<T> items, Func<T, float> height, Action<Rect, T> row, Action<int, int> reorder)
    {
        Event e = Event.current;
        // список не рисовался кадр (вкладку закрыли) или кнопка мыши уже не зажата (отпускание ушло мимо) — перетаскивание кончается
        if (from >= items.Count || Time.frameCount > frame + 1 || from >= 0 && e.type == EventType.Repaint && !UnityGUIBugsFixer.IsLeftMouseButtonPressed())
            (from, dragging) = (-1, false);
        frame = Time.frameCount;
        // отпускание — до строк: кнопка строки съедает MouseUp (Use), после строк его уже не видно; окно получает его и вне себя (rawType)
        bool release = from >= 0 && e.rawType == EventType.MouseUp;
        Rect view = new(0f, 0f, outRect.width - 16f, items.Sum(height));
        Vector2 m = e.mousePosition;
        const float edge = 40f;
        if (dragging && e.type == EventType.Repaint && m.x >= outRect.x && m.x <= outRect.xMax)
            scroll.y = Mathf.Clamp(scroll.y + (m.y < outRect.y + edge ? -1f : m.y > outRect.yMax - edge ? 1f : 0f) * 10f * RMUI.RowH * Time.unscaledDeltaTime,
                0f, Mathf.Max(0f, view.height - outRect.height));
        bool press = e.type == EventType.MouseDown && e.button == 0 && Mouse.IsOver(outRect);
        Widgets.BeginScrollView(outRect, ref scroll, view);
        m = e.mousePosition;
        if (from >= 0 && !dragging && e.type == EventType.Repaint && Vector2.Distance(at, m) > 5f)
        {
            dragging = true;
            SoundDefOf.DragElement.PlayOneShotOnCamera();
        }
        float y = 0f, line = view.height;
        int to = items.Count;
        for (int i = 0; i < items.Count; i++)
        {
            float h = height(items[i]);
            Rect r = new(0f, y, view.width, h);
            // нажатие не съедаем: кнопки строки нажимаются как обычно
            if (press && Mouse.IsOver(r)) (from, at, dragging) = (i, m, false);
            if (to == items.Count && m.y < y + h / 2f) (to, line) = (i, y);
            if (i % 2 == 0) Widgets.DrawAltRect(r);
            if (dragging && i == from)
            {
                GUI.color = HeldColor;
                Widgets.DrawHighlight(r);
                GUI.color = Color.white;
            }
            GUI.DrawTexture(new Rect(r.x + 4f, r.y + (h - 16f) / 2f, 16f, 16f), TexButton.DragHash);
            row(new Rect(r.x + 26f, r.y, r.width - 26f, h), items[i]);
            y += h;
        }
        if (dragging && e.type == EventType.Repaint) Widgets.DrawBoxSolid(new Rect(0f, Mathf.Clamp(line - 1f, 0f, view.height - 2f), view.width, 2f), LineColor);
        if (release)
        {
            if (dragging && to != from && to != from + 1)
            {
                SoundDefOf.DropElement.PlayOneShotOnCamera();
                reorder(from, to);
            }
            (from, dragging) = (-1, false);
        }
        Widgets.EndScrollView();
    }
}

/// Строка карточки (RMUI.Card): подпись слева, две колонки значений справа; wide — текст на всю ширину с переносом;
/// bar — полоса между подписью и значением (у стоимости без предела — полная); sep — черта; пустая строка — отступ высотой h;
/// check — строка целиком рисует сама (галка с подписью).
public sealed class Line
{
    public string left = "", mid, right;
    public Action<Rect> check;
    public Color color = Color.white;
    public bool wide, sep;
    public float bar = -1f, h = 24f;
}


/// Справка блока — выпадает под кнопкой «Справка» (RMUI.HelpButton), правым краем по кнопке, как список ванили: блоки с заголовками
/// (Medium), высота по тексту, не выше экрана (дальше — прокрутка). Клик мимо или Esc — закрыть; клик по той же кнопке — тоже.
public class Dialog_RMHelp : Window
{
    readonly List<(string title, string text)> blocks;
    readonly string key;
    readonly Vector2 corner;
    Vector2 scroll;
    const float W = 560f, Gap = 14f, TitleH = 34f;
    /// Закрыли кликом мимо — тем же кликом по своей кнопке не открывать снова.
    static string lastKey;
    static float lastClose;

    Dialog_RMHelp(List<(string title, string text)> blocks, string key, Vector2 corner)
    {
        this.blocks = blocks;
        this.key = key;
        this.corner = corner;
        layer = WindowLayer.Super;
        closeOnClickedOutside = true;
        closeOnCancel = true;
        doCloseX = false;
        absorbInputAroundWindow = false;
        preventCameraMotion = false;
        drawShadow = true;
        soundAppear = SoundDefOf.FloatMenu_Open;
        soundClose = SoundDefOf.FloatMenu_Cancel;
    }

    /// b — кнопка в координатах окна, где она нарисована: угол под ней справа — через мышь (она сейчас на кнопке).
    public static void Toggle(List<(string title, string text)> blocks, Rect b)
    {
        string key = string.Join("|", blocks.Select(x => x.title + x.text));
        if (Find.WindowStack.Windows.OfType<Dialog_RMHelp>().FirstOrDefault(x => x.key == key) is { } open)
        {
            open.Close();
            return;
        }
        if (lastKey == key && Time.realtimeSinceStartup - lastClose < 0.4f) return;
        Vector2 m = Event.current.mousePosition;
        Find.WindowStack.Add(new Dialog_RMHelp(blocks, key, UI.MousePositionOnUIInverted + new Vector2(b.xMax - m.x, b.yMax - m.y + 2f)));
    }

    public override void PostClose()
    {
        base.PostClose();
        lastKey = key;
        lastClose = Time.realtimeSinceStartup;
    }

    float ContentH(float w) => blocks.Sum(x => (x.title != null ? TitleH : 0f) + RMUI.HelpH(x.text, w)) + (blocks.Count - 1) * Gap;

    public override Vector2 InitialSize
    {
        get
        {
            Text.Font = GameFont.Small;
            return new Vector2(Mathf.Min(W, UI.screenWidth), Mathf.Min(UI.screenHeight * 0.85f, 2f * Margin + ContentH(W - 2f * Margin - 16f) + 4f));
        }
    }

    /// Правый верхний угол — под кнопкой, у её правого края; не влезло вниз — выше, по экрану.
    protected override void SetInitialSizeAndPosition()
    {
        Vector2 s = InitialSize;
        float x = Mathf.Clamp(corner.x - s.x, 0f, UI.screenWidth - s.x), y = Mathf.Clamp(corner.y, 0f, UI.screenHeight - s.y);
        windowRect = new Rect(x, y, s.x, s.y).Rounded();
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        bool scrolls = ContentH(inRect.width - 16f) > inRect.height;
        float w = scrolls ? inRect.width - 16f : inRect.width;
        Rect view = new(0f, 0f, w, ContentH(w));
        Widgets.BeginScrollView(inRect, ref scroll, view, scrolls);
        float y = 0f;
        foreach (var (title, text) in blocks)
        {
            if (title != null)
            {
                Text.Font = GameFont.Medium;
                RMUI.Label(new Rect(0f, y, w, TitleH), title);
                Text.Font = GameFont.Small;
                y += TitleH;
            }
            float h = RMUI.HelpH(text, w);
            RMUI.Help(new Rect(0f, y, w, h), text);
            y += h + Gap;
        }
        Widgets.EndScrollView();
    }
}
