using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ReplimatMech;

/// Вкладка «Задания» репликатора — как у верстака ванили: «Добавить задание», справа вверху — копировать все задания и вставить,
/// под ними — что в баках; задания одной очередью: печатает первое сверху, что можно печатать. Строку тащат мышью; в строке —
/// копировать, пауза, окно задания, удалить.
[StaticConstructorOnStartup]
public class ITab_RMBills : ITab
{
    /// Буфер обмена, как BillUtility.Clipboard: все задания репликатора или одно; вставляется в любой репликатор.
    public static List<Order> Clipboard;
    /// Грузится при первом рисовании: вкладку ваниль создаёт не в главном потоке, ContentFinder там отдаёт пусто.
    public static readonly CachedTexture Gear = new("UI/Icons/Options/OptionsGeneral");
    const float Btn = 24f, BtnGap = 4f;
    readonly RowList list = new();

    public ITab_RMBills()
    {
        size = new Vector2(460f, 480f);
        labelKey = "TabBills";
    }

    /// Ширина — как у панели осмотра под вкладкой.
    protected override void UpdateSize() => size = new Vector2(InspectPaneUtility.PaneWidthFor(Find.WindowStack.WindowOfType<IInspectPane>()), size.y);

    protected override void FillTab()
    {
        if (SelThing.TryGetComp<CompPrinter>() is not { } c) return;
        Rect r = new Rect(0f, 0f, size.x, size.y).ContractedBy(10f);
        // значки — слева от крестика вкладки (он у правого края, 22 от него)
        Bar(new Rect(r.x, r.y, size.x - 30f - r.x, 29f), c);
        RMUI.TanksBlock(new Rect(r.x, r.y + 33f, r.width, RMUI.TanksH), c.Net);
        float y = r.y + 37f + RMUI.TanksH;
        Orders(new Rect(r.x, y, r.width, r.yMax - y), c, list);
    }

    /// «Добавить задание» слева, у правого края — копировать все задания и вставить: во вкладке и в «Очередях» компьютера.
    public static void Bar(Rect r, CompPrinter c)
    {
        if (Widgets.ButtonText(new Rect(r.x, r.y, 150f, 29f), "AddBill".Translate())) Find.WindowStack.Add(new Dialog_AddBill(c));
        float bx = r.xMax - Btn, by = r.y + (29f - Btn) / 2f;
        Paste(new Rect(bx, by, Btn, Btn), c);
        if (Icon(new Rect(bx - Btn - BtnGap, by, Btn, Btn), TexButton.Copy, c.orders.Count > 0)) Copy(c.orders);
    }

    /// Задания репликатора — строки, что тащат мышью.
    public static void Orders(Rect r, CompPrinter c, RowList list)
    {
        list.Draw(r, c.orders.ToList(), RMUI.OrderHeight, (row, o) => RMUI.OrderRow(row, c, o), (from, to) => Edit.Reorder(c, from, to));
    }

    /// Значок-кнопка; нельзя — серый и не нажимается.
    public static bool Icon(Rect r, Texture2D tex, bool active)
    {
        if (active) return Widgets.ButtonImageFitted(r, tex, Color.white);
        GUI.color = Color.gray;
        Widgets.DrawTextureFitted(r, tex, 1f);
        GUI.color = Color.white;
        return false;
    }

    public static void Copy(IEnumerable<Order> orders)
    {
        Clipboard = orders.Select(o => o.Clone()).ToList();
        SoundDefOf.Tick_High.PlayOneShotOnCamera();
    }

    /// Вставить, как в ванили: буфер пуст — значка нет; ничего из буфера не подходит этому репликатору (механоид в репликатор
    /// для животных) — значок серый. Задание на тот же шаблон уже есть — получает настройки из буфера.
    static void Paste(Rect r, CompPrinter c)
    {
        if (Clipboard == null) return;
        List<Order> fit = Clipboard.Where(o => c.Accepts(o.pattern) && !GameComponent_RM.Get.Trashed(o.pattern)).ToList();
        if (!Icon(r, TexButton.Paste, fit.Count > 0)) return;
        foreach (Order o in fit) Edit.Create(c, o.Clone());
        SoundDefOf.Tick_Low.PlayOneShotOnCamera();
    }
}

/// «Добавить задание» — одно окно: слева дерево категорий склада ванили (при открытии свёрнуто и выделено «Все» сверху — все шаблоны;
/// клик по группе выделяет её, раскрывает только стрелка), посередине карточки шаблонов, справа настройка задания выбранного шаблона
/// и «Добавить задание». Окно остаётся открытым:
/// можно добавить ещё. Задание на шаблон в репликаторе одно (воскрешений и животных — сколько угодно): шаблон уже в заданиях — справа его задание
/// и «Применить». Неизученное и запрещённое — карточка полупрозрачная, с причиной, не выбирается. На карточке — «Удалить»: в «Корзину»
/// (своё дерево под «Все»; там на карточке круглая стрелка — вернуть) уходит всё.
public class Dialog_AddBill : Window
{
    sealed class Node
    {
        /// key — своя у каждой строки обоих деревьев (в корзине — с приставкой «trash/»), kind — категория без приставки.
        public string key, kind, label;
        public int order, count;
        /// Дерево «Корзины».
        public bool bin;
        public readonly List<Node> kids = new();
        public readonly List<Pattern> items = new();
        /// Черты и гены базы людей — в группах «Черт характера» и «Генов».
        public readonly List<(TraitDef def, int degree)> traits = new();
        public readonly List<GeneDef> genes = new();
    }

    /// Раздел карточек посередине: шаблоны (people — люди), черты или гены.
    sealed class Sec
    {
        public string title;
        public List<Pattern> list;
        public bool people;
        public List<(TraitDef def, int degree)> traits;
        public List<GeneDef> genes;
    }

    readonly CompPrinter printer;
    readonly QuickSearchWidget search = new();
    /// Окно открывается с раскрытым «Все»: категории видны сразу.
    readonly HashSet<string> open = new() { AllKey };
    const string AllKey = "all", MechKey = "mechs", AnimalKey = "animals", PeopleKey = "people", TraitsKey = "traits", GenesKey = "genes", TrashKey = "trash";
    /// Скопированный человек — строка данных (2026-10-21): «Копировать» на его карточке; «Вставить» на карточке — правкой в конструктор,
    /// в шапке «Людей» — сразу новым шаблоном. Буфер окна, не игры.
    static string personClip;
    Node tree, trashTree;
    string treeStamp, trashStamp, selCat = AllKey;
    Vector2 treeScroll, tileScroll, rightScroll;
    Pattern sel;
    OrderForm form;
    /// Конструктор выбранного шаблона человека — справа под заданием.
    TemplateEditor editor;

    /// По содержимому (2026-10-20: компактность важнее): категории — по самому длинному названию, шаблоны — два столбца карточек,
    /// задание — по полям (у биорепликатора — по таблице навыков конструктора); высота — одна (2026-10-21: как у репликатора).
    public override Vector2 InitialSize => RMUI.Fit(LeftW + MidW + RightW + 2f * BlockGap + 2f * Margin, 720f);

    const float BlockGap = 20f;

    /// Шаблоны: у репликатора — два столбца карточек (2026-10-20), у биорепликатора — один, карточка человека во всю ширину,
    /// черты и гены — по 2 в ряд, названия целиком (2026-10-21), карточка человека — не уже двух столбцов навыков в половине. Запас 4:
    /// ваниль округляет окно вниз, без него второй столбец терял долю пикселя и пропадал.
    float MidW => printer.Incubator
        ? Mathf.Max(PersonMinW, TemplateEditor.MidCols * (TemplateEditor.MinCardW + TemplateEditor.CardGap) - TemplateEditor.CardGap) + 16f + 6f + 4f
        : 2f * TileMinW + 8f + 16f + 6f + 4f;

    /// Категории не обрезаются: столбец не уже самого длинного названия с его отступом в обоих деревьях (рамка 4 с каждой стороны,
    /// стрелка 22, число 54, прокрутка 16).
    float LeftW
    {
        get
        {
            GameFont f = Text.Font;
            Text.Font = GameFont.Small;
            static float Widest(Node n, float indent) =>
                Mathf.Max(indent + Text.CalcSize(n.label).x, n.kids.Select(k => Widest(k, indent + 14f)).DefaultIfEmpty(0f).Max());
            float w = Mathf.Max(Widest(Root, 0f), Widest(TrashRoot, 0f)) + 2f * TreePad + 22f + 54f + 16f + 8f;
            Text.Font = f;
            return w;
        }
    }

    float RightW => printer.Incubator ? Mathf.Max(480f, TemplateEditor.MinW) : 480f;

    /// pick — сразу выбрать шаблон (из окна задания: «Шаблон — Изменить»).
    public Dialog_AddBill(CompPrinter printer, Pattern pick = null)
    {
        this.printer = printer;
        doCloseX = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = !printer.Incubator;
        if (pick != null) Pick(pick);
    }

