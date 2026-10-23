using System.Collections.Generic;
using System.Linq;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ReplimatMech;

/// Правки игрока в заданиях и компьютере — только отсюда. В Multiplayer каждая уходит командой и выполняется у всех игроков в один тик,
/// в одиночной игре — сразу. После правки — статусы заданий сети и задания излишков в расщепителях, как в тике.
[StaticConstructorOnStartup]
public static class Edit
{
    static Edit()
    {
        if (MP.enabled) MP.RegisterAll();
    }

    /// Порядок репликаторов — по номеру вещи: у всех игроков один, кто первым оплатит пачку из общих баков.
    static void Done(CompPrinter c)
    {
        foreach (CompPrinter x in CompPrinter.All.Where(x => x.Net == c.Net).OrderBy(x => x.parent.thingIDNumber)) x.Refresh();
        Building_RMHopper.SyncSurplus(c.parent.Map);
    }

    /// Новое задание (окно, вставка). На шаблон в репликаторе одно задание: уже есть — получает настройки нового.
    /// Воскрешений — сколько угодно: у каждого свой выбор механоидов.
    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void Create(CompPrinter c, Order draft)
    {
        // задание из конструктора — на сохранённый шаблон с тем же номером (в Multiplayer это уже сделал SyncOrder); удалили — задания нет
        if (draft.pattern is { IsHuman: true }) draft.pattern = GameComponent_RM.Get.ByKey(draft.pattern.Key);
        // шаблон в корзине не печатается (вставка из буфера, корзину сменил другой игрок)
        if (draft.pattern == null || GameComponent_RM.Get.Trashed(draft.pattern)) return;
        if (c.Same(draft.pattern) is { } o) Set(c, o, draft);
        else
        {
            draft.paused = draft.target == 0;
            c.Add(draft);
        }
        Done(c);
    }

    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void Apply(CompPrinter c, Order o, Order draft)
    {
        Set(c, o, draft);
        Done(c);
    }

    /// Настройки задания. Сменили механитора — к новому идёт и напечатанный механоид задания, который ждёт старого.
    static void Set(CompPrinter c, Order o, Order draft)
    {
        // правка печати — начатая пачка в баки, печать с нуля
        if (!o.SamePrint(draft)) c.Restart(o);
        Pawn old = o.mechanitor;
        o.ApplySettings(draft);
        if (o.pattern?.mechKind != null && o.mechanitor != null && o.mechanitor != old)
            foreach (WaitingMech w in c.waiting.Where(w => w.kind == o.pattern.mechKind && w.mechanitor == old)) w.mechanitor = o.mechanitor;
    }

    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void Pause(CompPrinter c, Order o, bool paused)
    {
        o.paused = paused;
        Done(c);
    }

    /// Режим разработчика: начатая пачка готова — выходит на следующем тике репликатора.
    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void FinishPrint(CompPrinter c, Order o) => o.progress = o.batchTicks;

    /// Удаление задания; механоид по нему уже напечатан и ждёт механитора — отменяется с возвратом в баки:
    /// задания нет — нет и пропускной способности под него. Осталось другое задание на того же механоида — ждущий его.
    /// Труп, который больше никому не нужен, выкладывает Refresh.
    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void Remove(CompPrinter c, Order o)
    {
        Delete(c, o);
        Done(c);
    }

    static void Delete(CompPrinter c, Order o)
    {
        c.Remove(o);
        PawnKindDef kind = o.pattern?.mechKind;
        if (kind != null && !c.orders.Any(x => x.pattern?.mechKind == kind))
            for (int i = c.waiting.Count - 1; i >= 0; i--)
                if (c.waiting[i].kind == kind) c.CancelWaiting(i);
    }

    /// Перенос в другой репликатор сети (кнопка «Репликатор» в строке задания): встаёт в конец его очереди,
    /// а если там уже есть задание на этот шаблон — оно получает настройки переносимого. Здесь задание удаляется, как кнопкой.
    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void Transfer(CompPrinter c, Order o, CompPrinter to)
    {
        if (to == c || to.Net != c.Net || o.pattern == null || !to.Accepts(o.pattern)) return;
        Order moved = o.Clone();
        Delete(c, o);
        if (to.Same(moved.pattern) is { } same) Set(to, same, moved);
        else to.Add(moved);
        Done(c);
    }

    /// Имя задания из строки очереди; пустое — снова название шаблона.
    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void Rename(CompPrinter c, Order o, string name) => o.RenamableLabel = name;

    /// − / + в строке задания, как у верстака ванили: «всегда» становится «1 раз», иначе количество ± step (Shift ×10, Ctrl ×100 — у нажавшего).
    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void Count(CompPrinter c, Order o, int step)
    {
        if (o.mode == OrderMode.Forever)
        {
            o.mode = OrderMode.Make;
            o.SetTarget(1);
        }
        else o.SetTarget(o.target + step);
        Done(c);
    }

