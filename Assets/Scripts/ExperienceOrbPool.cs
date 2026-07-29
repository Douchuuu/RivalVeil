using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// ExperienceOrbPool — пул орбов опыта.
///
/// ИЗМЕНЕНИЯ:
///   • Добавлен отдельный пул для бонусных орбов (bonusOrbPrefab).
///     Бонусные орбы спавнятся при срабатывании Академичной Шляпы и имеют
///     другой цвет. Если bonusOrbPrefab == null — используется обычный префаб,
///     но IsBonus = true (цвет применяется через материал ExperienceOrb).
///
///   • Добавлен метод GetBonusOrb(Vector3) — получить бонусный орб.
///   • Добавлен метод AttractToPoint(Vector3, float) — для Чёрной Дыры:
///     притянуть все активные орбы в радиусе к указанной точке.
///
/// АРХИТЕКТУРА ПУЛА:
///   Один пул хранит ВСЕ орбы (обычные и бонусные).
///   Разделение только визуальное — через флаг IsBonus и цвет материала.
///   Два отдельных Queue для предсказуемого повторного использования префабов.
///
/// ФИКС ADDITIVE SCENE:
///   • БЫЛО: if (Instance != null) Destroy(gameObject) — в Competitive режиме
///     вторая PlayerWorldScene создавала ExperienceOrbPool, который немедленно
///     уничтожался! Второй игрок оставался без пула орбов.
///
///   • СТАЛО: Instance = this всегда. Каждая PlayerWorldScene имеет свой пул.
///     EnemyHealth.FindPoolInMyScene() использует сцено-ориентированный поиск,
///     поэтому конфликт Instance не страшен — каждый враг находит свой пул.
///     DontDestroyOnLoad убран ранее — пул уничтожается вместе со своей сценой.
/// </summary>
public class ExperienceOrbPool : MonoBehaviour
{
    public static ExperienceOrbPool Instance { get; private set; }

    [Header("Обычный орб")]
    [SerializeField] private GameObject orbPrefab;
    [SerializeField] private int initialPoolSize = 200;

    [Header("Бонусный орб (Академичная Шляпа)")]
    [Tooltip("Если null — используется обычный prefab с IsBonus=true")]
    [SerializeField] private GameObject bonusOrbPrefab;
    [SerializeField] private int initialBonusPoolSize = 50;

    [Tooltip("Максимум активных орбов на сцене. 0 = без ограничений")]
    [SerializeField] private int maxActiveOrbs = 0;

    // ── Пулы ─────────────────────────────────────────────────────────────────
    private Queue<GameObject> _pool = new Queue<GameObject>();
    private Queue<GameObject> _bonusPool = new Queue<GameObject>();

    // Список ВСЕХ активных орбов + кэш компонентов — один Update вместо 1000
    private List<GameObject> _activeOrbs = new List<GameObject>();
    private List<ExperienceOrb> _activeOrbScripts = new List<ExperienceOrb>();

    private int _activeCount = 0;

    // ─────────────────────────────────────────────────────────────────────────

    void Awake()
    {
        // ФИКС ADDITIVE SCENE:
        // В Additive Scene режиме каждая PlayerWorldScene загружается с ОТДЕЛЬНЫМ
        // физическим миром. Каждому игроку нужен СВОЙ ExperienceOrbPool в его сцене.
        //
        // БЫЛО (сломанное поведение):
        //   if (Instance == null) { Instance = this; InitializePool(); }
        //   else { Destroy(gameObject); }   ← второй пул УНИЧТОЖАЛСЯ немедленно!
        //   Игрок 2 оставался без орбов и получал NullReferenceException.
        //
        // СТАЛО (правильное поведение):
        //   Instance обновляется до последнего созданного пула.
        //   EnemyHealth.FindPoolInMyScene() выбирает нужный пул по принадлежности сцены,
        //   а не через глобальный Instance — поэтому конфликт Instance безопасен.
        //   DontDestroyOnLoad убран — пул уничтожается вместе со своей PlayerWorldScene.
        Instance = this;
        InitializePool();
    }