    /// Выбор плитки: справа её задание (уже в очереди — его настройки); у шаблона человека под ним конструктор, и задание считает по черновику.
    void Pick(Pattern p)
    {
        sel = p;
        rightScroll = Vector2.zero;
        editor = p.IsHuman ? new TemplateEditor(printer, p.human) : null;
        Pattern shown = editor?.Pat ?? p;
        Order o = printer.Same(shown)?.Clone() ?? Order.Default(shown, printer);
        o.pattern = shown;
        // одна кнопка на задание и шаблон (2026-10-21): правка шаблона — «Применить» (сохраняет его, а задание в очереди — и правку задания)
        form = new OrderForm(printer, o)
        {
            beforeCommit = () => { if (editor?.Changed == true) editor.Save(); },
            commitRestarts = () => editor?.Restarts == true,
            extraChanged = () => editor?.Changed == true,
            extraEdited = () => editor?.Edited == true,
            extraReset = () => editor?.Reset(),
        };
    }

    PowerNet Net => printer.Net;

    /// Что можно добавить сюда: шаблоны (у репликатора для животных — корм) и воскрешение механоида; в биорепликатор — ДНК животных
    /// и шаблоны людей; trashed — что из этого в корзине.
    List<Pattern> Patterns(bool trashed)
    {
        GameComponent_RM gc = GameComponent_RM.Get;
        if (printer.Incubator) return gc.Bio(trashed);
        List<Pattern> all = gc.Shown(trashed).Where(printer.Accepts).ToList();
        if (ModsConfig.BiotechActive) all.AddRange(Pattern.Resurrections.Where(p => printer.Accepts(p) && gc.Trashed(p) == trashed));
        return all;
    }

    public override void DoWindowContents(Rect inRect)
    {
        if (!printer.parent.Spawned)
        {
            Close();
            return;
        }
        // что в баках — справа в строке заголовка, как в окне компьютера (2026-10-18)
        float tanksW = RMUI.TanksTitle(new Rect(inRect.x, inRect.y, inRect.width, 36f), Net);
        Text.Font = GameFont.Medium;
        RMUI.Label(new Rect(inRect.x, inRect.y, inRect.width - 40f - tanksW, 36f), "AddBill".Translate() + " — " + printer.RenamableLabel);
        // три столбца, у каждого шапка (2026-10-18: слева категории и шаблоны, справа задание и конструктор); экран уже окна —
        // место отдают шаблоны
        const float g = BlockGap;
        float y = inRect.y + 46f, lw = LeftW, rw = Mathf.Min(RightW, inRect.width - lw - 2f * g - 200f);
        Rect left = new(inRect.x, y, lw, inRect.yMax - y), right = new(inRect.xMax - rw, y, rw, left.height), mid = new(left.xMax + g, y, right.x - g - left.xMax - g, left.height);
        RMUI.Label(new Rect(left.x, y, left.width, 34f), "RM_ColCategories".Translate());
        RMUI.Label(new Rect(mid.x, y, mid.width, 34f), "RM_ColPatterns".Translate());
        RMUI.Label(new Rect(right.x, y, right.width, 34f), "RM_ColBill".Translate());
        Text.Font = GameFont.Small;
        GUI.color = RMUI.Edge;
        Widgets.DrawLineVertical(left.xMax + g / 2f, y, left.height);
        Widgets.DrawLineVertical(mid.xMax + g / 2f, y, left.height);
        GUI.color = Color.white;
        left.yMin = mid.yMin = right.yMin = y + 42f;
        // поиск кончается там, где рамки деревьев (за ними — полоса прокрутки)
        search.OnGUI(new Rect(left.x, left.y, left.width - 16f, 24f));
        Tree(new Rect(left.x, left.y + 30f, left.width, left.height - 30f));
        // выбранной категории больше нет (поиск, вернули последнее из неё) — всё её дерево
        Node shown = ByKey(Root, selCat) ?? ByKey(TrashRoot, selCat) ?? (selCat.StartsWith(TrashKey) ? TrashRoot : Root);
        if (shown.count == 0 && (shown == Root || shown == TrashRoot))
            Muted(mid, (search.filter.Active ? "RM_NothingFound" : shown.bin ? "RM_TrashEmpty" : printer.Incubator ? "RM_DnaNone" : "RM_NoPatterns").Translate());
        else Tiles(mid, shown);
        // убрали в корзину — выбора нет
        if (sel != null && GameComponent_RM.Get.Trashed(sel)) sel = null;
        if (sel == null) form = null;
        // одна «Справка» на окно — справа под баками, видна всегда, и без выбора (2026-10-20); блоками по частям окна: шаблоны
        // (у биорепликатора — откуда ДНК), задание, конструктор шаблона, корзина
        var help = new List<(string, string)>();
        if (printer.Incubator)
        {
            help.Add(("AnimalsSection".Translate(), "RM_DnaHelpAnimals".Translate()));
            help.Add(("RM_CatPeople".Translate(), "RM_DnaHelp".Translate()));
        }
        else help.Add(("RM_ColPatterns".Translate(), "RM_PatternsHelp".Translate()));
        help.Add(("RM_ColBill".Translate(), form?.HelpText ?? "RM_RestartNote".Translate().ToString()));
        if (printer.Incubator) help.AddRange(TemplateEditor.Help);
        // всё про корзину — одним блоком, последним (2026-10-21)
        help.Add(("RM_Trash".Translate(), "RM_TrashHelp".Translate() + "\n" + (printer.Incubator ? "RM_TrashHelpBio" : "RM_TrashHelpMech").Translate()));
        RMUI.HelpButton(new Rect(right.x, y, right.width, 34f), help);
        if (form == null)
        {
            editor = null;
            Muted(right, "RM_PickTile".Translate());
            return;
        }
        // у шаблона человека задание печатает черновик конструктора; шаблон уже в заданиях — правится его задание
        if (editor != null) form.o.pattern = editor.Pat;
        form.live = printer.Same(form.o.pattern);
        // задание в очереди без правки — «Удалить» его (2026-10-21)
        void Idle(Rect b)
        {
            if (RMUI.Button(b, "DeleteBillTip".Translate())) RMUI.DeleteOrder(printer, form.live);
        }
        if (editor == null)
        {
            form.Draw(right, Idle);
            return;
        }
        // сверху — название шаблона: всё под ним, и задание, — этой пешки (2026-10-20); задание в рамке, как блоки конструктора
        // под ним; не влезло — своя прокрутка правой части
        const float pad = 14f, sh = TemplateEditor.StripH;
        float w = right.width;
        float FormH() => form.Height(w - 2f * pad) + 2f * pad;
        float Total() => sh + RMUI.Gap + FormH() + RMUI.Gap + editor.Height(w);
        bool scroll = Total() > right.height;
        if (scroll) w -= 16f;
        float fh = FormH();
        Rect area = scroll ? new Rect(0f, 0f, w, Total()) : right;
        if (scroll) Widgets.BeginScrollView(right, ref rightScroll, area);
        editor.Strip(new Rect(area.x, area.y, w, sh));
        Rect box = new(area.x, area.y + sh + RMUI.Gap, w, fh);
        Widgets.DrawBoxSolidWithOutline(box, RMUI.CardFill, RMUI.Edge);
        form.Draw(box.ContractedBy(pad), Idle);
        editor.Draw(new Rect(area.x, box.yMax + RMUI.Gap, w, editor.Height(w)));
        if (scroll) Widgets.EndScrollView();
    }

    static void Muted(Rect r, string s)
    {
        GUI.color = RMUI.Muted;
        RMUI.Label(r, s);
        GUI.color = Color.white;
    }

    static Node ByKey(Node n, string key) => key == null ? null : n.key == key ? n : n.kids.Select(k => ByKey(k, key)).FirstOrDefault(k => k != null);

    static IEnumerable<Pattern> All(Node n) => n.items.Concat(n.kids.SelectMany(All));

    static List<T> Flat<T>(Node n, Func<Node, List<T>> of) => of(n).Concat(n.kids.SelectMany(k => Flat(k, of))).ToList();

    Node Root => Cached(ref tree, ref treeStamp, false);

    /// «Корзина» — свои категории того, что в корзине.
    Node TrashRoot => Cached(ref trashTree, ref trashStamp, true);

    /// Дерево строится заново, когда сменились шаблоны, база людей, корзина или поиск.
    Node Cached(ref Node node, ref string stamp, bool trashed)
    {
        List<Pattern> all = Patterns(trashed);
        GameComponent_RM gc = GameComponent_RM.Get;
        string now = all.Count + "|" + gc.traitBase.Count + "|" + gc.geneBase.Count + "|" + gc.trash.Count + "|" + search.filter.Text;
        if (node == null || stamp != now) (node, stamp) = (Build(all, trashed), now);
        return node;
    }