    /// Режим из строки задания — кнопкой со списком, как у верстака ванили.
    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void Mode(CompPrinter c, Order o, OrderMode m)
    {
        o.mode = m;
        Done(c);
    }

    /// Перетаскивание задания: с места from на место to.
    [SyncMethod]
    public static void Reorder(CompPrinter c, int from, int to)
    {
        Move(c.orders, from, to);
        Done(c);
    }

    static void Move<T>(List<T> list, int from, int to)
    {
        if (from < 0 || from >= list.Count) return;
        T moved = list[from];
        if (to > from) to--;
        list.RemoveAt(from);
        list.Insert(Mathf.Clamp(to, 0, list.Count), moved);
    }

    // ---- расщепитель: задание — по номеру ванили (Bill_N), порядок в списке может смениться ----

    static Bill Split(Building_RMHopper h, string id) => h.billStack.Bills.FirstOrDefault(b => b.GetUniqueLoadID() == id);

    /// Новое задание (окно, вставка) — после заданий игрока, перед «Излишками»: их место в конце, пока игрок сам их не перетащил.
    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void AddSplit(Building_RMHopper h, SplitDraft d)
    {
        var b = (Bill_Production)RMDefOf.RM_Split.MakeNewBill();
        d.ApplyTo(b);
        b.billStack = h.billStack;
        h.billStack.Bills.Insert(h.billStack.Bills.FindLastIndex(x => x is not Bill_RMSurplus) + 1, b);
    }

    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void ApplySplit(Building_RMHopper h, string id, SplitDraft d)
    {
        if (Split(h, id) is Bill_Production b) d.ApplyTo(b);
    }

    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void RenameSplit(Building_RMHopper h, string id, string name)
    {
        if (Split(h, id) is Bill_Production b) b.RenamableLabel = name.NullOrEmpty() || name == b.BaseLabel ? null : name;
    }

    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void PauseSplit(Building_RMHopper h, string id, bool paused)
    {
        if (Split(h, id) is { } b) b.suspended = paused;
    }

    /// Удалить; у «Излишков» снимается и галка в задании репликатора (Building_RMHopper.Notify_BillDeleted).
    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void DeleteSplit(Building_RMHopper h, string id)
    {
        if (Split(h, id) is { } b) h.billStack.Delete(b);
    }

    /// − / + в строке задания расщепителя, как у верстака ванили (повторять до — у него нет).
    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void CountSplit(Building_RMHopper h, string id, int step)
    {
        if (Split(h, id) is not Bill_Production b) return;
        if (b.repeatMode == BillRepeatModeDefOf.Forever) (b.repeatMode, b.repeatCount) = (BillRepeatModeDefOf.RepeatCount, 1);
        else b.repeatCount = Mathf.Max(0, b.repeatCount + step);
    }

    /// Режим задания расщепителя из строки: «бесконечно» или «сделать X».
    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void ModeSplit(Building_RMHopper h, string id, bool forever)
    {
        if (Split(h, id) is Bill_Production b) b.repeatMode = forever ? BillRepeatModeDefOf.Forever : BillRepeatModeDefOf.RepeatCount;
    }

    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void ReorderSplit(Building_RMHopper h, int from, int to)
    {
        Move(h.billStack.Bills, from, to);
    }

    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void CollectValue(Building_RMComputer computer, bool on) => computer.collectValue = on;

    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void SelectScan(Building_RMBioscanner s, Pawn p) => s.Select(p);

    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void CancelScan(Building_RMBioscanner s) => s.Cancel();

    /// Правый клик колонистом по лежачему животному: выбрать его в сканере (где уже ждёт или ближний свободный), колонист сразу несёт.
    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void CarryToScan(Pawn carrier, Pawn p)
    {
        if (Building_RMBioscanner.For(p) is not { } s) return;
        s.Select(p);
        if (s.SelectedPawn != p) return;
        Job job = JobMaker.MakeJob(JobDefOf.CarryToBuilding, s, p);
        job.count = 1;
        carrier.jobs.TryTakeOrderedJob(job, JobTag.Misc);
    }

    /// Режим разработчика: скан готов на следующем тике сканера.
    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void FinishScan(Building_RMBioscanner s) => s.ticksLeft = 1;

    /// Шаблон человека из конструктора. Задания печатают уже новый: начатая пачка по нему — в баки, печать с нуля (кроме смены названия).
    [SyncMethod]
    public static void SaveHuman(int id, string data)
    {
        if (GameComponent_RM.Get.humans.FirstOrDefault(h => h.id == id) is { } was && !Human.SameBody(was.Encode(), data))
            foreach (CompPrinter c in CompPrinter.All.OrderBy(c => c.parent.thingIDNumber).ToList())
                foreach (Order o in c.orders.Where(o => o.pattern?.human == was).ToList()) c.Restart(o);
        GameComponent_RM.Get.SaveHuman(id, data);
        RefreshBio();
    }