    void InitializePool()
    {
        for (int i = 0; i < initialPoolSize; i++)
        {
            var orb = CreateOrbObject(orbPrefab, false);
            orb.SetActive(false);
            _pool.Enqueue(orb);
        }

        // Бонусный пул: если отдельный prefab не назначен — клонируем обычный
        GameObject bonusPrefabToUse = bonusOrbPrefab != null ? bonusOrbPrefab : orbPrefab;
        for (int i = 0; i < initialBonusPoolSize; i++)
        {
            var orb = CreateOrbObject(bonusPrefabToUse, true);
            orb.SetActive(false);
            _bonusPool.Enqueue(orb);
        }

        Debug.Log($"✅ ExperienceOrbPool: {initialPoolSize} обычных + {initialBonusPoolSize} бонусных орбов " +
                  $"(сцена: {gameObject.scene.name})");
    }

    private GameObject CreateOrbObject(GameObject prefab, bool isBonus)
    {
        var orb = Instantiate(prefab);
        orb.name = isBonus ? "XP_Orb_Bonus" : "XP_Orb";
        var script = orb.GetComponent<ExperienceOrb>();
        if (script != null) script.IsBonus = isBonus;
        return orb;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОЛУЧЕНИЕ ОРБА
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Получить обычный орб опыта из пула.</summary>
    public GameObject GetOrb(Vector3 position, int xpAmount = 10, ulong ownerClientId = ulong.MaxValue)
    {
        return GetFromQueue(_pool, orbPrefab, false, position, xpAmount, ownerClientId);
    }

    /// <summary>Получить бонусный орб (Академичная Шляпа) из пула.</summary>
    public GameObject GetBonusOrb(Vector3 position, int xpAmount = 10, ulong ownerClientId = ulong.MaxValue)
    {
        GameObject bonusPrefabToUse = bonusOrbPrefab != null ? bonusOrbPrefab : orbPrefab;
        return GetFromQueue(_bonusPool, bonusPrefabToUse, true, position, xpAmount, ownerClientId);
    }

    private GameObject GetFromQueue(Queue<GameObject> queue, GameObject prefab, bool isBonus, Vector3 position, int xp, ulong ownerClientId = ulong.MaxValue)
    {
        if (maxActiveOrbs > 0 && _activeCount >= maxActiveOrbs) return null;

        // Извлекаем живой объект из очереди
        GameObject orb = null;
        while (queue.Count > 0)
        {
            var candidate = queue.Dequeue();
            if (candidate != null) { orb = candidate; break; }
        }

        // Если пул пуст — создаём новый
        if (orb == null)
            orb = CreateOrbObject(prefab, isBonus);

        // Настраиваем орб
        var script = orb.GetComponent<ExperienceOrb>();
        if (script != null) script.Setup(xp, isBonus, ownerClientId);

        orb.transform.position = position;
        orb.SetActive(true);
        _activeOrbs.Add(orb);
        var orbScript = orb.GetComponent<ExperienceOrb>();
        _activeOrbScripts.Add(orbScript);
        _activeCount++;
        return orb;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВОЗВРАТ ОРБА
    // ─────────────────────────────────────────────────────────────────────────

    public void ReturnOrb(GameObject orb)
    {
        if (orb == null) return;

        int idx = _activeOrbs.IndexOf(orb);
        if (idx >= 0)
        {
            // Swap-remove — O(1) вместо O(n) сдвига
            int last = _activeOrbs.Count - 1;
            _activeOrbs[idx] = _activeOrbs[last];
            _activeOrbScripts[idx] = _activeOrbScripts[last];
            _activeOrbs.RemoveAt(last);
            _activeOrbScripts.RemoveAt(last);
        }

        var script = orb.GetComponent<ExperienceOrb>();
        bool isBonus = script != null && script.IsBonus;

        if (script != null) script.ResetOrb();

        orb.SetActive(false);
        orb.transform.position = Vector3.zero;

        if (isBonus)
            _bonusPool.Enqueue(orb);
        else
            _pool.Enqueue(orb);

        _activeCount = Mathf.Max(0, _activeCount - 1);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ЕДИНЫЙ ЦИКЛ ДВИЖЕНИЯ — заменяет 1000 отдельных Update() в ExperienceOrb
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        float dt = Time.deltaTime;
        // Итерируемся с конца чтобы безопасно удалять через ReturnOrb внутри цикла
        for (int i = _activeOrbScripts.Count - 1; i >= 0; i--)
        {
            ExperienceOrb orb = _activeOrbScripts[i];
            if (orb == null || !orb.IsAttracting) continue;

            Transform target = orb.Target;
            if (target == null)
            {
                orb.ResetOrb();
                continue;
            }

            Vector3 targetPos = target.position;
            Vector3 orbPos = orb.transform.position;

            orb.transform.position = Vector3.MoveTowards(orbPos, targetPos, orb.speed * dt);

            // sqrMagnitude вместо Distance — избегаем sqrt
            if ((orb.transform.position - targetPos).sqrMagnitude < 0.04f) // 0.2f²
            {
                orb.GiveXP();
                ReturnOrb(orb.gameObject);
            }
        }
    }

    /// <summary>
    /// Притянуть ВСЕ активные орбы к Transform игрока (отслеживает позицию в реальном времени).
    /// Вызывается из ItemInstance (Чёрная Дыра) каждые cooldownOrDuration секунд.
    ///
    /// ФИКС БАГ 2: добавлен параметр ownerClientId.
    /// В Competitive режиме каждый игрок должен притягивать только СВОИ орбы.
    /// ulong.MaxValue = без фильтра (SinglePlayer / legacy).
    /// </summary>
    public void AttractAll(Transform playerTransform, ulong ownerClientId = ulong.MaxValue)
    {
        if (playerTransform == null) return;

        // Итерируемся по копии списка чтобы избежать модификации во время обхода
        var copy = new List<GameObject>(_activeOrbs);
        int attracted = 0;
        foreach (var orb in copy)
        {
            if (orb == null || !orb.activeSelf) continue;
            var script = orb.GetComponent<ExperienceOrb>();
            if (script == null) continue;

            // ФИКС БАГ 2: фильтр по владельцу
            // ulong.MaxValue у орба = без владельца → притягиваем всегда
            // Иначе: орб должен принадлежать вызывающему игроку
            if (ownerClientId != ulong.MaxValue
                && script.OwnerClientId != ulong.MaxValue
                && script.OwnerClientId != ownerClientId)
                continue;

            script.StartAttract(playerTransform);
            attracted++;
        }
        if (attracted > 0)
            Debug.Log($"[ExperienceOrbPool] 🌀 Чёрная дыра: притягивает {attracted} орбов");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОЧИСТКА
    // ─────────────────────────────────────────────────────────────────────────

    public void CleanupDestroyedOrbs()
    {
        CleanQueue(_pool);
        CleanQueue(_bonusPool);

        // Синхронизируем оба списка — они должны всегда быть одинаковой длины
        for (int i = _activeOrbs.Count - 1; i >= 0; i--)
        {
            if (_activeOrbs[i] == null)
            {
                _activeOrbs.RemoveAt(i);
                _activeOrbScripts.RemoveAt(i);
            }
        }

        _activeCount = _activeOrbs.Count;
        Debug.Log("[ExperienceOrbPool] Очистка завершена.");
    }

    private void CleanQueue(Queue<GameObject> queue)
    {
        int before = queue.Count;
        var alive = new Queue<GameObject>();
        while (queue.Count > 0)
        {
            var obj = queue.Dequeue();
            if (obj != null) alive.Enqueue(obj);
        }
        while (alive.Count > 0) queue.Enqueue(alive.Dequeue());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СТАТИСТИКА
    // ─────────────────────────────────────────────────────────────────────────

    public int GetActiveOrbCount() => _activeCount;
    public int GetPooledOrbCount() => _pool.Count;
    public int GetBonusPooledCount() => _bonusPool.Count;
}