    /// Корень — «Все» или «Корзина», под ним категории.
    Node Build(List<Pattern> all, bool trashed)
    {
        string root0 = trashed ? TrashKey : AllKey;
        var root = new Node { key = root0, kind = root0, label = (trashed ? "RM_Trash" : "RM_CatAll").Translate(), bin = trashed };
        Node Kid(Node parent, string key, string label, int order)
        {
            var k = new Node { key = (trashed ? TrashKey + "/" : "") + key, kind = key, label = label, order = order, bin = trashed };
            parent.kids.Add(k);
            return k;
        }
        var map = new Dictionary<string, Node>();
        foreach (Pattern p in all)
        {
            if (search.filter.Active && !search.filter.Matches(p.Label)) continue;
            Node n = root;
            foreach (var (key, label, order) in CategoryPath(p))
            {
                if (!map.TryGetValue(key, out Node k)) map[key] = k = Kid(n, key, label, order);
                n = k;
            }
            n.items.Add(p);
        }
        // у биорепликатора «Люди» (в шапке — «Вставить»), за ними черты и гены базы людей есть всегда (2026-10-18);
        // у корзины — только с тем, что в ней
        bool bio = printer.Incubator && Rules.BioOn, always = bio && !trashed && !search.filter.Active;
        if (always && !map.ContainsKey(PeopleKey)) Kid(root, PeopleKey, "RM_CatPeople".Translate(), 1000);
        // черт и генов много — группами (2026-10-21): черты — по ступени цены, гены — по категориям сборщика генов ванили
        List<(TraitDef def, int degree)> traits = BaseTraits(trashed);
        List<GeneDef> genes = BaseGenes(trashed);
        if (bio && (traits.Count > 0 || always))
        {
            Node tn = Kid(root, TraitsKey, "Traits".Translate().CapitalizeFirst(), 1001);
            foreach (IGrouping<int, (TraitDef, int)> g in traits.GroupBy(Human.TraitStep).OrderByDescending(g => g.Key))
                Kid(tn, TraitsKey + ":" + g.Key, TraitGroup(g.Key), -g.Key).traits.AddRange(g);
        }
        if (bio && (genes.Count > 0 || always))
        {
            Node gn = Kid(root, GenesKey, "Genes".Translate().CapitalizeFirst(), 1002);
            foreach (IGrouping<(string key, string label, float order), GeneDef> g in genes.GroupBy(GeneGroup).OrderByDescending(g => g.Key.order))
                Kid(gn, GenesKey + ":" + g.Key.key, g.Key.label, gn.kids.Count).genes.AddRange(g);
        }
        Count(root);
        return root;
    }

    static int Count(Node n) => n.count = n.items.Count + n.traits.Count + n.genes.Count + n.kids.Sum(Count);

    static bool IsTraits(string key) => key != null && (key == TraitsKey || key.StartsWith(TraitsKey + ":"));
    static bool IsGenes(string key) => key != null && (key == GenesKey || key.StartsWith(GenesKey + ":"));

    /// Группа гена — категория сборщика генов ванили; «Сопротивление и чувствительность» — двумя группами на её месте по знаку метаболизма,
    /// сопротивление первым (2026-10-21: короче самая длинная подпись — уже левая часть).
    static (string key, string label, float order) GeneGroup(GeneDef g)
    {
        GeneCategoryDef c = g.displayCategory;
        if (c.defName != "ResistanceAndWeakness") return (c.defName, c.LabelCap, c.displayPriorityInXenotype);
        bool res = g.biostatMet <= 0;
        return (c.defName + (res ? ":res" : ":sens"), (res ? "RM_GeneResistance" : "RM_GeneSensitivity").Translate(), c.displayPriorityInXenotype + (res ? 0.5f : 0f));
    }

    static string TraitGroup(int step) => (step >= 2 ? "RM_TraitsVeryGood" : step > 0 ? "RM_TraitsGood" : step <= -2 ? "RM_TraitsVeryBad" : "RM_TraitsBad").Translate();

    /// Гены базы людей — для категории «Гены», в порядке сборщика генов ванили; trashed — что в корзине; при поиске — подходящие.
    List<GeneDef> BaseGenes(bool trashed)
    {
        GameComponent_RM gc = GameComponent_RM.Get;
        return !printer.Incubator || !Rules.BioOn ? new List<GeneDef>()
            : gc.geneBase.Where(g => gc.Trashed(g) == trashed && (!search.filter.Active || search.filter.Matches(g.label))).OrderBy(GeneUtility.GenesInOrder.IndexOf).ToList();
    }

    /// Черты базы людей — для категории «Черты характера», по названию; trashed — что в корзине; при поиске — подходящие.
    List<(TraitDef def, int degree)> BaseTraits(bool trashed)
    {
        GameComponent_RM gc = GameComponent_RM.Get;
        return !printer.Incubator || !Rules.BioOn ? new List<(TraitDef, int)>()
            : gc.Traits.Where(t => gc.Trashed(t) == trashed && (!search.filter.Active || search.filter.Matches(TraitLabel(t)))).OrderBy(TraitLabel).ToList();
    }

    /// Название черты — для пола открытого шаблона.
    string TraitLabel((TraitDef def, int degree) t) => t.def.DataAtDegree(t.degree).GetLabelCapFor(editor?.h.gender ?? Gender.Female);