    /// Глобальная «Вставить» у «Людей» (2026-10-21): новый шаблон — копия человека, номер — следующий свободный, название «Шаблон N».
    [SyncMethod]
    public static void AddHuman(string data)
    {
        GameComponent_RM gc = GameComponent_RM.Get;
        gc.SaveHuman(gc.NextHuman, Human.Body(data));
        RefreshBio();
    }

    /// Название сохранённого шаблона — сразу, без «Сохранить» (2026-10-21); печать заново не начинается: название не печатается.
    [SyncMethod]
    public static void RenameHuman(int id, string name)
    {
        if (GameComponent_RM.Get.humans.FirstOrDefault(h => h.id == id) is { } h) h.RenamableLabel = name;
    }

    /// В корзину (2026-10-18, 2026-10-21) — шаблон, воскрешение, черта или ген базы людей: задания по шаблону
    /// удаляются (начатая пачка — в баки); шаблоны людей с чертой или геном из корзины их сохраняют.
    [SyncMethod]
    public static void Trash(string key)
    {
        GameComponent_RM gc = GameComponent_RM.Get;
        if (!gc.Trashable(key) || gc.trash.Contains(key)) return;
        RemoveBills(key);
        gc.trash.Add(key);
    }

    [SyncMethod]
    public static void Restore(string key) => GameComponent_RM.Get.trash.Remove(key);

    /// Из корзины навсегда (2026-10-21: карточки людей копятся без конца) — шаблон человека, не «Репликант»; задания по нему
    /// удалены ещё в корзину, на всякий случай — снова.
    [SyncMethod]
    public static void DeleteHuman(int id)
    {
        GameComponent_RM gc = GameComponent_RM.Get;
        if (gc.humans.FirstOrDefault(h => h.id == id) is not { blank: false } h || !gc.Trashed(gc.HumanPattern(h))) return;
        RemoveBills(gc.HumanPattern(h).Key);
        gc.DeleteHuman(h);
    }

    /// Задания по шаблону — во всех репликаторах, как кнопкой «Удалить».
    static void RemoveBills(string key)
    {
        foreach (CompPrinter c in CompPrinter.All.OrderBy(c => c.parent.thingIDNumber).ToList())
        {
            List<Order> gone = c.orders.Where(o => o.pattern?.Key == key).ToList();
            foreach (Order o in gone) Delete(c, o);
            if (gone.Count > 0) Done(c);
        }
    }

    static void RefreshBio()
    {
        foreach (CompPrinter c in CompPrinter.All.Where(c => c.Incubator).OrderBy(c => c.parent.thingIDNumber).ToList()) Done(c);
    }

    /// Задание из очереди — репликатор и номер; черновик (буфер обмена, новое задание) — его настройки.
    [SyncWorker]
    static void SyncOrder(SyncWorker sync, ref Order o)
    {
        CompPrinter c = sync.isWriting ? o.printer : null;
        bool queued = c != null && c.orders.Contains(o);
        sync.Bind(ref queued);
        if (queued)
        {
            int i = sync.isWriting ? c.orders.IndexOf(o) : 0;
            sync.Bind(ref c);
            sync.Bind(ref i);
            if (!sync.isWriting) o = c != null && i < c.orders.Count ? c.orders[i] : null;
            return;
        }
        o ??= new Order();
        string key = o.pattern?.Key;
        sync.Bind(ref key);
        o.pattern = GameComponent_RM.Get.ByKey(key);
        sync.Bind(ref o.name);
        sync.Bind(ref o.mode);
        sync.Bind(ref o.target);
        sync.Bind(ref o.manualQuality);
        sync.Bind(ref o.quality);
        sync.Bind(ref o.mechanitor);
        sync.Bind(ref o.group);
        sync.Bind(ref o.gender);
        sync.Bind(ref o.surplus);
        sync.Bind(ref o.slider);
        sync.Bind(ref o.pct);
        string corpses = o.CorpseKey;
        sync.Bind(ref corpses);
        o.CorpseKey = corpses;
    }

    /// Черновик задания расщепителя — настройками, фильтр строкой.
    [SyncWorker]
    static void SyncSplit(SyncWorker sync, ref SplitDraft d)
    {
        d ??= new SplitDraft();
        sync.Bind(ref d.name);
        sync.Bind(ref d.mode);
        sync.Bind(ref d.count);
        sync.Bind(ref d.suspended);
        sync.Bind(ref d.radius);
        sync.Bind(ref d.pawn);
        sync.Bind(ref d.only);
        string f = d.FilterKey;
        sync.Bind(ref f);
        if (!sync.isWriting) d.FilterKey = f;
    }
}