    /// Путь в дереве — категории склада ванили от корня; механоиды и воскрешение, животные — отдельной веткой.
    static IEnumerable<(string key, string label, int order)> CategoryPath(Pattern p)
    {
        if (p.IsAnimal)
        {
            yield return (AnimalKey, "AnimalsSection".Translate(), 999);
            yield break;
        }
        if (p.IsHuman)
        {
            yield return (PeopleKey, "RM_CatPeople".Translate(), 1000);
            yield break;
        }
        if (p.IsMech)
        {
            yield return (MechKey, DefDatabase<MainButtonDef>.GetNamedSilentFail("Mechs")?.LabelCap ?? "RM_CatMechs".Translate(), 1000);
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

    /// Дерево — только категории; при поиске раскрыто всё.
    void Flatten(Node n, int depth, List<(Node node, int depth)> rows)
    {
        foreach (Node k in n.kids.OrderBy(k => k.order))
        {
            rows.Add((k, depth));
            if (search.filter.Active || open.Contains(k.key)) Flatten(k, depth + 1, rows);
        }
    }

    const float TreePad = 4f;

    /// Слева два дерева, каждое в своей рамке с фоном (2026-10-21): «Все» — все категории, сразу за ним «Корзина» — свои категории
    /// того, что в ней (найти, что вернуть); оба раскрываются стрелкой, рамка растёт и сжимается с деревом. Окно открывается на «Все»
    /// (2026-10-18); поиск ничего не нашёл — в его рамке «Ничего не найдено».
    void Tree(Rect outRect)
    {
        const float h = 26f, gap = 8f;
        var frames = new List<List<(Node node, int depth)>>();
        foreach (Node root in new[] { Root, TrashRoot })
        {
            var rows = new List<(Node node, int depth)> { (root, 0) };
            if (search.filter.Active || open.Contains(root.key)) Flatten(root, 1, rows);
            frames.Add(rows);
        }
        bool none = search.filter.Active && Root.kids.Count == 0;
        float FrameH(int f) => (frames[f].Count + (f == 0 && none ? 1 : 0)) * h + 2f * TreePad;
        Rect view = new(0f, 0f, outRect.width - 16f, FrameH(0) + gap + FrameH(1));
        Widgets.BeginScrollView(outRect, ref treeScroll, view);
        float y = 0f;
        for (int f = 0; f < frames.Count; f++)
        {
            Widgets.DrawBoxSolidWithOutline(new Rect(0f, y, view.width, FrameH(f)), RMUI.CardFill, RMUI.Edge);
            float ry = y + TreePad;
            foreach (var (node, depth) in frames[f])
            {
                Rect r = new(TreePad + depth * 14f, ry, view.width - 2f * TreePad - depth * 14f, h);
                // стрелка — свернуть/раскрыть; строка — выделить и показать всё, что в ней
                if (node.kids.Count > 0 && !search.filter.Active && Widgets.ButtonInvisible(new Rect(r.x, r.y, 20f, h)) && !open.Remove(node.key))
                    open.Add(node.key);
                Row(r, node);
                if (node.kids.Count > 0)
                    GUI.DrawTexture(new Rect(r.x + 2f, r.y + 6f, 14f, 14f), search.filter.Active || open.Contains(node.key) ? TexButton.Collapse : TexButton.Reveal);
                ry += h;
            }
            if (f == 0 && none)
            {
                Text.Anchor = TextAnchor.MiddleLeft;
                Muted(new Rect(TreePad + 14f + 22f, ry, view.width - 2f * TreePad - 36f, h), "RM_NothingFound".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
            }
            y += FrameH(f) + gap;
        }
        Widgets.EndScrollView();
    }

    /// Строка дерева: выделение, название с отступом под стрелку, число справа; клик — выбрать.
    void Row(Rect r, Node node)
    {
        if (node.key == selCat) Widgets.DrawHighlightSelected(r);
        else Widgets.DrawHighlightIfMouseover(r);
        Text.Anchor = TextAnchor.MiddleLeft;
        RMUI.Label(new Rect(r.x + 22f, r.y, r.width - 70f, r.height), node.label.Truncate(r.width - 70f));
        GUI.color = RMUI.Muted;
        Text.Anchor = TextAnchor.MiddleRight;
        RMUI.Label(new Rect(r.xMax - 50f, r.y, 46f, r.height), node.count.ToString());
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
        if (!Widgets.ButtonInvisible(r)) return;
        selCat = node.key;
        tileScroll = Vector2.zero;
    }

    /// Категория целиком — карточки всех шаблонов, по подкатегориям. У людей справа в шапке раздела — «Вставить» (2026-10-21),
    /// в корзине — нет.
    void Tiles(Rect r, Node n)
    {
        var sections = new List<Sec>();
        // раздел — категория, механоиды — двумя: печать и восстановление; по названию, воскрешение — по весу, как рецепты ванили;
        // черты и гены — карточками черт и генов; all — со всем, что в подкатегориях
        void Add(Node k, bool all)
        {
            if (IsTraits(k.kind) || IsGenes(k.kind))
            {
                List<(TraitDef def, int degree)> ts = all ? Flat(k, x => x.traits) : k.traits;
                List<GeneDef> gs = all ? Flat(k, x => x.genes) : k.genes;
                bool tr = IsTraits(k.kind);
                sections.Add(new Sec { title = k.label + " (" + (tr ? ts.Count : gs.Count) + ")", traits = tr ? ts : null, genes = tr ? null : gs });
                return;
            }
            List<Pattern> list = (all ? All(k) : k.items).OrderBy(p => Pattern.Resurrections.IndexOf(p)).ThenBy(p => p.human?.blank != true).ThenBy(p => p.Label).ToList();
            if (k.kind != MechKey) sections.Add(new Sec { title = k.label + " (" + list.Count + ")", list = list, people = k.kind == PeopleKey });
            else
                foreach (IGrouping<bool, Pattern> g in list.GroupBy(p => p.resurrect))
                    sections.Add(new Sec { title = (g.Key ? "RM_CatResurrect" : "RM_CatMechPrint").Translate() + " (" + g.Count() + ")", list = g.ToList() });
        }
        if (n.items.Count > 0 || n.traits.Count > 0 || n.genes.Count > 0 || n.kind == PeopleKey || (IsTraits(n.kind) || IsGenes(n.kind)) && n.kids.Count == 0) Add(n, false);
        foreach (Node k in n.kids.OrderBy(k => k.order)) Add(k, true);
        // карточки растянуты во всю ширину: зазор 8 и по горизонтали, и по вертикали; от краёв — отступ, иначе рамку обрезает
        const float gap = 8f, pad = 3f;
        float width = r.width - 16f - 2f * pad;
        int cols = Mathf.Max(1, (int)((width + gap) / (TileMinW + gap) + 0.001f));
        float tw = (width + gap) / cols - gap;
        // название не влезло — карточка выше, в несколько строк; у карточек раздела одна высота картинок и названия — строки на одной высоте
        float Ih(List<Pattern> list) => list.Max(p => IconH(p, tw));
        float Th(List<Pattern> list) => (n.bin ? BinTileH : TileH) + Ih(list) + list.Max(p => Text.CalcHeight(p.Label, tw - 8f));
        // люди — карточки во всю ширину, у каждой своя высота
        float mass = Tanks.Mass(Net), value = Tanks.Value(Net);
        // черты и гены — карточки по 2 в ряд; у черты сверху полоса под значок — она выше, чем в блоках шаблона
        float tch = TemplateEditor.TraitCardH(DelS), gch = TemplateEditor.GeneCardH;
        float Rows(Sec x) =>
            x.traits != null ? TemplateEditor.GridH(x.traits.Count, width, TemplateEditor.MidCols, tch) + gap
            : x.genes != null ? TemplateEditor.GridH(x.genes.Count, width, TemplateEditor.MidCols, gch) + gap
            : x.people ? x.list.Sum(p => PersonH(p, width, mass, value) + gap) : (x.list.Count + cols - 1) / cols * (Th(x.list) + gap);
        Rect view = new(0f, 0f, r.width - 16f, pad + sections.Sum(x => 30f + Rows(x)));
        Widgets.BeginScrollView(r, ref tileScroll, view);
        float y = pad;
        foreach (var x in sections)
        {
            // «Вставить» — справа в строке названия, правым краем по значкам карточек, над чертой раздела
            if (x.people && !n.bin) PasteNew(new Rect(pad + width - 4f - HeadIc, y + 2f, HeadIc, HeadIc));
            Widgets.ListSeparator(ref y, view.width, x.title);
            y += 5f;
            if (x.traits != null)
            {
                if (x.traits.Count == 0) Muted(new Rect(pad, y, width, Text.LineHeight), "RM_TraitsNone".Translate());
                TemplateEditor.Grid(new Rect(pad, y, width, 0f), x.traits, TemplateEditor.MidCols, tch, TraitTile);
            }
            else if (x.genes != null)
            {
                if (x.genes.Count == 0) Muted(new Rect(pad, y, width, Text.LineHeight), "RM_GenesNone".Translate());
                TemplateEditor.Grid(new Rect(pad, y, width, 0f), x.genes, TemplateEditor.MidCols, gch, GeneTile);
            }
            else if (x.people)
            {
                float py = y;
                foreach (Pattern p in x.list)
                {
                    float h = PersonH(p, width, mass, value);
                    Person(new Rect(pad, py, width, h), p, mass, value);
                    py += h + gap;
                }
            }
            else
            {
                float th = Th(x.list), ih = Ih(x.list);
                for (int i = 0; i < x.list.Count; i++)
                    Tile(new Rect(pad + i % cols * (tw + gap), y + i / cols * (th + gap), tw, th), x.list[i], ih, mass, value);
            }
            y += Rows(x);
        }
        Widgets.EndScrollView();
    }

    /// Карточка черты базы: клик — в открытый шаблон, как вещь на пешку (2026-10-18), ещё клик — убрать; уже в шаблоне — обведена,
    /// нельзя добавить (конфликт, три черты) или шаблон не открыт — бледная. В корзине — только вернуть.
    void TraitTile(Rect c, (TraitDef def, int degree) t)
    {
        bool bin = GameComponent_RM.Get.Trashed(t);
        Human h = bin ? null : editor?.h;
        bool on = h?.traits.Contains(t) == true, ok = h != null && (on || h.CanAdd(t));
        Widgets.DrawOptionBackground(c, on);
        GUI.color = ok || bin ? Color.white : new Color(1f, 1f, 1f, 0.35f);
        TemplateEditor.TraitCard(c, editor?.h ?? GameComponent_RM.Get.Blank, t, DelS);
        GUI.color = Color.white;
        BinIcon(Corner(c), GameComponent_RM.BinKey(t), TraitLabel(t), bin);
        if (bin && Widgets.ButtonInvisible(c)) Restore(GameComponent_RM.BinKey(t));
        if (!ok || !Widgets.ButtonInvisible(c)) return;
        if (!h.traits.Remove(t)) h.AddTrait(t);
        SoundDefOf.Tick_High.PlayOneShotOnCamera();
    }

    /// Карточка гена базы — как черты: клик — в открытый шаблон, ещё клик — убрать; в шаблоне — обведена; нельзя добавить или убрать
    /// (конфликт, предпосылка, предел метаболизма) или шаблон не открыт — бледная. В корзине — только вернуть.
    void GeneTile(Rect c, GeneDef g)
    {
        bool bin = GameComponent_RM.Get.Trashed(g);
        Human h = bin ? null : editor?.h;
        bool on = h?.genes.Contains(g) == true, ok = h != null && (on ? h.CanRemove(g) : h.CanAdd(g));
        Widgets.DrawOptionBackground(c, on);
        GUI.color = ok || bin ? Color.white : new Color(1f, 1f, 1f, 0.35f);
        TemplateEditor.GeneCard(c, g);
        GUI.color = Color.white;
        BinIcon(Corner(c), GameComponent_RM.BinKey(g), g.LabelCap, bin);
        if (bin && Widgets.ButtonInvisible(c)) Restore(GameComponent_RM.BinKey(g));
        if (!ok || !Widgets.ButtonInvisible(c)) return;
        h.Toggle(g);
        SoundDefOf.Tick_High.PlayOneShotOnCamera();
    }

    const float HeadIc = 20f;

    /// «Вставить» в шапке «Людей» (2026-10-21: как вставка заданий верстака) — сразу сохранённый шаблон: скопированный человек
    /// под названием «Шаблон N»; ничего не скопировано — серая.
    static void PasteNew(Rect icon)
    {
        if (!ITab_RMBills.Icon(icon, TexButton.Paste, personClip != null)) return;
        Edit.AddHuman(personClip);
        SoundDefOf.Tick_Low.PlayOneShotOnCamera();
    }

    /// Карточка шаблона человека (2026-10-21): сверху лицо, справа от него название, «хватит на N», «уже в заданиях» — по центру
    /// высоты лица; низ пополам: слева все навыки сеткой в 2 столбца по порядку ванили (свой значок, уровень, огонёк интереса — столбцами),
    /// справа черты по одной в рамках, как на карточке персонажа ванили, под ними значки генов рядами. Без подписей и подсказок;
    /// карточка — по большей половине.
    const float FaceS = 80f, StackH = 22f, GeneS = 28f, CardPad = 8f, SkillIc = StackH - 2f;

    /// Столбец уровня навыка — под самое длинное число.
    static float LevelW
    {
        get
        {
            GameFont f = Text.Font;
            Text.Font = GameFont.Small;
            float w = Text.CalcSize(Human.MaxLevel.ToString()).x;
            Text.Font = f;
            return w;
        }
    }

    /// Самая узкая карточка человека: в половине — два столбца навыков (значок, уровень, огонёк).
    static float PersonMinW => 3f * CardPad + 2f * (2f * (2f * SkillIc + LevelW + 8f) + CardPad);

    bool InBill(Pattern p) => printer.orders.Any(o => o.pattern == p);

    float PersonH(Pattern p, float w, float mass, float value) => Body(new Rect(0f, 0f, w, 0f), p, Rules.CanPrint(p), mass, value, false);

    /// Значков справа вверху карточки человека: «Переименовать», «Копировать», «Вставить», «Удалить»; в корзине — вернуть и «Удалить»
    /// навсегда, у «Репликанта» только вернуть.
    static int Icons(Pattern p) => !GameComponent_RM.Get.Trashed(p) ? 4 : p.human.blank ? 1 : 2;

    /// Ширина текста карточки человека: справа от лица, слева от значков.
    static float PersonTw(Pattern p, float w) => w - FaceS - 3f * CardPad - Icons(p) * (DelS + 4f);

    /// Карточка в r (её высота — что вернёт); draw == false — только высота. В корзине — без «хватит на N». Бледность лица — от GUI.color.
    float Body(Rect r, Pattern p, bool can, float mass, float value, bool draw)
    {
        Human h = p.human;
        var lines = new List<(string text, Color color)> { (p.Label, Color.white) };
        if (!GameComponent_RM.Get.Trashed(p)) lines.Add((Status(p, can, mass, value), can ? RMUI.Muted : ColorLibrary.RedReadable));
        if (InBill(p)) lines.Add(("RM_InBill".Translate(), RMUI.Blue));
        // значки только в верхней полосе (до r.y + 28): ниже названия строки идут до края карточки
        float[] tw = lines.Select((_, i) => i == 0 ? PersonTw(p, r.width) : r.width - FaceS - 3f * CardPad).ToArray();
        float[] lh = lines.Select((l, i) => Text.CalcHeight(l.text, tw[i])).ToArray();
        float th = lh.Sum(), top = Mathf.Max(FaceS, th);
        float half = (r.width - 3f * CardPad) / 2f, x1 = r.x + 2f * CardPad + half, y0 = r.y + 2f * CardPad + top;
        List<SkillDef> skills = DefDatabase<SkillDef>.AllDefsListForReading.OrderByDescending(s => s.listOrder).ToList();
        int rows = (skills.Count + 1) / 2;
        if (draw)
        {
            RMUI.Icon(new Rect(r.x + CardPad, r.y + CardPad + (top - FaceS) / 2f, FaceS, FaceS), p);
            float ly = r.y + CardPad + (top - th) / 2f;
            for (int i = 0; i < lines.Count; i++)
            {
                GUI.color = lines[i].color;
                RMUI.Label(new Rect(r.x + FaceS + 2f * CardPad, ly, tw[i], lh[i]), lines[i].text);
                ly += lh[i];
            }
            GUI.color = Color.white;
            // навыки — сверху вниз по столбцам
            float lw = LevelW;
            for (int i = 0; i < skills.Count; i++)
            {
                SkillDef s = skills[i];
                float x = r.x + CardPad + i / rows * half / 2f, y = y0 + i % rows * StackH;
                RMUI.SkillIcon(new Rect(x, y + 1f, SkillIc, SkillIc), s);
                // описание навыка ванили — над всей его ячейкой (2026-10-21)
                Rect cell = new(x, y, half / 2f, StackH);
                if (Mouse.IsOver(cell)) TooltipHandler.TipRegion(cell, s.skillLabel.CapitalizeFirst() + "\n" + s.description);
                Text.Anchor = TextAnchor.MiddleRight;
                RMUI.Label(new Rect(x + SkillIc + 4f, y, lw, StackH), h.Level(s).ToString());
                Text.Anchor = TextAnchor.UpperLeft;
                if (h.PassionOf(s) != Passion.None)
                    GUI.DrawTexture(new Rect(x + SkillIc + 4f + lw + 4f, y + 1f, SkillIc, SkillIc),
                        h.PassionOf(s) == Passion.Major ? SkillUI.PassionMajorIcon : SkillUI.PassionMinorIcon);
            }
        }
        // черты — во всю половину, длинное название в две строки; под ними гены рядами
        float ry = y0;
        foreach (var t in h.traits)
        {
            string label = h.TraitLabel(t);
            float fh = Mathf.Max(StackH, Text.CalcHeight(label, half - 10f));
            if (draw)
            {
                GUI.color = CharacterCardUtility.StackElementBackground;
                GUI.DrawTexture(new Rect(x1, ry, half, fh), BaseContent.WhiteTex);
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.MiddleLeft;
                RMUI.Label(new Rect(x1 + 5f, ry, half - 10f, fh), label);
                Text.Anchor = TextAnchor.UpperLeft;
                TemplateEditor.TraitTip(new Rect(x1, ry, half, fh), h, t);
            }
            ry += fh + 4f;
        }
        int per = Mathf.Max(1, (int)((half + 4f) / (GeneS + 4f)));
        for (int i = 0; draw && i < h.genes.Count; i++)
        {
            Rect gr = new(x1 + i % per * (GeneS + 4f), ry + i / per * (GeneS + 4f), GeneS, GeneS);
            TemplateEditor.GeneIcon(gr, h.genes[i]);
            TemplateEditor.GeneTip(gr, h.genes[i]);
        }
        ry += (h.genes.Count + per - 1) / per * (GeneS + 4f);
        GUI.color = Color.white;
        return 3f * CardPad + top + Mathf.Max(rows * StackH, ry - y0 - 4f);
    }

    void Person(Rect r, Pattern p, float mass, float value)
    {
        bool bin = GameComponent_RM.Get.Trashed(p), can = !bin && Rules.CanPrint(p);
        Widgets.DrawOptionBackground(r, sel == p);
        GUI.color = can || bin ? Color.white : new Color(1f, 1f, 1f, 0.35f);
        Body(r, p, can, mass, value, true);
        // справа вверху (2026-10-21): «Переименовать», «Копировать», «Вставить», «Удалить»; в корзине — вернуть и «Удалить» навсегда
        // (с вопросом), у «Репликанта» только вернуть. Название значка — подсказкой.
        // Значки — до клика по карточке: свой клик они забирают.
        Human h = p.human;
        Rect ic = Corner(r);
        RMUI.Tip(ic, bin ? "RM_Restore" : "Delete");
        BinIcon(ic, p.Key, p.Label, bin);
        bool Next(Texture2D tex, string tip, bool active)
        {
            ic.x -= DelS + 4f;
            RMUI.Tip(ic, tip);
            return ITab_RMBills.Icon(ic, tex, active);
        }
        if (bin)
        {
            if (!h.blank && Next(TexButton.Delete, "Delete", true))
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(RMUI.Tr("RM_DeleteForever", p.Label), () =>
                {
                    Edit.DeleteHuman(h.id);
                    SoundDefOf.Click.PlayOneShotOnCamera();
                }, true));
            if (Widgets.ButtonInvisible(r)) Restore(p.Key);
            return;
        }
        if (Next(TexButton.Paste, "Paste", can && personClip != null))
        {
            if (sel != p) Pick(p);
            editor.Paste(personClip);
            SoundDefOf.Tick_Low.PlayOneShotOnCamera();
        }
        if (Next(TexButton.Copy, "Copy", true))
        {
            personClip = h.Encode();
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
        }
        // открытый черновик шаблона берёт новое название сразу
        if (Next(TexButton.Rename, "RM_Rename", true))
            Find.WindowStack.Add(new Dialog_RMRename(h.RenamableLabel, n =>
            {
                Edit.RenameHuman(h.id, n);
                if (editor?.Saved == h) editor.h.RenamableLabel = n;
            }));
        if (can && Widgets.ButtonInvisible(r))
        {
            Pick(p);
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
        }
    }

    /// «Запрещено», «не изучено» или «хватит на N».
    string Status(Pattern p, bool can, float mass, float value) =>
        !Rules.Allowed(p) ? "RM_StForbidden".Translate().ToString() : !can ? "RM_StNoResearch".Translate().ToString() : RMUI.Tr("RM_Enough", Enough(p, mass, value));

    /// TileH — карточка без картинок и названия: «хватит на N», «уже в заданиях», поля; BinTileH — в корзине, без этих строк.
    /// Mini — картинка механоида у воскрешения.
    const float TileMinW = 136f, TileH = 8f + 4f + 2f * 22f + 6f, BinTileH = 8f + 4f + 8f, Mini = 28f, MiniGap = 2f;

    static int PerRow(float tw) => Mathf.Max(1, (int)((tw - 8f + MiniGap) / (Mini + MiniGap)));

    /// Высота картинок: одна 48, у воскрешения — все механоиды рядами по 28 под значком «Удалить» (ряд — во всю ширину карточки).
    static float IconH(Pattern p, float tw) => !p.resurrect ? 48f : DelS + Mathf.Ceil(RMUI.Mechs(p).Count / (float)PerRow(tw)) * (Mini + MiniGap) - MiniGap;

    /// Карточка: картинка (у воскрешения — все его механоиды), название (в несколько строк, если не влезло), «хватит на N», «уже в заданиях».
    /// Печатать нельзя — полупрозрачная, ниже красным причина («не изучено», «запрещено»), клик ничего не делает. ih — высота картинок раздела.
    /// В корзине — без строк под названием.
    void Tile(Rect r, Pattern p, float ih, float mass, float value)
    {
        bool bin = GameComponent_RM.Get.Trashed(p), can = !bin && Rules.CanPrint(p);
        Widgets.DrawOptionBackground(r, sel == p);
        GUI.color = can || bin ? Color.white : new Color(1f, 1f, 1f, 0.35f);
        float top = r.y + 8f + (ih - IconH(p, r.width)) / 2f;
        if (!p.resurrect) RMUI.Icon(new Rect(r.center.x - 24f, top, 48f, 48f), p);
        else RMUI.MechGrid(r.center.x, top + DelS, RMUI.Mechs(p), PerRow(r.width), Mini, MiniGap);
        Text.Anchor = TextAnchor.UpperCenter;
        float ly = r.y + 8f + ih + 4f, lh = r.height - (bin ? BinTileH : TileH) - ih;
        RMUI.Label(new Rect(r.x + 4f, ly, r.width - 8f, lh), p.Label);
        Text.Anchor = TextAnchor.MiddleCenter;
        float y = ly + lh;
        if (!bin)
        {
            GUI.color = can ? RMUI.Muted : ColorLibrary.RedReadable;
            RMUI.Label(new Rect(r.x + 4f, y, r.width - 8f, 22f), Status(p, can, mass, value).Truncate(r.width - 8f));
        }
        if (InBill(p))
        {
            GUI.color = RMUI.Blue;
            RMUI.Label(new Rect(r.x + 4f, y + 22f, r.width - 8f, 22f), "RM_InBill".Translate().ToString().Truncate(r.width - 8f));
        }
        GUI.color = Color.white;
        Text.Anchor = TextAnchor.UpperLeft;
        BinIcon(Corner(r), p.Key, p.Label, bin);
        if (bin && Widgets.ButtonInvisible(r)) Restore(p.Key);
        if (can && Widgets.ButtonInvisible(r))
        {
            Pick(p);
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
        }
    }

    const float DelS = 24f;

    static void Restore(string key)
    {
        Edit.Restore(key);
        SoundDefOf.Tick_High.PlayOneShotOnCamera();
    }

    /// Значок «Удалить» / вернуть — справа вверху каждой карточки (2026-10-21): у черты над названием своя полоса, у гена — рядом
    /// со значком гена.
    static Rect Corner(Rect card) => new(card.xMax - 4f - DelS, card.y + 4f, DelS, DelS);

    /// «Удалить» — в корзину (2026-10-21: всё, «Репликант» тоже): есть задания по шаблону — сначала вопрос, они удалятся; про начатое
    /// (вернётся в баки) — только если есть оплаченная пачка (2026-10-22). В корзине вместо него круглая стрелка, как сброс ванили, —
    /// вернуть; возвращает и клик по всей карточке (2026-10-21).
    /// Значок — до клика по карточке: свой клик он забирает.
    static void BinIcon(Rect icon, string key, string label, bool bin)
    {
        if (bin)
        {
            // стрелка — во всю текстуру, у креста «Удалить» поля по 4 из 64: стрелка того же видимого размера (2026-10-21)
            if (ITab_RMBills.Icon(icon.ContractedBy(DelS * 4f / 64f), TemplateEditor.ResetTex.Texture, true)) Restore(key);
            return;
        }
        if (!ITab_RMBills.Icon(icon, TexButton.Delete, true)) return;
        void Go()
        {
            Edit.Trash(key);
            SoundDefOf.Click.PlayOneShotOnCamera();
        }
        List<Order> bills = CompPrinter.All.SelectMany(c => c.orders).Where(o => o.pattern?.Key == key).ToList();
        if (bills.Count > 0)
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(RMUI.Tr(bills.Any(o => o.paid) ? "RM_TrashConfirmPaid" : "RM_TrashConfirm", label), Go, true));
        else Go();
    }

    /// «Хватит на N» — по тому, чего не хватит первым.
    string Enough(Pattern p, float mass, float value)
    {
        var o = Order.Default(p, printer);
        float m = o.UnitMass, v = o.UnitValue;
        float n = Mathf.Min(m > 0f ? mass / m : float.MaxValue, v > 0f ? value / v : float.MaxValue);
        return n >= 99999f ? "∞" : Fmt.Num(Mathf.Floor(n));
    }
}

/// Окно задания (⚙), как у задания ванили. Правится черновик: «Применить» (вместо «Приостановить», пока есть правка) отдаёт его заданию
/// и закрывает окно. Копировать задание в буфер — за «Переименовать»: вставляется во вкладке любого репликатора.
public class Dialog_Order : Window
{
    readonly CompPrinter c;
    readonly Order live;
    readonly OrderForm form;

    public override Vector2 InitialSize => new(640f, Mathf.Min(760f, UI.screenHeight));

    public Dialog_Order(CompPrinter c, Order o)
    {
        this.c = c;
        live = o;
        // «Копировать» — за «Переименовать», «Справка» — справа вверху; что в баках — второй строкой, правым краем (2026-10-20)
        form = new OrderForm(c, o.Clone())
        {
            live = o, applied = () => Close(), copy = () => ITab_RMBills.Copy(new[] { form.o }), head = r => RMUI.HelpButton(r, form.HelpText),
            row2 = r => RMUI.TanksTitle(r, c.Net),
        };
        if (o.pattern.IsHuman)
            form.editTemplate = () =>
            {
                Close();
                Find.WindowStack.Add(new Dialog_AddBill(c, o.pattern));
            };
        forcePause = true;
        doCloseX = true;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = true;
    }

    public override void DoWindowContents(Rect inRect)
    {
        if (!c.parent.Spawned || !c.orders.Contains(live))
        {
            Close();
            return;
        }
        form.Draw(inRect, b =>
        {
            if (RMUI.Button(b, (live.paused ? "Suspended" : "NotSuspended").Translate())) Edit.Pause(c, live, !live.paused);
        });
    }
}

/// Настройка задания — в окне задания и справа в «Добавить задание». Правится черновик o; live — задание в очереди с тем же шаблоном:
/// сверху его статус, правку ему отдаёт «Применить»; нет задания — «Добавить задание». Задание поменялось само («сделать X» досчитало) —
/// черновик без правки берёт его настройки, с правкой — только количество, если его не трогали.
/// Строки — подпись, поле, число справа — растянуты на всю высоту до кнопок; поля одной ширины, столбцом (справа от них — числа);
/// внизу по краям две кнопки: «Сбросить» и действие. applied — после «Применить» (окно задания закрывается).
public class OrderForm
{
    readonly CompPrinter c;
    public Order o, live;
    /// applied — после «Применить»; beforeCommit — перед «Применить» (сохранить шаблон из конструктора);
    /// editTemplate — строка «Шаблон» с «Изменить» (окно задания: конструктор — в «Добавить задание»).
    public Action applied, beforeCommit, editTemplate;
    /// commitRestarts — beforeCommit начнёт заново начатую печать (сохранит шаблон с правкой): спросить вместе с правкой задания;
    /// extraChanged — правка не в задании (шаблон в конструкторе): справа «Применить» и при ней одной, и без задания в очереди;
    /// extraEdited и extraReset — шаблон не как был и откатить его: «Сбросить» задания — общая, как «Применить» (2026-10-21).
    public Func<bool> commitRestarts, extraChanged, extraEdited;
    public Action extraReset;
    /// Окно задания: copy — значок «Копировать» за «Переименовать»; head — справа в строке названия (справка), отдаёт занятую ширину;
    /// row2 — вторая строка (баки).
    public Action copy;
    public Func<Rect, float> head;
    public Action<Rect> row2;
    Order snap;
    string qtyBuf;
    int seen = -1;

    const float Gap = 12f;

    public OrderForm(CompPrinter c, Order draft)
    {
        this.c = c;
        o = draft;
    }

    void Follow()
    {
        if (live == null || snap != null && live.SameSettings(snap)) return;
        if (snap == null || o.SameSettings(snap)) o.ApplySettings(live);
        else if (o.target == snap.target) o.target = live.target;
        snap = live.Clone();
    }

    int Count() => o.mode == OrderMode.Forever ? 1 : o.target;
    (float, float, float) Plan() => (Count() * o.UnitTicks, Count() * o.UnitMass, Count() * o.UnitValue);

    /// Строк с полями и ползунками.
    int Rows(Pattern p) => 3 + (o.mode != OrderMode.Forever ? 1 : 0) + (p.HasQuality ? 1 : 0) + (o.CanSurplus ? 1 : 0) + (p.IsMech ? 2 : 0) + (p.resurrect ? 1 : 0)
                           + (o.Gendered ? 1 : 0) + (p.IsHuman && editTemplate != null ? 1 : 0);

    /// Шапка до строк — сведения сверху (2026-10-18): картинка с названием, «за штуку», кто печатает и с какой скоростью, статус задания
    /// в очереди, черта.
    float Top(float w, string speed) => 44f + (row2 != null ? Row2H : 0f) + 26f + (speed.Length > 0 ? Text.CalcHeight(speed, w) + 2f : 0f) + (StatusLine ? 26f : 0f) + 12f;

    /// Статус задания в очереди; нехватку баков не пишет — её говорит «План» ниже (2026-10-21).
    bool StatusLine => live != null && (live.running || live.status is not ("RM_StLowMass" or "RM_StLowSilver"));

    const float Row2H = 38f;

    /// Под строками — «План»; печать уже идёт — её оплаченная пачка в доступном.
    float PlanFrame(Rect r, bool draw = true) =>
        RMUI.Plan(r, Plan(), c.Net, live is { paid: true } ? (live.paidMass, live.paidValue) : default, draw);

    float Tail(float w) => PlanFrame(new Rect(0f, 0f, w, 0f), false);

    string Speed(Pattern p)
    {
        var (speed, bad) = SpeedLine(p);
        // у задания в очереди нет оператора или линка — это уже в статусе сверху
        return bad && live != null ? "" : speed;
    }

    /// Высота без растяжки строк (по 36) — у биорепликатора под заданием конструктор.
    public float Height(float w)
    {
        Pattern p = o.pattern;
        return Top(w, Speed(p)) + Rows(p) * 36f + Tail(w) + RMUI.Gap + 36f;
    }

    /// Справка задания — по кнопке в шапке (RMUI.HelpButton): правка начатого — печать заново, излишки, воскрешение, ползунок массы — абзацами.
    public string HelpText => Help(o.pattern);

    /// idle — правая кнопка у задания в очереди без правки.
    public void Draw(Rect r, Action<Rect> idle)
    {
        Follow();
        Pattern p = o.pattern;
        float labelW = Mathf.Round(r.width * 0.3f), valW = Mathf.Round(r.width * 0.28f);
        RMUI.Icon(new Rect(r.x, r.y, 40f, 40f), p);
        float hw = head?.Invoke(new Rect(r.x, r.y + 4f, r.width, 34f)) ?? 0f;
        float rx = RMUI.TitleRename(new Rect(r.x + 52f, r.y + 2f, r.width - 52f - hw - (copy != null ? 32f : 0f), 34f), o, o.Renamable ? o.SetName : null);
        if (copy != null && ITab_RMBills.Icon(new Rect(rx + 8f, r.y + 7f, 24f, 24f), TexButton.Copy, true)) copy();
        float y = r.y + 44f;
        if (row2 != null)
        {
            row2(new Rect(r.x, y, r.width, 30f));
            y += Row2H;
        }
        GUI.color = RMUI.Muted;
        // человек дешевле «Репликанта» не бывает: на этом пороге — «мин. стоимость» (2026-10-20)
        RMUI.Label(new Rect(r.x, y, r.width, 24f), RMUI.Tr(p.human?.AtFloor == true ? "RM_PerUnitMin" : "RM_PerUnit", Fmt.Mass(o.UnitMass), Fmt.Silver(o.UnitValue),
            Fmt.Time(o.UnitTicks)));
        y += 26f;
        bool bad = SpeedLine(p).bad;
        string speed = Speed(p);
        if (speed.Length > 0)
        {
            GUI.color = bad ? ColorLibrary.RedReadable : RMUI.Muted;
            float sh = Text.CalcHeight(speed, r.width);
            RMUI.Label(new Rect(r.x, y, r.width, sh), speed);
            y += sh + 2f;
        }
        if (StatusLine)
        {
            var (status, color) = RMUI.Status(c, live);
            GUI.color = color;
            RMUI.Label(new Rect(r.x, y, r.width, 24f), status);
            y += 26f;
        }
        GUI.color = Color.white;
        Widgets.DrawLineHorizontal(r.x, y + 4f, r.width, Widgets.SeparatorLineColor);
        y += 12f;

        // снизу вверх: кнопки, «План»; что осталось — строкам
        Rect btns = new(r.x, r.yMax - 36f, r.width, 36f);
        float bottom = btns.y - RMUI.Gap;
        float tail = Tail(r.width);
        float free = bottom - y - tail, pitch = Mathf.Clamp(free / Rows(p), 36f, 60f), h = Mathf.Min(pitch - 6f, 40f);

        // поле — между подписью и столбцом чисел, у всех строк одной ширины (как полоса ползунка)
        float fieldW = r.width - labelW - valW - Gap;
        Rect Field(string key)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            RMUI.Label(new Rect(r.x, y, labelW, pitch), key.Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            Rect f = new(r.x + labelW, y + (pitch - h) / 2f, fieldW, h);
            y += pitch;
            return f;
        }
        if (Widgets.ButtonText(Field("RM_EdMode"), RMUI.ModeLabel(o.mode).CapitalizeFirst()))
            Find.WindowStack.Add(RMUI.ModeMenu(p, m => o.mode = m));
        if (o.mode != OrderMode.Forever)
        {
            Rect f = Field("RM_EdCount");
            if (o.target != seen) qtyBuf = null;
            RMUI.CountEntry(f, ref o.target, ref qtyBuf, o.Stack > 1 ? o.Stack : 10);
            // кнопки −10/−1 ванили уводят в минус — не меньше нуля (0 — задание на паузе) и не больше предела ванили, и в поле тоже
            int t = Mathf.Clamp(o.target, 0, Order.MaxTarget);
            if (t != o.target) (o.target, qtyBuf) = (t, t.ToString());
            seen = o.target;
            if (o.mode == OrderMode.Until)
                Value(new Rect(r.xMax - valW, f.y, valW, h), RMUI.Tr(p.IsPawn ? "RM_OnMap" : "RM_InStock", c.Stock(o)), RMUI.Yellow);
        }
        if (p.HasQuality)
        {
            string ql = o.manualQuality ? o.Quality.GetLabel().CapitalizeFirst() : "RM_QualityBest".Translate(o.Quality.GetLabel()).ToString();
            if (Widgets.ButtonText(Field("RM_EdQuality"), ql))
            {
                var opts = new List<FloatMenuOption> { new("RM_QualityBest".Translate(p.Best.GetLabel()), () => o.manualQuality = false) };
                opts.AddRange(p.qualities.OrderByDescending(q => q).Select(q => new FloatMenuOption(q.GetLabel().CapitalizeFirst(), () =>
                {
                    o.manualQuality = true;
                    o.quality = q;
                })));
                Find.WindowStack.Add(new FloatMenu(opts));
            }
        }
        if (o.CanSurplus)
        {
            // галка — у правого края полей
            Widgets.CheckboxLabeled(new Rect(r.x, y + (pitch - 24f) / 2f, labelW + fieldW, 24f), "RM_Surplus".Translate(), ref o.surplus);
            y += pitch;
        }
        if (p.IsMech) Mechanitor(Field("RM_EdMechanitor"), Field("ControlGroup"));
        if (p.resurrect) Corpses(Field("RM_EdMech"), p);
        // шаблон человека правится в конструкторе окна «Добавить задание»
        if (p.IsHuman && editTemplate != null && Widgets.ButtonText(Field("RM_EdTemplate"), "Edit".Translate())) editTemplate();
        if (o.Gendered)
            o.gender = RMUI.Choice(Field("Sex"), Gender.Female.GetLabel(true).CapitalizeFirst(), Gender.Male.GetLabel(true).CapitalizeFirst(),
                o.gender == Gender.Female) ? Gender.Female : Gender.Male;
        for (int i = 0; i < 2; i++)
        {
            Slider(new Rect(r.x, y, r.width, pitch), i, labelW, valW, h, Count());
            y += pitch;
        }
        PlanFrame(new Rect(r.x, y, r.width, 0f));

        const float bw = 180f;
        Rect left = new(r.x, btns.y, bw, 36f), right = new(r.xMax - bw, btns.y, bw, 36f);
        // «Сбросить» откатывает всю неприменённую правку: задание — к заданию в очереди (нет его — к умолчаниям), шаблон — как был
        Order back = live ?? Order.Default(p, c);
        if (RMUI.Button(left, "ResetButton".Translate(), extraEdited?.Invoke() == true || !o.SameSettings(back)))
        {
            o.ApplySettings(back);
            extraReset?.Invoke();
        }
        // правая кнопка — по состоянию (2026-10-21): правка шаблона или задания в очереди — «Применить»; без правки — «Добавить
        // задание», а задание уже в очереди — idle
        Order was = live;
        bool extra = extraChanged?.Invoke() == true;
        if (was == null && !extra)
        {
            if (!RMUI.Button(right, "AddBill".Translate())) return;
            Edit.Create(c, o.Clone());
            SoundDefOf.Tick_Low.PlayOneShotOnCamera();
        }
        else if (was == null || extra || !o.SameSettings(was))
        {
            if (!RMUI.Button(right, "RM_Apply".Translate())) return;
            Order draft = o.Clone();
            // печать уже идёт — всегда сначала вопрос (2026-10-21): начнётся заново или пойдёт дальше с новыми настройками
            bool restarts = commitRestarts?.Invoke() == true || was?.paid == true && !was.SamePrint(draft);
            RMUI.Confirm(restarts ? "RM_RestartConfirm" : was?.paid == true ? "RM_PrintOnConfirm" : null, () =>
            {
                beforeCommit?.Invoke();
                if (was != null && !draft.SameSettings(was)) Edit.Apply(c, was, draft);
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                applied?.Invoke();
            });
        }
        else idle(right);
    }

    string Help(Pattern p) => string.Join("\n\n", new[]
    {
        "RM_RestartNote".Translate().ToString(),
        !o.CanSurplus ? null : Building_RMHopper.Spawned_.Any(x => x.Map == c.parent.Map)
            ? RMUI.Tr("RM_SurplusNote", RMUI.Tr("RM_SurplusBill", p.Label.UncapitalizeFirst(), o.target)) : "RM_SurplusNoHopper".Translate().ToString(),
        p.resurrect ? "RM_ResurrectHelp".Translate().ToString() : null,
        o.MassSlider ? "RM_SliderMassTip".Translate().ToString() : null,
    }.Where(x => x != null));

    /// Число справа от поля, по правому краю.
    static void Value(Rect r, string text, Color color)
    {
        GUI.color = color;
        Text.Anchor = TextAnchor.MiddleRight;
        RMUI.Label(r, text.Truncate(r.width));
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
    }

    /// Механитор — к кому подключится механоид, рядом его пропускная способность; группа — из его групп управления.
    void Mechanitor(Rect mf, Rect gf)
    {
        string Name(Pawn m) => m == null ? "RM_None".Translate().ToString()
            : m.mechanitor == null ? m.LabelShort : m.LabelShort + " (" + m.mechanitor.UsedBandwidth + " / " + m.mechanitor.TotalBandwidth + ")";
        if (Widgets.ButtonText(mf, Name(o.mechanitor).Truncate(mf.width - 12f)))
        {
            List<FloatMenuOption> opts = c.Mechanitors().Select(m => new FloatMenuOption(Name(m), () => o.mechanitor = m)).ToList();
            if (opts.Count == 0) opts.Add(new FloatMenuOption("RM_NoMechanitors".Translate(), null));
            Find.WindowStack.Add(new FloatMenu(opts));
        }
        int groups = Mathf.Max(1, o.mechanitor?.mechanitor?.controlGroups.Count ?? 1);
        if (Widgets.ButtonText(gf, "ControlGroup".Translate() + " " + Mathf.Clamp(o.group, 1, groups)))
            Find.WindowStack.Add(new FloatMenu(Enumerable.Range(1, groups)
                .Select(g => new FloatMenuOption("ControlGroup".Translate() + " " + g, () => o.group = g)).ToList()));
    }

    /// Какого механоида воскрешать — список, как выбор механоида у ванили: любой механоид рецепта или один, с картинкой.
    /// Несколько из старого сохранения — через запятую.
    void Corpses(Rect f, Pattern p)
    {
        List<ThingDef> all = p.recipe.fixedIngredientFilter.AllowedThingDefs.OrderBy(d => d.ingestible.sourceDef.label).ToList();
        List<ThingDef> on = all.Where(d => o.corpses?.Allows(d) != false).ToList();
        string label = on.Count == all.Count ? "AnyMech".Translate().ToString() : string.Join(", ", on.Select(d => d.ingestible.sourceDef.LabelCap.ToString()));
        if (!Widgets.ButtonText(f, label.Truncate(f.width - 12f))) return;
        var opts = new List<FloatMenuOption> { new("AnyMech".Translate(), () => o.corpses = Order.AllCorpses(p)) };
        opts.AddRange(all.Select(d => new FloatMenuOption(d.ingestible.sourceDef.LabelCap, () => o.CorpseKey = d.defName, d.ingestible.sourceDef)));
        Find.WindowStack.Add(new FloatMenu(opts));
    }

    /// Чем печатается: линк оператора и скорость; нет оператора или линка — красным.
    (string text, bool bad) SpeedLine(Pattern p)
    {
        Pawn op = c.Operator;
        Link l = Links.Of(p);
        if (op == null) return ("RM_StNoOperatorFull".Translate(), true);
        if (!Links.Has(op, l)) return (RMUI.Tr("RM_StNoLinkFull", Links.Def(l).label), true);
        return (RMUI.Tr("RM_SpeedLevel", op.LabelShort, Links.Def(l).label, Links.Level(op, l), Links.Pct(Links.Speed(op, l))), false);
    }

    string SliderKey(int i) => i == 0 ? "RM_SliderTime" : o.MassSlider ? "RM_SliderMass" : "RM_SliderValue";

    /// Два связанных ползунка (время, стоимость; у шаблона без стоимости — время, масса), шаг 5%, отметки через 10 без цифр. Вправо — параметра больше,
    /// влево — меньше; другой зеркально. Справа — только что выйдет за n штук, без скобок с разницей (2026-10-21); цвет один — белый
    /// (2026-10-20: красный — только когда чего-то не хватает).
    void Slider(Rect r, int i, float labelW, float valW, float h, int n)
    {
        int cap = Order.Cap, v = o.SliderValue(i);
        // полоса — там же, где поля над ней; рельс ванили — на 6 ниже верха полосы: по центру строки, как подпись и число
        Text.Anchor = TextAnchor.MiddleLeft;
        RMUI.Label(new Rect(r.x, r.y, labelW, r.height), SliderKey(i).Translate());
        Text.Anchor = TextAnchor.UpperLeft;
        Rect bar = new(r.x + labelW, r.y + r.height / 2f - 6f, r.width - labelW - valW - Gap, 18f);
        int nv = -Mathf.RoundToInt(Widgets.HorizontalSlider(bar, -v, -cap, cap, roundTo: 5f));
        if (nv != v) o.SetSlider(i, nv);
        Func<float, string> fmt = i == 0 ? Fmt.Time : o.MassSlider ? Fmt.Mass : Fmt.Silver;
        float total = n * (i == 0 ? o.UnitTicks : o.MassSlider ? o.UnitMass : o.UnitValue);
        Value(new Rect(r.xMax - valW, r.y + (r.height - h) / 2f, valW, h), n == 0 ? "—" : fmt(total), Color.white);
        GUI.color = RMUI.Muted;
        for (int t = -(cap / 10) * 10; t <= cap; t += 10)
            Widgets.DrawLineVertical(Mathf.Lerp(bar.x + 6f, bar.xMax - 6f, (t + cap) / (2f * cap)), bar.yMax, 4f);
        GUI.color = Color.white;
    }
}
